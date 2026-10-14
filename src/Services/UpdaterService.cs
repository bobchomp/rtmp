using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using RTMPProjector.Models;

namespace RTMPProjector.Services;

/// <summary>
/// Checks the GitHub Releases API for a newer version, downloads the Inno Setup
/// installer (verifying its size and SHA-256 against what GitHub reports), and
/// launches it silently. App owns the update state; this class just does the work.
/// </summary>
public class UpdaterService
{
    private const string ApiUrl = "https://api.github.com/repos/bobchomp/rtmp/releases/latest";

    private static readonly string UpdateDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "RTMPProjector", "updates");

    private readonly string _currentVersion;
    private readonly HttpClient _http;
    private int _downloading;   // 0/1 — guards against two downloads of the same file

    public Action<DownloadProgress>? OnProgress;
    public Action<string>? LogMessage;               // routed to diagnostics log

    public string CurrentVersion => _currentVersion;

    public UpdaterService(string currentVersion)
    {
        _currentVersion = NormaliseVersion(currentVersion);
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("RTMPProjector", _currentVersion));
    }

    /// Strips a leading 'v' and any +build-metadata suffix (e.g. "1.2.5+abc1234").
    public static string NormaliseVersion(string version) => version.Trim().TrimStart('v').Split('+')[0];

    // ── Version check ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the newer release, or null when already up to date.
    /// Throws (after logging) if the check fails or the release has no installer.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        LogMessage?.Invoke($"[Updater] Checking for updates — current: v{_currentVersion}");
        try
        {
            using var response = await _http.GetAsync(ApiUrl);

            // 404 = repo has no releases yet — treat as up to date, not an error
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                LogMessage?.Invoke("[Updater] No releases found on GitHub — up to date.");
                return null;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var latestVersion = NormaliseVersion(root.GetProperty("tag_name").GetString() ?? "");

            if (!IsNewer(latestVersion, _currentVersion))
            {
                LogMessage?.Invoke($"[Updater] Latest release: v{latestVersion} — already up to date.");
                return null;
            }

            LogMessage?.Invoke($"[Updater] Update available: v{latestVersion} (current: v{_currentVersion})");

            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (!name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase)) continue;

                    // GitHub publishes "digest": "sha256:<hex>" for release assets
                    var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString() ?? "" : "";

                    return new UpdateInfo
                    {
                        Version        = latestVersion,
                        CurrentVersion = _currentVersion,
                        AssetName      = Path.GetFileName(name),
                        DownloadUrl    = asset.GetProperty("browser_download_url").GetString() ?? "",
                        SizeBytes      = asset.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                        Sha256         = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                                             ? digest[7..].ToLowerInvariant() : "",
                        ReleaseNotes   = root.TryGetProperty("body", out var body)
                                             ? body.GetString() ?? "" : "",
                    };
                }
            }

            // Usually means the release is still uploading its files — try again later
            throw new InvalidOperationException(
                $"Update v{latestVersion} was found but its installer isn't available yet. Try again in a few minutes.");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[Updater] Check failed: {ex.Message}");
            throw;
        }
    }

    // ── Download ──────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads and verifies the installer. Returns its path. Throws on failure
    /// (including a size or checksum mismatch — the file is deleted).
    /// </summary>
    public async Task<string> DownloadUpdateAsync(UpdateInfo info)
    {
        if (Interlocked.Exchange(ref _downloading, 1) == 1)
            throw new InvalidOperationException("An update is already downloading.");

        Directory.CreateDirectory(UpdateDir);
        var destPath = Path.Combine(UpdateDir, info.AssetName);
        var partPath = destPath + ".partial";

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

            await using (var src = await response.Content.ReadAsStreamAsync())
            await using (var dest = File.Create(partPath))
            {
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
            }

            await VerifyAsync(partPath, info);
            File.Move(partPath, destPath, overwrite: true);
            return destPath;
        }
        catch
        {
            try { File.Delete(partPath); } catch { }
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _downloading, 0);
        }
    }

    private async Task VerifyAsync(string path, UpdateInfo info)
    {
        var size = new FileInfo(path).Length;
        if (info.SizeBytes > 0 && size != info.SizeBytes)
            throw new InvalidDataException(
                $"Downloaded installer is the wrong size ({size:N0} bytes, expected {info.SizeBytes:N0}). Please try again.");

        if (string.IsNullOrEmpty(info.Sha256))
        {
            LogMessage?.Invoke("[Updater] Release has no published checksum — verified size only.");
            return;
        }

        await using var fs = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
        if (actual != info.Sha256)
            throw new InvalidDataException(
                "Downloaded installer failed its checksum check and was deleted. Please try again.");

        LogMessage?.Invoke("[Updater] Installer checksum verified (SHA-256).");
    }

    // ── Install ────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches the verified Inno Setup installer silently. The caller must then shut
    /// the app down; the installer relaunches it when finished.
    /// </summary>
    public void LaunchSetupInstaller(string setupPath)
    {
        var appDir = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)
                     ?? AppContext.BaseDirectory;

        // Read the registry install dir in case the user chose a custom location
        var installDir = ReadInstallDirFromRegistry() ?? appDir;

        // The installer isn't code-signed, so Windows' "downloaded from the internet"
        // marker would make SmartScreen block a silent update. Safe to remove only
        // because the file's size and SHA-256 were just verified against GitHub's.
        try { File.Delete(setupPath + ":Zone.Identifier"); } catch { }

        // /SILENT (not /VERYSILENT) shows a basic progress dialog — this is intentional:
        // /VERYSILENT + /SUPPRESSMSGBOXES swallows SmartScreen and other error dialogs
        // silently, leaving the user with no feedback if something goes wrong.
        Process.Start(new ProcessStartInfo
        {
            FileName  = setupPath,
            Arguments = $"/SILENT /CLOSEAPPLICATIONS /NOCANCEL /DIR=\"{installDir.TrimEnd('\\')}\"",
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

    // ── Version comparison ────────────────────────────────────────────────

    /// <summary>
    /// Semver-style: compares major.minor.patch numerically, and treats a pre-release
    /// ("1.4.0-beta.1") as older than the matching release ("1.4.0").
    /// </summary>
    public static bool IsNewer(string candidate, string current)
    {
        static (int[] Core, string Pre) Parse(string v)
        {
            var dash = v.IndexOf('-');
            var core = dash >= 0 ? v[..dash] : v;
            var pre  = dash >= 0 ? v[(dash + 1)..] : "";
            var parts = core.Split('.');
            var nums = new int[3];
            for (int i = 0; i < 3; i++)
                nums[i] = parts.Length > i && int.TryParse(parts[i], out var n) ? n : 0;
            return (nums, pre);
        }

        var (c1, pre1) = Parse(candidate);
        var (c2, pre2) = Parse(current);
        for (int i = 0; i < 3; i++)
        {
            if (c1[i] > c2[i]) return true;
            if (c1[i] < c2[i]) return false;
        }

        // Same core version: a release beats a pre-release; two pre-releases compare as text
        if (pre1.Length == 0) return pre2.Length > 0;
        if (pre2.Length == 0) return false;
        return string.CompareOrdinal(pre1, pre2) > 0;
    }
}
