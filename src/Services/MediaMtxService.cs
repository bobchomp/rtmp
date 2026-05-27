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
            var downloadUrl = await ResolveDownloadUrlAsync();
            progress?.Report($"Downloading MediaMTX from GitHub...");

            var zipPath = Path.Combine(AppDir, "mediamtx.zip");

            // Stream the download so large files don't sit in memory
            using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (var fs = File.Create(zipPath))
                await response.Content.CopyToAsync(fs);

            progress?.Report("Extracting MediaMTX...");
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, AppDir, overwriteFiles: true);
            File.Delete(zipPath);

            if (!File.Exists(MtxExePath))
                throw new FileNotFoundException(
                    $"mediamtx.exe not found after extraction. " +
                    $"Check {AppDir} — the zip may use a different folder layout.");

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

    private async Task<string> ResolveDownloadUrlAsync()
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
                return asset.GetProperty("browser_download_url").GetString()
                    ?? throw new InvalidOperationException("Asset has no download URL.");
            }
        }

        throw new InvalidOperationException(
            "No Windows AMD64 zip found in the latest MediaMTX release. " +
            "Check https://github.com/bluenviron/mediamtx/releases/latest manually.");
    }

    public void WriteConfig(AppSettings settings)
    {
        Directory.CreateDirectory(AppDir);
        if (!string.IsNullOrWhiteSpace(settings.RecordingPath))
            Directory.CreateDirectory(settings.RecordingPath);

        var pathsYaml = BuildPathsYaml(settings);

        var config = $"""
            ###############################################
            # RTMPProjector - MediaMTX configuration
            ###############################################

            logLevel: warn
            logDestinations: [file]
            logFile: {EscapeYaml(MtxLogPath)}

            api: yes
            apiAddress: :{settings.ApiPort}

            rtmp: yes
            rtmpAddress: :{settings.RtmpPort}
            rtmpEncryption: "no"

            hls: {(settings.WebStreamEnabled ? "yes" : "no")}
            hlsAddress: :{settings.HlsPort}
            hlsAllowOrigin: '*'
            hlsSegmentCount: 3
            hlsSegmentDuration: 1s
            webrtc: no
            srt: no

            paths:
            {pathsYaml}
            """;

        File.WriteAllText(MtxConfigPath, config);
    }

    private static string BuildPathsYaml(AppSettings settings)
    {
        var lines = new System.Text.StringBuilder();

        foreach (var key in settings.StreamKeys)
        {
            lines.AppendLine($"  {key.RtmpPath}:");

            if (key.RecordEnabled && !string.IsNullOrWhiteSpace(settings.RecordingPath))
            {
                // %path is required by MediaMTX and expands to the stream path (e.g. live/abc123)
                var recPath = Path.Combine(settings.RecordingPath, "%path", "%Y-%m-%d_%H-%M-%S-%f")
                                  .Replace('\\', '/');
                lines.AppendLine($"    record: yes");
                lines.AppendLine($"    recordPath: {recPath}");
                lines.AppendLine($"    recordFormat: fmp4");
            }

            if (key.RestreamEnabled && !string.IsNullOrWhiteSpace(key.RestreamUrl))
            {
                // Invoke ffmpeg to forward the stream when it goes live
                var restream = $"ffmpeg -re -i rtmp://localhost:{settings.RtmpPort}/{key.RtmpPath} -c copy -f flv {key.RestreamUrl}";
                lines.AppendLine($"    runOnReady: {restream}");
                lines.AppendLine($"    runOnReadyRestart: yes");
            }
        }

        // Catch-all: accept any publisher path that wasn't explicitly listed above.
        // This means DJI / third-party encoders that append the stream key differently
        // will still connect and be detected. The path appears in the diagnostics log.
        lines.AppendLine("  ~^.*$:");

        return lines.ToString().TrimEnd();
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;

        if (!File.Exists(MtxExePath))
            throw new FileNotFoundException("MediaMTX binary not found. Run EnsureBinaryAsync first.");

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

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) LogMessage?.Invoke(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) LogMessage?.Invoke($"ERR: {e.Data}"); };
        _process.Exited += (_, _) => LogMessage?.Invoke("MediaMTX process exited.");

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // Give it a moment to bind the port
        await Task.Delay(1000);
    }

    public async Task StopAsync()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }

            await _process.WaitForExitAsync();
        }
        _process?.Dispose();
        _process = null;
    }

    public async Task RestartAsync(AppSettings settings)
    {
        await StopAsync();
        WriteConfig(settings);
        await StartAsync();
    }

    private static string EscapeYaml(string path) => path.Replace('\\', '/');

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }
}
