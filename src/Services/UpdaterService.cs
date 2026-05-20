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

    public static string InstallLogPath =>
        Path.Combine(Path.GetTempPath(), "rtmprojector_update.log");

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

            // Prefer a Setup.exe installer asset; fall back to the zip.
            UpdateInfo? info = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                System.Text.Json.JsonElement? setupAsset = null;
                System.Text.Json.JsonElement? zipAsset   = null;

                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase) && setupAsset == null)
                        setupAsset = asset;
                    else if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase) && zipAsset == null)
                        zipAsset = asset;
                }

                var chosen = setupAsset ?? zipAsset;
                if (chosen is System.Text.Json.JsonElement a)
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    info = new UpdateInfo
                    {
                        Version        = latestVersion,
                        CurrentVersion = _currentVersion,
                        AssetName      = name,
                        DownloadUrl    = a.GetProperty("browser_download_url").GetString() ?? "",
                        SizeBytes      = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                        ReleaseNotes   = root.TryGetProperty("body", out var body)
                                             ? body.GetString() ?? "" : "",
                        ReleaseUrl     = root.TryGetProperty("html_url", out var url)
                                             ? url.GetString() ?? "" : "",
                        IsInstaller    = setupAsset != null
                    };
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

    // ── Install ────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches the Inno Setup installer silently. It closes the running app
    /// automatically (/CLOSEAPPLICATIONS) and restarts it after installation.
    /// The caller should call Application.Current.Shutdown() shortly after.
    /// </summary>
    public void LaunchSetupInstaller(string setupPath)
    {
        var appDir = Path.GetDirectoryName(
            Process.GetCurrentProcess().MainModule?.FileName ?? AppContext.BaseDirectory)
            ?? AppContext.BaseDirectory;

        // Read the registry install dir in case the user chose a custom location
        var registryDir = ReadInstallDirFromRegistry();
        var installDir  = registryDir ?? appDir;

        // Remove Zone.Identifier so Windows doesn't flag a downloaded exe as unsafe
        try { File.Delete(setupPath + ":Zone.Identifier"); } catch { }

        // /SILENT (not /VERYSILENT) shows a basic progress dialog — this is intentional:
        // /VERYSILENT + /SUPPRESSMSGBOXES swallows SmartScreen and other error dialogs
        // silently, leaving the user with no feedback if something goes wrong.
        Process.Start(new ProcessStartInfo
        {
            FileName  = setupPath,
            Arguments = $"/SILENT /CLOSEAPPLICATIONS /NOCANCEL /DIR=\"{installDir}\"",
            UseShellExecute = true
        });
    }

    private static string? ReadInstallDirFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\RTMPProjector");
            return key?.GetValue("InstallDir") as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// Legacy zip-based update: writes a PowerShell script that waits for this
    /// process to exit, extracts the new zip over the app directory, then restarts.
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
            $appPid = {{Environment.ProcessId}}
            $zip    = '{{zipPath.Replace("'", "''")}}'
            $dir    = '{{appDir.Replace("'", "''")}}'
            $exe    = '{{appExe.Replace("'", "''")}}'
            $log    = Join-Path $env:TEMP 'rtmprojector_update.log'
            $tmp    = Join-Path $env:TEMP 'rtmprojector_update_extract'

            function Log($msg) { "[$(Get-Date -f 'HH:mm:ss')] $msg" | Add-Content $log }

            Log "Update starting — AppPID=$appPid zip=$zip"
            Log "Target dir: $dir"

            # Wait for the app to exit ($pid is a reserved PS variable — use $appPid)
            try { Wait-Process -Id $appPid -Timeout 15 -ErrorAction SilentlyContinue } catch {}
            Start-Sleep -Milliseconds 500
            Log "App exited (or 15s timeout). Proceeding with extraction."

            try {
                # Extract to a temp dir first so we never partially overwrite
                if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $tmp)
                $count = (Get-ChildItem $tmp -Recurse -File).Count
                Log "Extracted $count files to $tmp"

                # Robocopy: mirror temp dir over app dir (quote paths — handles spaces in dir names)
                $rc = robocopy "$tmp" "$dir" /E /IS /IT /NP /NFL /NDL /NJH /NJS
                Log "Robocopy exit code: $LASTEXITCODE (0-7 = success)"
                if ($LASTEXITCODE -gt 7) { Log "WARNING: robocopy returned $LASTEXITCODE — some files may not have been copied" }

                Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
            } catch {
                Log "ERROR during update: $_"
            }

            if (Test-Path $exe) {
                Log "Restarting $exe"
                Start-Process $exe
            } else {
                Log "ERROR: exe not found after update: $exe"
            }

            Remove-Item $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
            """);

        // Keep the window visible so the user can see progress and any errors
        Process.Start(new ProcessStartInfo
        {
            FileName  = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -File \"{script}\"",
            UseShellExecute = true,
            WindowStyle     = ProcessWindowStyle.Normal
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
