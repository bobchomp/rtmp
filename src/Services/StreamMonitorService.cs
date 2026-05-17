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

    private readonly HashSet<string> _activePaths = [];

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

                        if (hasPublisher)
                            currentPaths.Add(pathName);
                    }
                }

                // Fire started events for newly active paths
                foreach (var path in currentPaths.Except(_activePaths))
                {
                    // Match against a configured key, or create a synthetic one for
                    // unrecognised paths (e.g. path format mismatch from the encoder)
                    var key = settings.StreamKeys.FirstOrDefault(k => k.RtmpPath == path)
                              ?? new StreamKey
                              {
                                  Name         = path,
                                  Key          = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path,
                                  ExplicitPath = path,
                                  IsActive     = true
                              };
                    key.IsActive = true;
                    StreamStarted?.Invoke(key);
                }

                // Fire stopped events for paths that went offline
                foreach (var path in _activePaths.Except(currentPaths))
                {
                    var key = settings.StreamKeys.FirstOrDefault(k => k.RtmpPath == path);
                    if (key != null) key.IsActive = false;
                    // Always fire — even for synthetic keys the projection window should close
                    StreamStopped?.Invoke(key ?? new StreamKey { ExplicitPath = path });
                }

                _activePaths.Clear();
                foreach (var p in currentPaths) _activePaths.Add(p);
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
