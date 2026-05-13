using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using RTMPProjector.Models;

namespace RTMPProjector.Services;

public class MediaMtxService : IAsyncDisposable
{
    private const string MtxVersion = "v1.9.1";
    private const string MtxExeName = "mediamtx.exe";
    private const string MtxDownloadUrl =
        $"https://github.com/bluenviron/mediamtx/releases/download/{MtxVersion}/mediamtx_{MtxVersion}_windows_amd64.zip";

    private static readonly string AppDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RTMPProjector");

    private static readonly string MtxExePath = Path.Combine(AppDir, MtxExeName);
    private static readonly string MtxConfigPath = Path.Combine(AppDir, "mediamtx.yml");
    private static readonly string MtxLogPath = Path.Combine(AppDir, "mediamtx.log");

    private Process? _process;
    private readonly HttpClient _http = new();

    public bool IsRunning => _process is { HasExited: false };

    public event Action<string>? LogMessage;

    public async Task<bool> EnsureBinaryAsync(IProgress<string>? progress = null)
    {
        if (File.Exists(MtxExePath))
            return true;

        progress?.Report("Downloading MediaMTX server binary...");
        Directory.CreateDirectory(AppDir);

        try
        {
            var zipPath = Path.Combine(AppDir, "mediamtx.zip");
            using var response = await _http.GetAsync(MtxDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var fs = File.Create(zipPath);
            await response.Content.CopyToAsync(fs);

            progress?.Report("Extracting MediaMTX...");
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, AppDir, overwriteFiles: true);
            File.Delete(zipPath);

            progress?.Report("MediaMTX ready.");
            return File.Exists(MtxExePath);
        }
        catch (Exception ex)
        {
            progress?.Report($"Download failed: {ex.Message}");
            return false;
        }
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
            rtmpEncryption: no

            hls: no
            webrtc: no
            srt: no

            paths:
            {pathsYaml}
            """;

        File.WriteAllText(MtxConfigPath, config);
    }

    private static string BuildPathsYaml(AppSettings settings)
    {
        if (settings.StreamKeys.Count == 0)
            return "  # No stream keys configured";

        var lines = new System.Text.StringBuilder();
        foreach (var key in settings.StreamKeys)
        {
            lines.AppendLine($"  {key.RtmpPath}:");

            if (key.RecordEnabled && !string.IsNullOrWhiteSpace(settings.RecordingPath))
            {
                var recPath = Path.Combine(settings.RecordingPath, key.Key, "%Y-%m-%d_%H-%M-%S-%f")
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
