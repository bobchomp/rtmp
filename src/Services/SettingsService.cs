using System.IO;
using System.Text.Json;
using RTMPProjector.Models;

namespace RTMPProjector.Services;

public class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RTMPProjector");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                Settings = new AppSettings();
                Settings.RecordingPath = Path.Combine(SettingsDir, "Recordings");
                Save();
                return;
            }

            var json = File.ReadAllText(SettingsFile);
            Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            MigrateLegacyRestreamUrls();
        }
        catch
        {
            // Keep the unreadable file instead of letting the next Save() overwrite it —
            // it may still hold stream keys the user can recover by hand
            try
            {
                if (File.Exists(SettingsFile))
                    File.Copy(SettingsFile,
                        Path.Combine(SettingsDir, $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json"),
                        overwrite: true);
            }
            catch { }
            Settings = new AppSettings();
        }
    }

    // Older versions stored one full "rtmp://server/app/KEY" URL — split it into
    // a Custom server + stream key so it shows up in the new Go Live fields.
    private void MigrateLegacyRestreamUrls()
    {
        foreach (var key in Settings.StreamKeys)
        {
            var url = key.RestreamUrl?.Trim();
            key.RestreamUrl = null;
            if (string.IsNullOrEmpty(url) || !string.IsNullOrEmpty(key.RestreamStreamKey)) continue;

            var slash = url.LastIndexOf('/');
            if (slash <= url.IndexOf("://", StringComparison.Ordinal) + 2) continue;

            key.RestreamPlatform  = "Custom";
            key.RestreamServer    = url[..slash];
            key.RestreamStreamKey = url[(slash + 1)..];
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            // Write-then-rename so a crash mid-save can't leave a half-written file
            var tmp = SettingsFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsFile, overwrite: true);
        }
        catch { /* non-fatal */ }
    }
}
