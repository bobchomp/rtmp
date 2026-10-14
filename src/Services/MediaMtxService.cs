using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using RTMPProjector.Models;

namespace RTMPProjector.Services;

public class MediaMtxService : IAsyncDisposable
{
    private const string MtxApiUrl  = "https://api.github.com/repos/bluenviron/mediamtx/releases/latest";
    private const string MtxExeName = "mediamtx.exe";

    private static readonly string AppDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RTMPProjector");

    // Check the app's install directory first (bundled by the installer), then AppData
    private static string MtxExePath =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, MtxExeName))
            ? Path.Combine(AppContext.BaseDirectory, MtxExeName)
            : Path.Combine(AppDir, MtxExeName);
    private static readonly string MtxConfigPath = Path.Combine(AppDir, "mediamtx.yml");
    private static readonly string MtxLogPath    = Path.Combine(AppDir, "mediamtx.log");

    private Process? _process;
    private readonly HttpClient _http;

    public MediaMtxService()
    {
        _http = new HttpClient();
        // GitHub API requires a User-Agent header
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("RTMPProjector", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public bool IsRunning => _process is { HasExited: false };

    public event Action<string>? LogMessage;

    public async Task<bool> EnsureBinaryAsync(IProgress<string>? progress = null)
    {
        if (File.Exists(MtxExePath))
            return true;

        Directory.CreateDirectory(AppDir);

        try
        {
            // Resolve the latest release URL dynamically via the GitHub API
            progress?.Report("Looking up latest MediaMTX release...");
            var (downloadUrl, sha256) = await ResolveDownloadUrlAsync();
            progress?.Report($"Downloading MediaMTX from GitHub...");

            var zipPath = Path.Combine(AppDir, "mediamtx.zip");

            // Stream the download so large files don't sit in memory
            using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (var fs = File.Create(zipPath))
                await response.Content.CopyToAsync(fs);

            try
            {
                if (sha256 != null)
                    await DownloadVerifier.VerifySha256Async(zipPath, sha256);
                else
                    LogMessage?.Invoke("[mtx] Release has no published checksum — continuing without verification.");

                progress?.Report("Extracting MediaMTX...");
                DownloadVerifier.ExtractEntry(zipPath, MtxExeName, Path.Combine(AppDir, MtxExeName));
            }
            finally
            {
                File.Delete(zipPath);
            }

            progress?.Report("MediaMTX ready.");
            return true;
        }
        catch (Exception ex)
        {
            var msg = $"MediaMTX download failed: {ex.Message}";
            progress?.Report(msg);
            LogMessage?.Invoke(msg);
            return false;
        }
    }

    /// Download URL of the Windows zip plus its SHA-256 from GitHub's "digest" field (null if absent).
    private async Task<(string Url, string? Sha256)> ResolveDownloadUrlAsync()
    {
        var json = await _http.GetStringAsync(MtxApiUrl);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("assets", out var assets))
            throw new InvalidOperationException("GitHub API response missing 'assets' field.");

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            // Match the windows amd64 zip, e.g. mediamtx_v1.9.1_windows_amd64.zip
            if (name.Contains("windows_amd64", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var url = asset.GetProperty("browser_download_url").GetString()
                    ?? throw new InvalidOperationException("Asset has no download URL.");
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
                return (url, digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null);
            }
        }

        throw new InvalidOperationException(
            "No Windows AMD64 zip found in the latest MediaMTX release. " +
            "Check https://github.com/bluenviron/mediamtx/releases/latest manually.");
    }

    // Config last written, and the ports the running process was started with —
    // lets ApplyConfigAsync hot-reload path changes instead of restarting.
    private string? _lastConfig;
    private int _runningRtmpPort;
    private int _runningApiPort;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private static string BuildConfig(AppSettings settings)
    {
        var pathsYaml = BuildPathsYaml(settings);

        // API is bound to localhost only — nothing else on the network needs it
        return $"""
            ###############################################
            # RTMPProjector - MediaMTX configuration
            ###############################################

            logLevel: warn
            logDestinations: [file, stdout]
            logFile: {Quote(MtxLogPath.Replace('\\', '/'))}

            api: yes
            apiAddress: 127.0.0.1:{settings.ApiPort}

            rtmp: yes
            rtmpAddress: :{settings.RtmpPort}
            rtmpEncryption: "no"

            rtsp: no
            hls: no
            webrtc: no
            srt: no

            paths:
            {pathsYaml}
            """;
    }

    private void WriteConfig(AppSettings settings, string config)
    {
        Directory.CreateDirectory(AppDir);
        if (!string.IsNullOrWhiteSpace(settings.RecordingPath))
            Directory.CreateDirectory(settings.RecordingPath);

        // Write-then-rename so MediaMTX's file watcher never reads a half-written file
        var tmp = MtxConfigPath + ".tmp";
        File.WriteAllText(tmp, config);
        File.Move(tmp, MtxConfigPath, overwrite: true);
        _lastConfig = config;
    }

    private static string BuildPathsYaml(AppSettings settings)
    {
        var lines = new System.Text.StringBuilder();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in settings.StreamKeys)
        {
            // Duplicate path names make MediaMTX reject the whole config
            if (string.IsNullOrWhiteSpace(key.Key) || !seen.Add(key.RtmpPath)) continue;

            // Every value is single-quoted so stray characters (#, :, quotes) in a
            // key or folder name can't break or inject into the YAML
            lines.AppendLine($"  {Quote(key.RtmpPath)}:");

            if (key.RecordEnabled && !string.IsNullOrWhiteSpace(settings.RecordingPath))
            {
                // %path is required by MediaMTX and expands to the stream path (e.g. live/abc123)
                var recPath = Path.Combine(settings.RecordingPath, "%path", "%Y-%m-%d_%H-%M-%S-%f")
                                  .Replace('\\', '/');
                lines.AppendLine($"    record: yes");
                lines.AppendLine($"    recordPath: {Quote(recPath)}");
                lines.AppendLine($"    recordFormat: fmp4");
            }
        }

        // Catch-all: accept any publisher path that wasn't explicitly listed above, so
        // DJI / third-party encoders that format the path differently still connect.
        // Such streams are logged but never auto-projected (see App.OnStreamBecameActive).
        lines.AppendLine("  ~^.*$:");

        return lines.ToString().TrimEnd();
    }

    public async Task StartAsync(AppSettings settings)
    {
        await _lifecycleLock.WaitAsync();
        try { await StartCoreAsync(settings); }
        finally { _lifecycleLock.Release(); }
    }

    private async Task StartCoreAsync(AppSettings settings)
    {
        if (IsRunning) return;

        if (!File.Exists(MtxExePath))
            throw new FileNotFoundException("MediaMTX binary not found. Run EnsureBinaryAsync first.");

        WriteConfig(settings, BuildConfig(settings));
        KillOrphanedInstances();

        var psi = new ProcessStartInfo
        {
            FileName = MtxExePath,
            Arguments = $"\"{MtxConfigPath}\"",
            WorkingDirectory = AppDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) LogMessage?.Invoke($"[mtx] {e.Data}"); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) LogMessage?.Invoke($"[mtx] {e.Data}"); };
        proc.Exited += (_, _) => LogMessage?.Invoke("MediaMTX process exited.");

        try
        {
            proc.Start();
        }
        catch
        {
            proc.Dispose();
            throw;
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _process = proc;
        _runningRtmpPort = settings.RtmpPort;
        _runningApiPort  = settings.ApiPort;

        // Give it a moment to bind the port, then make sure it didn't immediately die
        // (bad config, port already in use, ...) — the reason is in the [mtx] log lines
        await Task.Delay(1000);
        if (proc.HasExited)
        {
            _process = null;
            proc.Dispose();
            throw new InvalidOperationException(
                "MediaMTX exited straight after starting — see the Server tab log (is the port already in use?).");
        }
    }

    // Only kill mediamtx.exe copies that are ours (left over from a crash) — never
    // another application's MediaMTX server.
    private void KillOrphanedInstances()
    {
        var ours = new[] { Path.Combine(AppContext.BaseDirectory, MtxExeName), Path.Combine(AppDir, MtxExeName) };
        foreach (var stale in Process.GetProcessesByName("mediamtx"))
        {
            try
            {
                var exe = stale.MainModule?.FileName;
                if (exe != null && ours.Any(o => string.Equals(Path.GetFullPath(o), exe, StringComparison.OrdinalIgnoreCase)))
                {
                    stale.Kill(entireProcessTree: true);
                    stale.WaitForExit(2000);
                    LogMessage?.Invoke("Stopped a leftover MediaMTX process from a previous session.");
                }
            }
            catch { /* can't inspect it (other user / elevated) — not ours to kill */ }
            finally { stale.Dispose(); }
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycleLock.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var proc = _process;
        _process = null;
        _lastConfig = null;
        if (proc == null) return;

        if (!proc.HasExited)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch { /* already gone */ }

            await proc.WaitForExitAsync();
        }
        proc.Dispose();
    }

    /// <summary>
    /// Applies settings to the running server. Path changes (keys added/removed,
    /// recording toggled) are written to the config file, which MediaMTX hot-reloads
    /// without dropping other streams. Only a port change needs a full restart.
    /// Returns true if the server was restarted.
    /// </summary>
    public async Task<bool> ApplyConfigAsync(AppSettings settings)
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (!IsRunning) return false;

            var config = BuildConfig(settings);
            if (config == _lastConfig) return false;

            if (settings.RtmpPort == _runningRtmpPort && settings.ApiPort == _runningApiPort)
            {
                WriteConfig(settings, config);
                LogMessage?.Invoke("[mtx] Configuration updated (live reload — streams keep running).");
                return false;
            }

            LogMessage?.Invoke("[mtx] Port changed — restarting the RTMP server.");
            await StopCoreAsync();
            await StartCoreAsync(settings);
            return true;
        }
        finally { _lifecycleLock.Release(); }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }
}
