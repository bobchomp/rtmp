using System.IO;
using System.Net.Http;
using System.Text.Json;
using RTMPProjector.Models;

namespace RTMPProjector.Services;

/// <summary>
/// Polls the MediaMTX HTTP API to detect when streams go live or go offline.
/// </summary>
public class StreamMonitorService : IAsyncDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    public event Action<StreamKey>? StreamStarted;
    public event Action<StreamKey>? StreamStopped;
    public event Action<string>? LogMessage;

    private readonly HashSet<string> _activePaths = [];

    // Feature 2: Track bytes received for bitrate calculation
    private readonly Dictionary<string, long> _lastBytesReceived = new();

    public void Start(AppSettings settings)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _pollTask = PollLoopAsync(settings, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _activePaths.Clear();
        _lastBytesReceived.Clear();
    }

    private async Task PollLoopAsync(AppSettings settings, CancellationToken ct)
    {
        var apiBase = $"http://localhost:{settings.ApiPort}";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);

                var json = await _http.GetStringAsync($"{apiBase}/v3/paths/list", ct);
                using var doc = JsonDocument.Parse(json);

                var currentPaths = new HashSet<string>();
                if (doc.RootElement.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (!item.TryGetProperty("name", out var nameProp)) continue;
                        var pathName = nameProp.GetString() ?? "";

                        bool hasPublisher = item.TryGetProperty("source", out var src) &&
                                           src.ValueKind != JsonValueKind.Null;

                        // Feature 2: Extract bytesReceived for bitrate — no per-poll logging
                        if (hasPublisher)
                        {
                            currentPaths.Add(pathName);

                            // Try to extract bytesReceived from the source or from the item itself
                            long bytesReceived = 0;
                            if (item.TryGetProperty("bytesReceived", out var br))
                                bytesReceived = br.GetInt64();

                            var key = settings.StreamKeys.FirstOrDefault(k => k.RtmpPath == pathName);
                            if (key != null)
                            {
                                if (_lastBytesReceived.TryGetValue(pathName, out var prevBytes))
                                {
                                    var delta = bytesReceived - prevBytes;
                                    if (delta >= 0)
                                        key.BitrateKbps = delta * 8.0 / 2000.0; // bytes over 2s interval → kbps
                                }
                                _lastBytesReceived[pathName] = bytesReceived;
                            }
                        }
                    }
                }

                // Feature 2: Only log on state changes (not every poll)
                // Fire started events for newly active paths
                foreach (var path in currentPaths.Except(_activePaths))
                {
                    var key = settings.StreamKeys.FirstOrDefault(k => k.RtmpPath == path)
                              ?? new StreamKey
                              {
                                  Name         = path,
                                  Key          = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path,
                                  ExplicitPath = path,
                                  IsActive     = true
                              };
                    key.IsActive = true;
                    LogMessage?.Invoke($"[Monitor] Stream STARTED: {path}");
                    StreamStarted?.Invoke(key);
                }

                // Fire stopped events for paths that went offline
                foreach (var path in _activePaths.Except(currentPaths))
                {
                    var key = settings.StreamKeys.FirstOrDefault(k => k.RtmpPath == path);
                    if (key != null)
                    {
                        key.IsActive = false;
                        key.BitrateKbps = 0;
                        key.IsRecording = false;
                    }
                    _lastBytesReceived.Remove(path);
                    LogMessage?.Invoke($"[Monitor] Stream STOPPED: {path}");
                    // Always fire — even for synthetic keys the projection window should close
                    StreamStopped?.Invoke(key ?? new StreamKey { ExplicitPath = path });
                }

                _activePaths.Clear();
                foreach (var p in currentPaths) _activePaths.Add(p);

                // Feature 4: Recording status — check file write times for active recording keys
                if (!string.IsNullOrEmpty(settings.RecordingPath))
                {
                    foreach (var key in settings.StreamKeys)
                    {
                        if (!key.IsActive || !key.RecordEnabled)
                        {
                            if (key.IsRecording) key.IsRecording = false;
                            continue;
                        }

                        // Check if a file in the recording folder was written in the last 5s
                        var recDir = Path.Combine(settings.RecordingPath,
                            key.RtmpPath.Replace('/', Path.DirectorySeparatorChar));
                        bool isRec = false;
                        if (Directory.Exists(recDir))
                        {
                            var cutoff = DateTime.Now.AddSeconds(-5);
                            isRec = Directory.EnumerateFiles(recDir)
                                .Any(f =>
                                {
                                    try { return File.GetLastWriteTime(f) >= cutoff; }
                                    catch { return false; }
                                });
                        }
                        key.IsRecording = isRec;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* server not yet ready or network hiccup — retry next cycle */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_pollTask != null) await _pollTask.ConfigureAwait(false);
        _http.Dispose();
    }
}
