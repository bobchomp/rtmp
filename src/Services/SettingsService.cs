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
        }
        catch
        {
            Settings = new AppSettings();
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
