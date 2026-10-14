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
            File.WriteAllText(SettingsFile, json);
        }
        catch { /* non-fatal */ }
    }
}
