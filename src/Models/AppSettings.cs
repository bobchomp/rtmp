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
}
