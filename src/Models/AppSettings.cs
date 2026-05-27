namespace RTMPProjector.Models;

public class AppSettings
{
    public int RtmpPort { get; set; } = 1935;
    public int ApiPort { get; set; } = 9997;
    public List<StreamKey> StreamKeys { get; set; } = [];
    public int ProjectionMonitorIndex { get; set; } = 1;
    public bool AutoProjectOnConnect { get; set; } = true;
    public string RecordingPath { get; set; } = "";
    public bool StartMinimized { get; set; } = false;
    public bool AutoStartServer { get; set; } = false;

    // Feature 7: First-run wizard
    public bool FirstRunCompleted { get; set; } = false;

    // Feature 8: Remember window position/size
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 760;
    public double WindowHeight { get; set; } = 620;

    // Feature 10: Theme
    public string Theme { get; set; } = "Dark";

    // Feature: Auto-start with Windows
    public bool AutoStartWithWindows { get; set; } = false;

    // Web stream (HLS output for live.droneoutings.co.uk)
    public bool WebStreamEnabled { get; set; } = false;
    public int HlsPort { get; set; } = 8888;
    public string WebStreamTunnelUrl { get; set; } = "";
    public string WebStreamPassword { get; set; } = "";
}
