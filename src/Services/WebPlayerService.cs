using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using RTMPProjector.Models;

namespace RTMPProjector.Services;

/// <summary>
/// Minimal HTTP server that serves the live player website.
/// Runs on localhost:{PlayerPort} — exposed to the internet via Cloudflare Tunnel.
/// config.js is generated live from current app settings so the website always
/// reflects the active stream key, title, and password without any manual editing.
/// All /live/* HLS requests are reverse-proxied internally to MediaMTX so the
/// player and stream are on the same origin — no CORS needed.
/// </summary>
public class WebPlayerService : IDisposable
{
    private static readonly string WebDir =
        Path.Combine(AppContext.BaseDirectory, "web");

    // Reusable client for proxying HLS segments to MediaMTX on localhost
    private static readonly HttpClient _hlsClient = new HttpClient();

    private readonly HttpListener _listener = new();
    private AppSettings? _settings;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<string>? LogMessage;

    public bool IsRunning { get; private set; }

    public void Start(AppSettings settings)
    {
        if (IsRunning) return;

        _settings = settings;

        _listener.Prefixes.Clear();
        _listener.Prefixes.Add($"http://localhost:{settings.PlayerPort}/");

        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[web-player] Failed to start on port {settings.PlayerPort}: {ex.Message}");
            return;
        }

        IsRunning = true;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ListenLoop(_cts.Token));
        LogMessage?.Invoke($"[web-player] Listening on http://localhost:{settings.PlayerPort}/");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        _listener.Stop();
        _loop?.Wait(TimeSpan.FromSeconds(2));
        IsRunning = false;
        LogMessage?.Invoke("[web-player] Stopped.");
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequest(ctx), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch { }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";

            // Proxy all HLS traffic to MediaMTX internally so the player and
            // stream share the same origin — no cross-origin / CORS issues.
            if (path.StartsWith("/live/", StringComparison.OrdinalIgnoreCase))
            {
                ProxyHls(ctx);
                return;
            }

            var trimmed = path.TrimEnd('/');
            if (trimmed == "") trimmed = "/";

            switch (trimmed)
            {
                case "/":
                case "/index.html":
                    ServeFile(ctx.Response, "index.html", "text/html; charset=utf-8");
                    break;
                case "/style.css":
                    ServeFile(ctx.Response, "style.css", "text/css; charset=utf-8");
                    break;
                case "/player.js":
                    ServeFile(ctx.Response, "player.js", "application/javascript; charset=utf-8");
                    break;
                case "/config.js":
                    ServeConfigJs(ctx.Response);
                    break;
                default:
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    break;
            }
        }
        catch { ctx.Response.Abort(); }
    }

    private void ProxyHls(HttpListenerContext ctx)
    {
        var s = _settings;
        if (s == null) { ctx.Response.StatusCode = 503; ctx.Response.Close(); return; }

        var path = ctx.Request.Url!.PathAndQuery;
        var targetUrl = $"http://localhost:{s.HlsPort}{path}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
            using var resp = _hlsClient.Send(req, HttpCompletionOption.ResponseHeadersRead);

            var status = (int)resp.StatusCode;
            ctx.Response.StatusCode = status;

            var ct = resp.Content.Headers.ContentType?.ToString();
            if (!string.IsNullOrEmpty(ct))
                ctx.Response.ContentType = ct;

            if (resp.Content.Headers.ContentLength is long len)
                ctx.Response.ContentLength64 = len;
            else
                ctx.Response.SendChunked = true;

            // Only log non-200 responses for manifests so the log isn't spammed
            if (status != 200 && path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                LogMessage?.Invoke($"[hls-proxy] {path} → {status}");

            using var body = resp.Content.ReadAsStream();
            body.CopyTo(ctx.Response.OutputStream);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[hls-proxy] ERROR proxying {path}: {ex.Message}");
            try { ctx.Response.StatusCode = 503; } catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private void ServeConfigJs(HttpListenerResponse resp)
    {
        var s = _settings;
        if (s == null) { resp.StatusCode = 503; resp.Close(); return; }

        // Root-relative URL — resolves to the same origin as the page regardless
        // of whether it's served over HTTP or HTTPS. Never a CORS issue.
        var first = s.StreamKeys.FirstOrDefault();
        var hlsUrl = first != null
            ? $"/live/{first.Key}/index.m3u8"
            : "";
        var title    = first?.Name ?? "Live Stream";
        var password = s.WebStreamPassword ?? "";

        var js = $$"""
            // Auto-generated by RTMP Projector — do not edit manually
            window.STREAM_CONFIG = {
              hlsUrl:        '{{EscJs(hlsUrl)}}',
              title:         '{{EscJs(title)}}',
              password:      '{{EscJs(password)}}',
              retryInterval: 10,
            };
            """;

        WriteText(resp, js, "application/javascript; charset=utf-8");
    }

    private static void ServeFile(HttpListenerResponse resp, string filename, string contentType)
    {
        var path = Path.Combine(WebDir, filename);
        if (!File.Exists(path)) { resp.StatusCode = 404; resp.Close(); return; }

        var bytes = File.ReadAllBytes(path);
        resp.ContentType = contentType;
        resp.AddHeader("Cache-Control", "no-store");
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes);
        resp.Close();
    }

    private static void WriteText(HttpListenerResponse resp, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        resp.ContentType = contentType;
        resp.AddHeader("Cache-Control", "no-store");
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes);
        resp.Close();
    }

    private static string EscJs(string s) =>
        s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n");

    public void Dispose() => Stop();
}
