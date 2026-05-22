using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RTMPProjector.Models;

// Feature 2: Stream health state enum
public enum StreamHealthState { Offline, Good, Degraded, Poor }

public class StreamKey : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Stream";
    public string Key { get; set; } = Guid.NewGuid().ToString("N");

    /// Set by StreamMonitorService for paths that don't match any configured key.
    [JsonIgnore]
    public string? ExplicitPath { get; set; }

    [JsonIgnore]
    public string RtmpPath => ExplicitPath ?? $"live/{Key}";

    public bool RecordEnabled { get; set; } = false;
    public bool RestreamEnabled { get; set; } = false;
    public string RestreamUrl { get; set; } = "";

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
