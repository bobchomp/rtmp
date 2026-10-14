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

    public void Start(AppSettings settings)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _pollTask = PollLoopAsync(settings, _cts.Token);
    }

    /// Stops polling. All per-run state lives inside the loop, so a poll that is
    /// still finishing can't clash with a new Start(), and it fires no more events.
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task PollLoopAsync(AppSettings settings, CancellationToken ct)
    {
        // Read once per run — the view model restarts the monitor when the API port changes
        var apiBase = $"http://127.0.0.1:{settings.ApiPort}";

        var activePaths       = new HashSet<string>();
        var lastBytesReceived = new Dictionary<string, long>();   // Feature 2: bitrate
        // Stand-in keys for paths that match no configured key — kept so the
        // "stopped" event carries the same object the "started" event did
        var standIns          = new Dictionary<string, StreamKey>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);

                var json = await _http.GetStringAsync($"{apiBase}/v3/paths/list", ct);
                using var doc = JsonDocument.Parse(json);

                // Snapshot — the UI thread may add/remove keys while we poll
                var keys = settings.StreamKeys.ToArray();
                StreamKey? Find(string path) => keys.FirstOrDefault(k => k.RtmpPath == path);

                var currentPaths = new HashSet<string>();
                if (doc.RootElement.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (!item.TryGetProperty("name", out var nameProp)) continue;
                        var pathName = nameProp.GetString() ?? "";

                        bool hasPublisher = item.TryGetProperty("source", out var src) &&
                                           src.ValueKind != JsonValueKind.Null;
                        if (!hasPublisher) continue;

                        currentPaths.Add(pathName);

                        long bytesReceived = 0;
                        if (item.TryGetProperty("bytesReceived", out var br))
                            bytesReceived = br.GetInt64();

                        var key = Find(pathName);
                        if (key == null) continue;

                        key.HasAudio = DetectAudio(item);
                        if (lastBytesReceived.TryGetValue(pathName, out var prevBytes))
                        {
                            var delta = bytesReceived - prevBytes;
                            if (delta >= 0)
                                key.BitrateKbps = delta * 8.0 / 2000.0; // bytes over 2s interval → kbps
                        }
                        lastBytesReceived[pathName] = bytesReceived;
                    }
                }

                if (ct.IsCancellationRequested) break;

                // Only log on state changes (not every poll)
                foreach (var path in currentPaths.Except(activePaths).ToList())
                {
                    var key = Find(path);
                    if (key == null)
                    {
                        key = new StreamKey
                        {
                            Name         = path,
                            Key          = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path,
                            ExplicitPath = path,
                        };
                        standIns[path] = key;
                    }
                    key.IsActive = true;
                    LogMessage?.Invoke($"[Monitor] Stream STARTED: {path}");
                    StreamStarted?.Invoke(key);
                }

                foreach (var path in activePaths.Except(currentPaths).ToList())
                {
                    var key = Find(path);
                    if (key == null) standIns.Remove(path, out key);
                    key ??= new StreamKey { ExplicitPath = path };

                    key.IsActive    = false;
                    key.BitrateKbps = 0;
                    key.IsRecording = false;
                    lastBytesReceived.Remove(path);
                    LogMessage?.Invoke($"[Monitor] Stream STOPPED: {path}");
                    StreamStopped?.Invoke(key);
                }

                activePaths = currentPaths;

                // Feature 4: Recording status — check file write times for active recording keys
                if (!string.IsNullOrEmpty(settings.RecordingPath))
                {
                    foreach (var key in keys)
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

    // MediaMTX lists codecs as "tracks": ["H264", "MPEG-4 Audio"] (older) and/or
    // "tracks2": [{ "codec": "H264" }, ...] (newer). Null = couldn't tell.
    private static bool? DetectAudio(JsonElement item)
    {
        var codecs = new List<string>();
        if (item.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
            foreach (var t in tracks.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String) codecs.Add(t.GetString() ?? "");
        if (item.TryGetProperty("tracks2", out var tracks2) && tracks2.ValueKind == JsonValueKind.Array)
            foreach (var t in tracks2.EnumerateArray())
                if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("codec", out var c))
                    codecs.Add(c.GetString() ?? "");

        if (codecs.Count == 0) return null;

        string[] audioCodecs = ["Audio", "AAC", "Opus", "G711", "G722", "LPCM", "AC-3", "MP3", "Speex"];
        return codecs.Any(c => audioCodecs.Any(a => c.Contains(a, StringComparison.OrdinalIgnoreCase)));
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_pollTask != null) await _pollTask.ConfigureAwait(false);
        _http.Dispose();
    }
}
