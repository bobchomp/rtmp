using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using RTMPProjector.Models;
using RTMPProjector.Services;

namespace RTMPProjector.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly SettingsService _settingsService;
    private readonly MediaMtxService _mediaMtx;
    private readonly StreamMonitorService _monitor;

    // ── State ────────────────────────────────────────────────────────────────

    private bool _isServerRunning;
    public bool IsServerRunning
    {
        get => _isServerRunning;
        private set { _isServerRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(ServerStatusText)); OnPropertyChanged(nameof(ServerStatusColor)); }
    }

    public string ServerStatusText => IsServerRunning ? "Running" : "Stopped";
    public string ServerStatusColor => IsServerRunning ? "#2ecc71" : "#e74c3c";

    private string _statusMessage = "Server not started.";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    private string _busyText = "";
    public string BusyText
    {
        get => _busyText;
        set { _busyText = value; OnPropertyChanged(); }
    }

    // ── Local IP ─────────────────────────────────────────────────────────────

    public string LocalIpAddress { get; } = ResolveLocalIp();

    private static string ResolveLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }

    public string GetFullRtmpUrl(StreamKey key) =>
        $"rtmp://{LocalIpAddress}:{Settings.RtmpPort}/live/{key.Key}";

    // ── Settings pass-throughs ────────────────────────────────────────────────

    public AppSettings Settings => _settingsService.Settings;

    public ObservableCollection<StreamKey> StreamKeys { get; } = [];

    private StreamKey? _selectedStreamKey;
    public StreamKey? SelectedStreamKey
    {
        get => _selectedStreamKey;
        set { _selectedStreamKey = value; OnPropertyChanged(); }
    }

    // ── Monitor list ─────────────────────────────────────────────────────────

    public ObservableCollection<MonitorItem> Monitors { get; } = [];

    private MonitorItem? _selectedMonitor;
    public MonitorItem? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            _selectedMonitor = value;
            OnPropertyChanged();
            if (value != null)
            {
                Settings.ProjectionMonitorIndex = value.Index;
                _settingsService.Save();
            }
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public RelayCommand ToggleServerCommand { get; }
    public RelayCommand AddStreamKeyCommand { get; }
    public RelayCommand RemoveStreamKeyCommand { get; }
    public RelayCommand CopyRtmpUrlCommand { get; }
    public RelayCommand BrowseRecordingPathCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action<StreamKey>? StreamBecameActive;
    public event Action<StreamKey>? StreamBecameInactive;

    // ── Construction ──────────────────────────────────────────────────────────

    public MainViewModel(SettingsService settingsService, MediaMtxService mediaMtx, StreamMonitorService monitor)
    {
        _settingsService = settingsService;
        _mediaMtx = mediaMtx;
        _monitor = monitor;

        ToggleServerCommand = new RelayCommand(_ => _ = ToggleServerAsync());
        AddStreamKeyCommand = new RelayCommand(_ => AddStreamKey());
        RemoveStreamKeyCommand = new RelayCommand(
            o => RemoveStreamKey(o as StreamKey ?? SelectedStreamKey),
            o => (o as StreamKey ?? SelectedStreamKey) != null);
        CopyRtmpUrlCommand = new RelayCommand(
            o => CopyRtmpUrl(o as StreamKey ?? SelectedStreamKey),
            o => (o as StreamKey ?? SelectedStreamKey) != null);
        BrowseRecordingPathCommand = new RelayCommand(_ => BrowseRecordingPath());
        SaveSettingsCommand = new RelayCommand(_ => SaveSettings());

        _monitor.StreamStarted += key => System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            RefreshKeyActiveState();
            StreamBecameActive?.Invoke(key);
        });

        _monitor.StreamStopped += key => System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            RefreshKeyActiveState();
            StreamBecameInactive?.Invoke(key);
        });

        RefreshMonitors();
        SyncStreamKeys();
    }

    // ── Initialization ────────────────────────────────────────────────────────

    public void RefreshMonitors()
    {
        Monitors.Clear();
        int idx = 0;
        foreach (Screen screen in Screen.AllScreens)
        {
            Monitors.Add(new MonitorItem(idx, screen));
            idx++;
        }

        var target = Monitors.FirstOrDefault(m => m.Index == Settings.ProjectionMonitorIndex)
                     ?? Monitors.FirstOrDefault();
        SelectedMonitor = target;
    }

    public void SyncStreamKeys()
    {
        StreamKeys.Clear();
        foreach (var key in Settings.StreamKeys)
            StreamKeys.Add(key);
    }

    // ── Server control ────────────────────────────────────────────────────────

    private async Task ToggleServerAsync()
    {
        if (IsServerRunning)
        {
            await StopServerAsync();
        }
        else
        {
            await StartServerAsync();
        }
    }

    public async Task StartServerAsync()
    {
        IsBusy = true;
        BusyText = "Starting server...";
        try
        {
            // StatusMessage is updated live during download via the progress callback
            var progress = new Progress<string>(msg => StatusMessage = msg);
            bool ready = await _mediaMtx.EnsureBinaryAsync(progress);
            if (!ready)
            {
                // StatusMessage already contains the real error from EnsureBinaryAsync
                return;
            }

            _mediaMtx.WriteConfig(Settings);
            await _mediaMtx.StartAsync();
            _monitor.Start(Settings);
            IsServerRunning = true;
            StatusMessage = $"Server running — RTMP port {Settings.RtmpPort}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Start failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task StopServerAsync()
    {
        IsBusy = true;
        BusyText = "Stopping server...";
        try
        {
            _monitor.Stop();
            await _mediaMtx.StopAsync();
            IsServerRunning = false;
            StatusMessage = "Server stopped.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Stream key management ─────────────────────────────────────────────────

    private void AddStreamKey()
    {
        var key = new StreamKey { Name = $"Stream {StreamKeys.Count + 1}" };
        StreamKeys.Add(key);
        Settings.StreamKeys.Add(key);
        SelectedStreamKey = key;
        _settingsService.Save();
        if (IsServerRunning) _ = _mediaMtx.RestartAsync(Settings);
    }

    private void RemoveStreamKey(StreamKey? key)
    {
        if (key == null) return;
        StreamKeys.Remove(key);
        Settings.StreamKeys.Remove(key);
        if (SelectedStreamKey == key) SelectedStreamKey = StreamKeys.FirstOrDefault();
        _settingsService.Save();
        if (IsServerRunning) _ = _mediaMtx.RestartAsync(Settings);
    }

    private void CopyRtmpUrl(StreamKey? key)
    {
        if (key == null) return;
        var url = GetFullRtmpUrl(key);
        System.Windows.Clipboard.SetText(url);
        StatusMessage = $"Copied: {url}";
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    private void BrowseRecordingPath()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select recording folder" };
        if (!string.IsNullOrEmpty(Settings.RecordingPath))
            dlg.InitialDirectory = Settings.RecordingPath;

        if (dlg.ShowDialog() == DialogResult.OK)
        {
            Settings.RecordingPath = dlg.SelectedPath;
            OnPropertyChanged(nameof(Settings));
            _settingsService.Save();
        }
    }

    // ── Settings save ─────────────────────────────────────────────────────────

    private void SaveSettings()
    {
        // Sync editable fields from StreamKeys back to Settings
        Settings.StreamKeys.Clear();
        foreach (var k in StreamKeys)
            Settings.StreamKeys.Add(k);

        _settingsService.Save();
        StatusMessage = "Settings saved.";

        if (IsServerRunning)
            _ = _mediaMtx.RestartAsync(Settings);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RefreshKeyActiveState()
    {
        foreach (var key in StreamKeys)
            OnPropertyChanged(nameof(key.IsActive)); // triggers UI update via binding
    }

    public string BuildRtmpUrl(StreamKey key) =>
        $"rtmp://localhost:{Settings.RtmpPort}/{key.RtmpPath}";

    // ── INotifyPropertyChanged ─────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class MonitorItem
{
    public int Index { get; }
    public Screen Screen { get; }
    public string DisplayName { get; }

    public MonitorItem(int index, Screen screen)
    {
        Index = index;
        Screen = screen;
        DisplayName = $"Monitor {index + 1}{(screen.Primary ? " (Primary)" : "")}  {screen.Bounds.Width}×{screen.Bounds.Height}";
    }

    public override string ToString() => DisplayName;
}
