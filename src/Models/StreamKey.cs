using System.Text.Json.Serialization;

namespace RTMPProjector.Models;

public class StreamKey
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

    [JsonIgnore]
    public bool IsActive { get; set; } = false;
}
