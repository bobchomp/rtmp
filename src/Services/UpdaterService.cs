using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using RTMPProjector.Models;

namespace RTMPProjector.Services;

/// <summary>
/// Mirrors the architecture of services/updater.js from the Electron project.
/// Checks the GitHub Releases API, downloads the zip asset, and launches a
/// PowerShell update script — no code-signing required.
/// </summary>
public class UpdaterService
{
    private const string ApiUrl = "https://api.github.com/repos/bobchomp/rtmp/releases/latest";

    private static readonly string UpdateDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "RTMPProjector", "updates");

    private readonly string _currentVersion;
    private readonly HttpClient _http;

    // ── Callbacks (same four as the Electron version) ────────────────────
    public Action<UpdateInfo?>? OnUpdateAvailable;   // null = up to date
    public Action<DownloadProgress>? OnProgress;
    public Action<string>? OnComplete;               // destPath (the zip)
    public Action<string>? OnError;
    public Action<string>? LogMessage;               // routed to diagnostics log

    public string CurrentVersion => _currentVersion;

    public UpdaterService(string currentVersion)
    {
        // Strip leading 'v' and any +build-metadata suffix (e.g. "1.2.5+abc1234")
        _currentVersion = currentVersion.TrimStart('v').Split('+')[0];
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("RTMPProjector", _currentVersion));
    }

    // ── Version check ─────────────────────────────────────────────────────

    public async Task CheckForUpdateAsync(bool silent)
    {
        LogMessage?.Invoke($"[Updater] Checking for updates — current: v{_currentVersion}");
        try
        {
            using var response = await _http.GetAsync(ApiUrl);

            // 404 = repo has no releases yet — treat as up to date, not an error
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                LogMessage?.Invoke("[Updater] No releases found on GitHub — up to date.");
                if (!silent) OnUpdateAvailable?.Invoke(null);
                return;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var latestVersion = tagName.TrimStart('v');

            if (!IsNewer(latestVersion, _currentVersion))
            {
                LogMessage?.Invoke($"[Updater] Latest release: v{latestVersion} — already up to date.");
                if (!silent) OnUpdateAvailable?.Invoke(null);
                return;
            }

            LogMessage?.Invoke($"[Updater] Update available: v{latestVersion} (current: v{_currentVersion})");

            // Find the Windows zip asset (exclude .blockmap etc.)
            UpdateInfo? info = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (!name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                        continue;

                    info = new UpdateInfo
                    {
                        Version        = latestVersion,
                        CurrentVersion = _currentVersion,
                        AssetName      = name,
                        DownloadUrl    = asset.GetProperty("browser_download_url").GetString() ?? "",
                        SizeBytes      = asset.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                        ReleaseNotes   = root.TryGetProperty("body", out var body)
                                             ? body.GetString() ?? "" : "",
                        ReleaseUrl     = root.TryGetProperty("html_url", out var url)
                                             ? url.GetString() ?? "" : ""
                    };
                    break;
                }
            }

            if (info == null)
            {
                var msg = $"Update v{latestVersion} found but no Windows download asset was located. Check the GitHub releases page manually.";
                LogMessage?.Invoke($"[Updater] {msg}");
                OnError?.Invoke(msg);
            }
            else
            {
                OnUpdateAvailable?.Invoke(info);
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[Updater] Check failed: {ex.Message}");
            if (!silent) OnError?.Invoke(ex.Message);
        }
    }

    // ── Download ──────────────────────────────────────────────────────────

    public async Task DownloadUpdateAsync(UpdateInfo info)
    {
        Directory.CreateDirectory(UpdateDir);
        var destPath = Path.Combine(UpdateDir, info.AssetName);

        try
        {
            using var response = await _http.GetAsync(
                info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? info.SizeBytes;
            var buffer = new byte[81_920];
            long downloaded = 0;
            var lastReport = DateTime.UtcNow;
            long bytesAtLastReport = 0;

            await using var src  = await response.Content.ReadAsStreamAsync();
            await using var dest = File.Create(destPath);

            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read));
                downloaded += read;

                var now = DateTime.UtcNow;
                if ((now - lastReport).TotalSeconds >= 0.5 || downloaded == total)
                {
                    var elapsed = (now - lastReport).TotalSeconds;
                    var speed = elapsed > 0
                        ? (downloaded - bytesAtLastReport) / elapsed / 1_048_576.0
                        : 0;

                    OnProgress?.Invoke(new DownloadProgress
                    {
                        Percent   = total > 0 ? downloaded * 100.0 / total : 0,
                        MbDone    = downloaded / 1_048_576.0,
                        MbTotal   = total / 1_048_576.0,
                        SpeedMbps = speed
                    });

                    lastReport = now;
                    bytesAtLastReport = downloaded;
                }
            }

            OnComplete?.Invoke(destPath);
        }
        catch (Exception ex)
        {
            // Delete partial download
            try { File.Delete(destPath); } catch { }
            OnError?.Invoke(ex.Message);
        }
    }

    // ── Install (PowerShell replaces files while app is closed) ───────────

    /// <summary>
    /// Equivalent to openInstaller() in the Electron version.
    /// Writes a PowerShell script that waits for this process to exit,
    /// extracts the new zip over the app directory, then restarts the app.
    /// The caller should call Application.Current.Shutdown() shortly after.
    /// </summary>
    public void LaunchUpdateScript(string zipPath)
    {
        var appExe  = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        var appDir  = Path.GetDirectoryName(appExe) ?? AppContext.BaseDirectory;
        var script  = Path.Combine(Path.GetTempPath(), "rtmprojector_update.ps1");

        // $$""" lets { and } be literal (safe for PowerShell blocks);
        // C# interpolations use {{ expr }} syntax.
        File.WriteAllText(script, $$"""
            $pid = {{Environment.ProcessId}}
            $zip = '{{zipPath.Replace("'", "''")}}'
            $dir = '{{appDir.Replace("'", "''")}}'
            $exe = '{{appExe.Replace("'", "''")}}'

            # Wait for the app to close
            try { Wait-Process -Id $pid -Timeout 10 -ErrorAction SilentlyContinue } catch {}
            Start-Sleep -Seconds 1

            # Extract zip, overwriting existing files
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $dir, $true)

            # Restart
            if (Test-Path $exe) { Start-Process $exe }

            # Clean up script
            Remove-Item $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
            """);

        Process.Start(new ProcessStartInfo
        {
            FileName  = "powershell.exe",
            Arguments = $"-NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{script}\"",
            UseShellExecute = true,
            WindowStyle     = ProcessWindowStyle.Hidden
        });
    }

    // ── Semver comparison (mirrors isNewer() in updater.js) ──────────────

    private static bool IsNewer(string candidate, string current)
    {
        static int[] Parse(string v)
        {
            var parts = v.Split('.');
            return [
                parts.Length > 0 && int.TryParse(parts[0], out var a) ? a : 0,
                parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 0,
                parts.Length > 2 && int.TryParse(parts[2], out var c) ? c : 0
            ];
        }

        var c1 = Parse(candidate);
        var c2 = Parse(current);
        for (int i = 0; i < 3; i++)
        {
            if (c1[i] > c2[i]) return true;
            if (c1[i] < c2[i]) return false;
        }
        return false;
    }
}
