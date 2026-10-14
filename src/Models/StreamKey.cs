using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RTMPProjector.Models;

// Feature 2: Stream health state enum
public enum StreamHealthState { Offline, Good, Degraded, Poor }

public class StreamKey : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    private string _name = "Stream";
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public string Key { get; set; } = Guid.NewGuid().ToString("N");

    /// Set by StreamMonitorService for paths that don't match any configured key.
    [JsonIgnore]
    public string? ExplicitPath { get; set; }

    [JsonIgnore]
    public string RtmpPath => ExplicitPath ?? $"live/{Key}";

    private bool _recordEnabled;
    public bool RecordEnabled
    {
        get => _recordEnabled;
        set { if (_recordEnabled == value) return; _recordEnabled = value; OnPropertyChanged(); }
    }

    // ── Go Live (push to YouTube / Facebook / Twitch / custom RTMP) ──────────

    public static readonly string[] RestreamPlatforms = ["YouTube", "Facebook", "Twitch", "Custom"];

    private static string? PresetServer(string platform) => platform switch
    {
        "YouTube"  => "rtmp://a.rtmp.youtube.com/live2",
        "Facebook" => "rtmps://live-api-s.facebook.com:443/rtmp/",
        "Twitch"   => "rtmp://live.twitch.tv/app",
        _          => null
    };

    private string _restreamPlatform = "YouTube";
    public string RestreamPlatform
    {
        get => _restreamPlatform;
        set
        {
            _restreamPlatform = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomPlatform));
            // Presets fill in the server URL; Custom leaves whatever the user typed
            if (PresetServer(value) is { } server)
                RestreamServer = server;
        }
    }

    private string _restreamServer = "rtmp://a.rtmp.youtube.com/live2";
    public string RestreamServer
    {
        get => _restreamServer;
        set { _restreamServer = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasRestreamDestination)); }
    }

    private string _restreamStreamKey = "";
    /// The platform stream key. Never written to settings.json in plain text —
    /// see RestreamStreamKeyProtected.
    [JsonIgnore]
    public string RestreamStreamKey
    {
        get => _restreamStreamKey;
        set { _restreamStreamKey = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasRestreamDestination)); }
    }

    /// What settings.json stores: the stream key encrypted with Windows DPAPI for the
    /// current user, so copying the file to another account/PC doesn't leak it.
    public string RestreamStreamKeyProtected
    {
        get => Protect(_restreamStreamKey);
        set => _restreamStreamKey = Unprotect(value);
    }

    private static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }   // different user/PC — the key has to be re-entered
    }

    /// Legacy single-URL field from older versions — migrated on load, then cleared.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RestreamUrl { get; set; }

    [JsonIgnore]
    public bool IsCustomPlatform => RestreamPlatform == "Custom";

    [JsonIgnore]
    public bool HasRestreamDestination =>
        !string.IsNullOrWhiteSpace(RestreamServer) && !string.IsNullOrWhiteSpace(RestreamStreamKey);

    /// Full destination URL handed to FFmpeg (server + "/" + key).
    [JsonIgnore]
    public string RestreamDestination =>
        $"{RestreamServer.Trim().TrimEnd('/')}/{RestreamStreamKey.Trim()}";

    private bool _isLive;
    /// True while the user has pressed Go Live (stays true across drone drop-outs).
    [JsonIgnore]
    public bool IsLive
    {
        get => _isLive;
        set { if (_isLive == value) return; _isLive = value; OnPropertyChanged(); }
    }

    private string _liveStatus = "";
    [JsonIgnore]
    public string LiveStatus
    {
        get => _liveStatus;
        set { if (_liveStatus == value) return; _liveStatus = value; OnPropertyChanged(); }
    }

    /// Set by StreamMonitorService from MediaMTX's track list. Null = unknown.
    [JsonIgnore]
    public bool? HasAudio { get; set; }

    public bool NdiEnabled { get; set; } = false;
    public string NdiStreamName { get; set; } = "";

    // Feature 2: Notifying IsActive
    private bool _isActive;
    [JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HealthState));
        }
    }

    // Feature 2: BitrateKbps with health state derivation
    private double _bitrateKbps;
    [JsonIgnore]
    public double BitrateKbps
    {
        get => _bitrateKbps;
        set
        {
            if (Math.Abs(_bitrateKbps - value) < 0.001) return;
            _bitrateKbps = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HealthState));
        }
    }

    // Feature 2: Derived health state
    [JsonIgnore]
    public StreamHealthState HealthState
    {
        get
        {
            if (!IsActive) return StreamHealthState.Offline;
            if (BitrateKbps > 500) return StreamHealthState.Good;
            if (BitrateKbps >= 50) return StreamHealthState.Degraded;
            return StreamHealthState.Poor;
        }
    }

    // Feature 4: Recording status indicator
    private bool _isRecording;
    [JsonIgnore]
    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (_isRecording == value) return;
            _isRecording = value;
            OnPropertyChanged();
        }
    }

    // INotifyPropertyChanged
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
