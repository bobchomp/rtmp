using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Win32;
using RTMPProjector.Interop;
using RTMPProjector.Models;
using RTMPProjector.Services;

namespace RTMPProjector.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly SettingsService _settingsService;
    private readonly MediaMtxService _mediaMtx;
    private readonly StreamMonitorService _monitor;
    private readonly RestreamService _restream;

    // Active NDI senders — one per stream key with NdiEnabled=true
    private readonly Dictionary<string, NdiSenderService> _ndiSenders = [];

    // ── State ────────────────────────────────────────────────────────────────

    private bool _isServerRunning;
    public bool IsServerRunning
    {
        get => _isServerRunning;
        private set
        {
            _isServerRunning = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(ServerStatusText)); OnPropertyChanged(nameof(ServerStatusColor));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();   // Go Live etc. depend on this
        }
    }

    public string ServerStatusText => IsServerRunning ? "Running" : "Stopped";
    public string ServerStatusColor => IsServerRunning ? "#2ecc71" : "#e74c3c";

    public bool NdiAvailable => NdiLib.IsAvailable;

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

    // ── Diagnostics log ──────────────────────────────────────────────────────

    public ObservableCollection<string> Log { get; } = [];

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Log.Add(line);
        if (Log.Count > 500) Log.RemoveAt(0);
    }

    public void AddLogEntry(string message) => UIInvoke(() => AppendLog(message));

    public void ClearLog() => Log.Clear();

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

    // ── Feature 6: Disk space ─────────────────────────────────────────────────

    private string _diskSpaceText = "";
    public string DiskSpaceText
    {
        get => _diskSpaceText;
        set { _diskSpaceText = value; OnPropertyChanged(); }
    }

    private bool _diskSpaceIsLow;
    public bool DiskSpaceIsLow
    {
        get => _diskSpaceIsLow;
        set { _diskSpaceIsLow = value; OnPropertyChanged(); }
    }

    private readonly DispatcherTimer _diskTimer;

    private void RefreshDiskSpace()
    {
        try
        {
            var path = Settings.RecordingPath;
            if (string.IsNullOrEmpty(path)) { DiskSpaceText = ""; return; }

            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) { DiskSpaceText = ""; return; }

            var drive = new DriveInfo(root);
            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
            DiskSpaceText = $"{freeGb:F1} GB free";
            DiskSpaceIsLow = freeGb < 5.0;
        }
        catch
        {
            DiskSpaceText = "";
            DiskSpaceIsLow = false;
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public RelayCommand ToggleServerCommand { get; }
    public RelayCommand AddStreamKeyCommand { get; }
    public RelayCommand RemoveStreamKeyCommand { get; }
    public RelayCommand CopyRtmpUrlCommand { get; }
    public RelayCommand BrowseRecordingPathCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand OpenProjectionCommand { get; }

    // Feature 5: Open recordings folder
    public RelayCommand OpenRecordingsFolderCommand { get; }

    // Go Live / End Live for a stream key
    public RelayCommand ToggleGoLiveCommand { get; }

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action<StreamKey>? StreamBecameActive;
    public event Action<StreamKey>? StreamBecameInactive;
    public event Action<StreamKey>? OpenProjectionRequested;

    // ── Construction ──────────────────────────────────────────────────────────

    public MainViewModel(SettingsService settingsService, MediaMtxService mediaMtx,
                         StreamMonitorService monitor)
    {
        _settingsService = settingsService;
        _mediaMtx = mediaMtx;
        _monitor = monitor;
        _restream = new RestreamService(UIInvoke);
        _restream.LogMessage += msg => UIInvoke(() => AppendLog(msg));

        // Try to load NDI SDK runtime (non-fatal if absent)
        NdiLib.TryLoad();
        AppendLog(NdiLib.IsAvailable
            ? "[NDI] Runtime loaded — NDI output is available."
            : "[NDI] Runtime not found — NDI output disabled. Install from ndi.video to enable.");

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
        OpenProjectionCommand = new RelayCommand(
            o =>
            {
                // Display tab button has no parameter: use the selected key, else a live one, else the first
                var key = o as StreamKey ?? SelectedStreamKey
                          ?? StreamKeys.FirstOrDefault(k => k.IsActive) ?? StreamKeys.FirstOrDefault();
                if (key == null)
                {
                    StatusMessage = "Add a stream key first.";
                    return;
                }
                OpenProjectionRequested?.Invoke(key);
            });

        // Feature 5: Open recordings folder command
        OpenRecordingsFolderCommand = new RelayCommand(
            _ =>
            {
                var p = Settings.RecordingPath;
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", p) { UseShellExecute = true });
                }
            },
            _ => !string.IsNullOrEmpty(Settings.RecordingPath) && Directory.Exists(Settings.RecordingPath));

        ToggleGoLiveCommand = new RelayCommand(
            o =>
            {
                if (o is not StreamKey key) return;
                if (key.IsLive)
                {
                    _restream.EndLive(key);
                    StatusMessage = $"{key.Name}: live stream ended.";
                }
                else
                {
                    _settingsService.Save();   // remember the destination without restarting the server
                    StatusMessage = $"{key.Name}: going live on {key.RestreamDisplayName}…";
                    _ = _restream.GoLiveAsync(key, BuildRtmpUrl(key));
                }
            },
            o => o is StreamKey key && (key.IsLive || (IsServerRunning && key.HasRestreamDestination)));

        RemoveLegacyWindowsAutoStart();

        _mediaMtx.LogMessage += msg => UIInvoke(() => AppendLog(msg));
        _monitor.LogMessage  += msg => UIInvoke(() => AppendLog(msg));

        _monitor.StreamStarted += key => UIInvoke(() =>
        {
            // StreamKey.IsActive was already set in the monitor — INPC fires automatically
            StatusMessage = $"Stream connected: {key.RtmpPath}";
            AppendLog($"Stream STARTED — path: {key.RtmpPath}  name: {key.Name}");
            if (key.ExplicitPath != null)
                AppendLog($"⚠ Stream on unrecognised path '{key.RtmpPath}' — it doesn't match any configured stream key, " +
                          "so it won't be projected automatically. Check the URL on the sending device, " +
                          "or open it manually if you expected it.");

            if (key.NdiEnabled)
                StartNdiSender(key);

            _restream.OnStreamStarted(key);

            StreamBecameActive?.Invoke(key);
        });

        _monitor.StreamStopped += key => UIInvoke(() =>
        {
            // StreamKey.IsActive was already cleared in the monitor — INPC fires automatically
            StatusMessage = IsServerRunning
                ? $"Server running — RTMP port {Settings.RtmpPort}"
                : "Server stopped.";

            StopNdiSender(key.Id);
            _restream.OnStreamStopped(key);

            StreamBecameInactive?.Invoke(key);
        });

        // Feature 6: Disk space timer
        _diskTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _diskTimer.Tick += (_, _) => RefreshDiskSpace();
        _diskTimer.Start();
        RefreshDiskSpace();

        RefreshMonitors();
        SyncStreamKeys();

        var portsOk = ValidatePorts(Settings.RtmpPort, Settings.ApiPort) == null;
        _validRtmpPort = portsOk ? Settings.RtmpPort : 1935;
        _validApiPort  = portsOk ? Settings.ApiPort  : 9997;
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

        // Set the field directly: if the saved monitor is unplugged right now we fall back
        // for display, but must not overwrite the user's saved choice
        _selectedMonitor = Monitors.FirstOrDefault(m => m.Index == Settings.ProjectionMonitorIndex)
                           ?? Monitors.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedMonitor));
    }

    public void SyncStreamKeys()
    {
        foreach (var key in StreamKeys) key.PropertyChanged -= OnStreamKeyPropertyChanged;
        StreamKeys.Clear();
        foreach (var key in Settings.StreamKeys)
        {
            key.PropertyChanged += OnStreamKeyPropertyChanged;
            StreamKeys.Add(key);
        }
    }

    // Ticking "Record this stream" applies straight away (MediaMTX hot-reloads the path)
    private void OnStreamKeyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Go Live button state depends on these, which change outside of user input
        if (e.PropertyName is nameof(StreamKey.IsLive) or nameof(StreamKey.IsActive))
            UIInvoke(System.Windows.Input.CommandManager.InvalidateRequerySuggested);

        if (e.PropertyName != nameof(StreamKey.RecordEnabled) || sender is not StreamKey key) return;
        _settingsService.Save();
        _ = ApplyServerConfigAsync();
        StatusMessage = key.RecordEnabled
            ? $"Recording enabled for {key.Name}."
            : $"Recording disabled for {key.Name}.";
    }

    /// Pushes settings to the running server: hot-reloads path changes, and only
    /// restarts (dropping streams) when a port changed.
    private async Task ApplyServerConfigAsync()
    {
        if (!IsServerRunning) return;
        try
        {
            if (await _mediaMtx.ApplyConfigAsync(Settings))
            {
                // Ports changed: the restart dropped every publisher. Mark them stopped now —
                // the fresh monitor only reports streams that reconnect on the new port.
                foreach (var key in StreamKeys.Where(k => k.IsActive).ToList())
                {
                    key.IsActive = false;
                    key.BitrateKbps = 0;
                    StopNdiSender(key.Id);
                    _restream.OnStreamStopped(key);
                    StreamBecameInactive?.Invoke(key);
                }
                _monitor.Start(Settings);   // poll the new API port
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Applying server settings failed: {ex.Message}");
            StatusMessage = "Server failed to restart — see the Server tab log.";
            _restream.EndAll();
            _monitor.Stop();
            IsServerRunning = false;
        }
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

            await _mediaMtx.StartAsync(Settings);
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
            _restream.EndAll();
            _monitor.Stop();
            await _mediaMtx.StopAsync();
            StopAllNdiSenders();

            // The monitor doesn't fire "stopped" on shutdown — clear live indicators here
            foreach (var key in StreamKeys)
            {
                key.IsActive = false;
                key.BitrateKbps = 0;
            }
            IsServerRunning = false;
            StatusMessage = "Server stopped.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── NDI sender management ─────────────────────────────────────────────────

    private void StartNdiSender(StreamKey key)
    {
        StopNdiSender(key.Id);
        var sender = new NdiSenderService();
        sender.LogMessage += msg => UIInvoke(() => AppendLog(msg));
        _ndiSenders[key.Id] = sender;
        var ndiName = string.IsNullOrWhiteSpace(key.NdiStreamName) ? key.Name : key.NdiStreamName;
        sender.Start(BuildRtmpUrl(key), ndiName);
    }

    private void StopNdiSender(string keyId)
    {
        if (_ndiSenders.TryGetValue(keyId, out var s))
        {
            s.Dispose();
            _ndiSenders.Remove(keyId);
        }
    }

    public void EndAllLiveStreams() => _restream.EndAll();

    private void StopAllNdiSenders()
    {
        foreach (var s in _ndiSenders.Values) s.Dispose();
        _ndiSenders.Clear();
    }

    // ── Stream key management ─────────────────────────────────────────────────

    private void AddStreamKey()
    {
        var key = new StreamKey { Name = $"Stream {StreamKeys.Count + 1}" };
        key.PropertyChanged += OnStreamKeyPropertyChanged;
        StreamKeys.Add(key);
        Settings.StreamKeys.Add(key);
        SelectedStreamKey = key;
        _settingsService.Save();
        _ = ApplyServerConfigAsync();
    }

    private void RemoveStreamKey(StreamKey? key)
    {
        if (key == null) return;
        _restream.EndLive(key);
        StopNdiSender(key.Id);
        key.PropertyChanged -= OnStreamKeyPropertyChanged;
        StreamKeys.Remove(key);
        Settings.StreamKeys.Remove(key);
        if (SelectedStreamKey == key) SelectedStreamKey = StreamKeys.FirstOrDefault();
        _settingsService.Save();
        _ = ApplyServerConfigAsync();
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
            RefreshDiskSpace(); // Feature 6: refresh after path change
            _ = ApplyServerConfigAsync();
        }
    }

    // ── Settings save ─────────────────────────────────────────────────────────

    // Last ports that passed validation — restored if the user types something invalid
    private int _validRtmpPort;
    private int _validApiPort;

    private void SaveSettings()
    {
        var portError = ValidatePorts(Settings.RtmpPort, Settings.ApiPort);
        if (portError != null)
        {
            Settings.RtmpPort = _validRtmpPort;
            Settings.ApiPort  = _validApiPort;
            OnPropertyChanged(nameof(Settings));
            StatusMessage = $"Not saved: {portError}";
            return;
        }
        _validRtmpPort = Settings.RtmpPort;
        _validApiPort  = Settings.ApiPort;

        // StreamKeys and Settings.StreamKeys hold the same objects, so edits are already in place
        _settingsService.Save();
        StatusMessage = "Settings saved.";
        RefreshDiskSpace();

        _ = ApplyServerConfigAsync();
    }

    private static string? ValidatePorts(int rtmp, int api)
    {
        if (rtmp is < 1 or > 65535) return "RTMP port must be between 1 and 65535.";
        if (api  is < 1 or > 65535) return "API port must be between 1 and 65535.";
        if (rtmp == api)             return "RTMP port and API port must be different.";
        return null;
    }

    // Older versions had a "Start with Windows" option. It was removed, so clear any
    // Run entry it left behind — otherwise the app would keep auto-starting with no
    // setting left to turn it off.
    private static void RemoveLegacyWindowsAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue("RTMPProjector", throwOnMissingValue: false);
        }
        catch { /* non-fatal */ }
    }

    // Feature 8: Save window bounds
    public void SaveWindowBounds(double l, double t, double w, double h)
    {
        Settings.WindowLeft   = l;
        Settings.WindowTop    = t;
        Settings.WindowWidth  = w;
        Settings.WindowHeight = h;
        _settingsService.Save();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public string BuildRtmpUrl(StreamKey key) =>
        $"rtmp://localhost:{Settings.RtmpPort}/{key.RtmpPath}";

    // Safe cross-thread UI dispatch: fire-and-forget, no-op if app is shutting down.
    private static void UIInvoke(Action action)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);

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
