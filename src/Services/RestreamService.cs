using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using RTMPProjector.Models;

namespace RTMPProjector.Services;

/// <summary>
/// "Go Live": pushes a drone stream from the local MediaMTX server out to
/// YouTube / Facebook / Twitch / any RTMP(S) server using FFmpeg.
///
/// Once the user presses Go Live a key stays "live" until they press End Live —
/// if the drone drops out, FFmpeg is restarted automatically when it reconnects.
/// </summary>
public sealed class RestreamService : IDisposable
{
    private const string FfmpegExeName = "ffmpeg.exe";

    // Both zips contain <folder>/bin/ffmpeg.exe; the second is a fallback mirror
    private static readonly string[] FfmpegZipUrls =
    [
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
    ];

    // Give up after this many attempts in a row that never got as far as sending video
    private const int MaxFailedAttempts = 3;

    private static readonly string AppDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RTMPProjector");

    private readonly Action<Action> _uiInvoke;
    private readonly Dictionary<string, Session> _sessions = [];
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    public event Action<string>? LogMessage;

    /// <param name="uiInvoke">Marshals key-status updates onto the UI thread.</param>
    public RestreamService(Action<Action> uiInvoke) => _uiInvoke = uiInvoke;

    private sealed class Session
    {
        public required StreamKey Key;
        public required string SourceUrl;
        public Process? Process;
        public bool SentVideo;
        public int FailedAttempts;
        public string LastError = "";
        public CancellationTokenSource? RetryCts;
    }

    // ── Public API (call from the UI thread) ──────────────────────────────────

    public async Task GoLiveAsync(StreamKey key, string sourceUrl)
    {
        if (_sessions.ContainsKey(key.Id)) return;
        if (!key.HasRestreamDestination)
        {
            key.LiveStatus = "Enter a server URL and stream key first.";
            return;
        }

        var session = new Session { Key = key, SourceUrl = sourceUrl };
        _sessions[key.Id] = session;
        key.IsLive = true;
        Log($"[Go Live] {key.Name} → {key.RestreamPlatform} ({key.RestreamServer})");

        if (FindFfmpeg() == null)
        {
            key.LiveStatus = "Downloading FFmpeg (one-time, ~100 MB)…";
            if (!await EnsureFfmpegAsync() || !_sessions.ContainsKey(key.Id))
            {
                if (_sessions.ContainsKey(key.Id)) EndLive(key, "FFmpeg download failed — see Server tab log.");
                return;
            }
        }

        if (key.IsActive)
            Launch(session);
        else
            key.LiveStatus = "Waiting for drone stream…";
    }

    public void EndLive(StreamKey key, string status = "")
    {
        if (!_sessions.Remove(key.Id, out var session)) return;
        session.RetryCts?.Cancel();
        Kill(session);
        key.IsLive = false;
        key.LiveStatus = status;
        Log($"[Go Live] {key.Name} ended.{(status.Length > 0 ? " " + status : "")}");
    }

    /// Drone (re)connected — resume pushing if this key is live.
    public void OnStreamStarted(StreamKey key)
    {
        if (!_sessions.TryGetValue(key.Id, out var session) || session.Process != null) return;
        if (FindFfmpeg() == null) return;   // still downloading — GoLiveAsync launches when done
        session.RetryCts?.Cancel();
        session.FailedAttempts = 0;
        Launch(session);
    }

    /// Drone disconnected — stay live, wait for it to come back.
    public void OnStreamStopped(StreamKey key)
    {
        if (!_sessions.TryGetValue(key.Id, out var session)) return;
        session.RetryCts?.Cancel();
        Kill(session);
        key.LiveStatus = "Drone disconnected — waiting for it to reconnect…";
    }

    public void EndAll()
    {
        foreach (var session in _sessions.Values.ToList())
            EndLive(session.Key);
    }

    // ── FFmpeg process ────────────────────────────────────────────────────────

    private void Launch(Session session)
    {
        var key = session.Key;
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
        {
            EndLive(key, "FFmpeg not found.");
            return;
        }

        session.SentVideo = false;
        session.LastError = "";
        key.LiveStatus = "Connecting…";

        var psi = new ProcessStartInfo
        {
            FileName               = ffmpeg,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardError  = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in BuildArgs(session.SourceUrl, key.RestreamDestination, key.HasAudio))
            psi.ArgumentList.Add(arg);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) OnFfmpegLine(session, proc, e.Data); };
        proc.Exited += (_, _) => _uiInvoke(() => OnFfmpegExited(session, proc));

        try
        {
            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            session.Process = proc;
            if (key.HasAudio == false)
                Log($"[Go Live] {key.Name}: drone has no audio — adding a silent track (YouTube/Facebook require one).");
        }
        catch (Exception ex)
        {
            proc.Dispose();
            EndLive(key, $"Couldn't start FFmpeg: {ex.Message}");
        }
    }

    private static List<string> BuildArgs(string source, string destination, bool? hasAudio)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-stats",
            "-rw_timeout", "10000000",              // 10 s — notice a dead input quickly
            "-i", source,
        };

        if (hasAudio == false)
        {
            // Platforms reject (or misbehave on) video-only streams — add silence
            args.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=44100",
                           "-map", "0:v:0", "-map", "1:a:0",
                           "-c:v", "copy", "-c:a", "aac", "-b:a", "128k", "-shortest"]);
        }
        else
        {
            args.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-c", "copy"]);
        }

        args.AddRange(["-f", "flv", "-flvflags", "no_duration_filesize", destination]);
        return args;
    }

    // Called on a background thread for every stderr line
    private void OnFfmpegLine(Session session, Process proc, string line)
    {
        // "-stats" progress lines look like: frame= 123 fps= 30 ... / size= 1024kB time=...
        if (line.StartsWith("frame=") || line.StartsWith("size="))
        {
            if (session.SentVideo) return;
            session.SentVideo = true;
            _uiInvoke(() =>
            {
                if (session.Process != proc) return;
                session.FailedAttempts = 0;
                session.Key.LiveStatus = $"● LIVE on {session.Key.RestreamPlatform}";
                Log($"[Go Live] {session.Key.Name} is LIVE on {session.Key.RestreamPlatform}.");
            });
            return;
        }

        var clean = Mask(line, session.Key.RestreamStreamKey);
        session.LastError = clean;
        Log($"[ffmpeg] {clean}");
    }

    private void OnFfmpegExited(Session session, Process proc)
    {
        var code = proc.ExitCode;
        proc.Dispose();
        if (session.Process != proc) return;   // we killed it on purpose
        session.Process = null;

        var key = session.Key;
        if (!_sessions.ContainsKey(key.Id)) return;

        Log($"[Go Live] {key.Name}: FFmpeg exited (code {code}).");

        if (!key.IsActive)
        {
            key.LiveStatus = "Drone disconnected — waiting for it to reconnect…";
            return;
        }

        if (!session.SentVideo && ++session.FailedAttempts >= MaxFailedAttempts)
        {
            var reason = string.IsNullOrEmpty(session.LastError) ? $"exit code {code}" : session.LastError;
            EndLive(key, $"Couldn't connect to {key.RestreamPlatform}: {reason}. Check the server URL and stream key.");
            return;
        }

        // Network blip / platform hiccup — try again shortly
        var delay = TimeSpan.FromSeconds(Math.Min(30, 3 * Math.Pow(2, session.FailedAttempts)));
        key.LiveStatus = $"Connection lost — retrying in {delay.TotalSeconds:0}s…";
        var cts = new CancellationTokenSource();
        session.RetryCts = cts;
        _ = Task.Delay(delay, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _uiInvoke(() =>
            {
                if (_sessions.ContainsKey(key.Id) && session.Process == null && key.IsActive)
                    Launch(session);
            });
        });
    }

    private static void Kill(Session session)
    {
        var proc = session.Process;
        session.Process = null;
        if (proc == null) return;
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
    }

    private static string Mask(string text, string secret) =>
        string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "****");

    private void Log(string msg) => LogMessage?.Invoke(msg);

    // ── FFmpeg discovery / download ───────────────────────────────────────────

    /// Bundled next to the exe by the installer, else downloaded to AppData, else on PATH.
    public static string? FindFfmpeg()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, FfmpegExeName),
            Path.Combine(AppDir, FfmpegExeName),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try { candidates.Add(Path.Combine(dir.Trim(), FfmpegExeName)); } catch { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task<bool> EnsureFfmpegAsync()
    {
        await _downloadLock.WaitAsync();
        try
        {
            if (FindFfmpeg() != null) return true;

            Directory.CreateDirectory(AppDir);
            var zipPath = Path.Combine(AppDir, "ffmpeg.zip");
            Log("[Go Live] FFmpeg not found — downloading it (one-time)…");

            Exception? lastError = null;
            foreach (var url in FfmpegZipUrls)
            {
                try
                {
                    await DownloadAndExtractAsync(url, zipPath);
                    lastError = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Log($"[Go Live] Download from {new Uri(url).Host} failed: {ex.Message}");
                }
                finally
                {
                    try { File.Delete(zipPath); } catch { }
                }
            }
            if (lastError != null) throw lastError;

            Log("[Go Live] FFmpeg ready.");
            return true;
        }
        catch (Exception ex)
        {
            Log($"[Go Live] FFmpeg download failed: {ex.Message}");
            return false;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private static async Task DownloadAndExtractAsync(string url, string zipPath)
    {
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RTMPProjector", "1.0"));
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var fs = File.Create(zipPath);
            await response.Content.CopyToAsync(fs);
        }

        // Pull out just bin/ffmpeg.exe — the rest of the zip isn't needed
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith("/bin/" + FfmpegExeName, StringComparison.OrdinalIgnoreCase));
        if (entry == null) throw new FileNotFoundException("ffmpeg.exe not found inside the download.");
        entry.ExtractToFile(Path.Combine(AppDir, FfmpegExeName), overwrite: true);
    }

    public void Dispose() => EndAll();
}
