namespace RTMPProjector.Models;

public class UpdateInfo
{
    public string Version { get; init; } = "";
    public string CurrentVersion { get; init; } = "";
    public string AssetName { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public long SizeBytes { get; init; }
    public string ReleaseNotes { get; init; } = "";
    public string ReleaseUrl { get; init; } = "";

    public string SizeMb => $"{SizeBytes / 1_048_576.0:F1} MB";
}

public class DownloadProgress
{
    public double Percent { get; init; }
    public double MbDone { get; init; }
    public double MbTotal { get; init; }
    public double SpeedMbps { get; init; }
}
