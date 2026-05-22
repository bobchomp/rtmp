using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Reflection;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using RTMPProjector.Models;
using RTMPProjector.Services;
using RTMPProjector.ViewModels;
using RTMPProjector.Windows;

namespace RTMPProjector;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private SettingsService? _settingsService;
    private MediaMtxService? _mediaMtx;
    private StreamMonitorService? _monitor;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private ProjectionWindow? _projectionWindow;

    // ── Updater state (mirrors main.js) ───────────────────────────────────
    private UpdaterService? _updater;
    private UpdateWindow? _updateWindow;
    private UpdateInfo? _pendingUpdateInfo;
    private string? _downloadedAssetPath;
    private UpdateStatus _updateStatus = UpdateStatus.Idle;
    private System.Threading.Timer? _updateTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            WriteCrashLog(ex.ExceptionObject?.ToString() ?? "Unknown error");
        DispatcherUnhandledException += (_, ex) =>
        {
            WriteCrashLog(ex.Exception.ToString());
            ex.Handled = true;
            MessageBox.Show($"Unexpected error:\n\n{ex.Exception.Message}\n\nDetails written to crash.log",
                "RTMP Projector", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        _settingsService = new SettingsService();
        _settingsService.Load();

        // Feature 10: Apply theme from settings
        ApplyTheme(_settingsService.Settings.Theme);

        // Feature 7: First-run wizard
        // If existing users already have stream keys, treat as completed
        if (!_settingsService.Settings.FirstRunCompleted)
        {
            if (_settingsService.Settings.StreamKeys.Count > 0)
            {
                // Existing user — skip wizard
                _settingsService.Settings.FirstRunCompleted = true;
                _settingsService.Save();
            }
            else
            {
                var localIp = ResolveLocalIp();
                var wizard = new FirstRunWizard(_settingsService, localIp);
                wizard.ShowDialog();

                if (!wizard.WasCompleted)
                {
                    Shutdown();
                    return;
                }
            }
        }

        _mediaMtx = new MediaMtxService();
        _monitor  = new StreamMonitorService();

        _viewModel = new MainViewModel(_settingsService, _mediaMtx, _monitor);
        _viewModel.StreamBecameActive      += OnStreamBecameActive;
        _viewModel.StreamBecameInactive    += OnStreamBecameInactive;
        _viewModel.OpenProjectionRequested += key =>
            OpenProjectionManually(_viewModel.BuildRtmpUrl(key), key);

        _mainWindow = new MainWindow(_viewModel);

        StartUpdater();
        BuildTrayIcon();

        if (_settingsService.Settings.StartMinimized)
            return;

        _mainWindow.Show();

        if (_settingsService.Settings.AutoStartServer)
            _ = _viewModel.StartServerAsync();
    }

    // Feature 10: Theme switching
    public void ApplyTheme(string theme)
    {
        var themeFile = theme == "Light" ? "Themes/Light.xaml" : "Themes/Dark.xaml";
        var uri = new Uri($"pack://application:,,,/{themeFile}", UriKind.Absolute);
        var dict = new ResourceDictionary { Source = uri };

        if (Resources.MergedDictionaries.Count > 0)
            Resources.MergedDictionaries[0] = dict;
        else
            Resources.MergedDictionaries.Add(dict);

        if (_settingsService != null)
        {
            _settingsService.Settings.Theme = theme;
        }
    }

    private static string ResolveLocalIp()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }

    // ── Updater (mirrors startUpdater + onUpdateAvailable in main.js) ─────

    private void StartUpdater()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

        _updater = new UpdaterService(version);
        _updater.OnUpdateAvailable = info => Dispatcher.Invoke(() => OnUpdateAvailable(info));
        _updater.OnProgress        = p    => Dispatcher.Invoke(() => _updateWindow?.ShowProgress(p));
        _updater.OnComplete        = path => Dispatcher.Invoke(() => OnUpdateComplete(path));
        _updater.OnError           = msg  => Dispatcher.Invoke(() => OnUpdateError(msg));
        _updater.LogMessage        = msg  => _viewModel?.AddLogEntry(msg);

        // Silent background check on startup, then every 60 minutes
        _ = _updater.CheckForUpdateAsync(silent: true);
        _updateTimer = new System.Threading.Timer(
            _ => Dispatcher.Invoke(() => _ = _updater.CheckForUpdateAsync(silent: true)),
            null,
            TimeSpan.FromMinutes(60),
            TimeSpan.FromMinutes(60));
    }

    private void OnUpdateAvailable(UpdateInfo? info)
    {
        if (info == null) return;  // silent check found nothing — stay quiet

        _pendingUpdateInfo = info;
        _updateStatus      = UpdateStatus.Available;
        RebuildTrayMenu();
        OpenUpdateWindow();
        _updateWindow!.ShowUpdateInfo(info);
    }

    private void OnUpdateComplete(string assetPath)
    {
        _downloadedAssetPath = assetPath;
        _updateStatus = UpdateStatus.Ready;
        RebuildTrayMenu();
        _updateWindow?.ShowDone(assetPath, _pendingUpdateInfo);
    }

    private void OnUpdateError(string message)
    {
        _updateStatus = UpdateStatus.Idle;
        RebuildTrayMenu();
        _updateWindow?.ShowError(message);
    }

    /// <summary>Mirrors manualCheckForUpdates() in main.js.</summary>
    public void ManualCheckForUpdates()
    {
        if (_updater == null) return;
        OpenUpdateWindow();
        _updateWindow!.ShowChecking();

        _updater.OnUpdateAvailable = info => Dispatcher.Invoke(() =>
        {
            if (info == null)
            {
                _updateStatus = UpdateStatus.Idle;
                _updateWindow?.ShowUpdateInfo(null);  // shows up-to-date screen
            }
            else
            {
                OnUpdateAvailable(info);
            }
        });

        _ = _updater.CheckForUpdateAsync(silent: false);
    }

    private void OpenUpdateWindow()
    {
        if (_updateWindow == null || !_updateWindow.IsLoaded)
            _updateWindow = new UpdateWindow(_updater!);

        // Restore the "ready to install" state if the window was recreated after download
        if (_updateStatus == UpdateStatus.Ready && _downloadedAssetPath != null)
            _updateWindow.ShowDone(_downloadedAssetPath, _pendingUpdateInfo);

        _updateWindow.Show();
        _updateWindow.Activate();
    }

    // ── Tray icon ─────────────────────────────────────────────────────────

    private void BuildTrayIcon()
    {
        _trayIcon = new TaskbarIcon { ToolTipText = "RTMP Projector" };

        try
        {
            var iconUri = new Uri("pack://application:,,,/Assets/tray.ico");
            _trayIcon.Icon = new System.Drawing.Icon(
                GetResourceStream(iconUri).Stream);
        }
        catch { }

        RebuildTrayMenu();
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
    }

    /// <summary>Mirrors rebuildTrayMenu() — called whenever updateStatus changes.</summary>
    private void RebuildTrayMenu()
    {
        if (_trayIcon == null) return;

        var menu = new System.Windows.Controls.ContextMenu();

        void Add(string header, Action onClick)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += (_, _) => onClick();
            menu.Items.Add(item);
        }
        void AddSep() => menu.Items.Add(new System.Windows.Controls.Separator());

        Add("Open Control Panel", ShowMainWindow);

        // Update badge — mirrors the tray menu update entries in main.js
        switch (_updateStatus)
        {
            case UpdateStatus.Available:
                AddSep();
                Add($"⬆  Update Available (v{_pendingUpdateInfo?.Version})", () =>
                {
                    OpenUpdateWindow();
                    _updateWindow!.ShowUpdateInfo(_pendingUpdateInfo);
                });
                break;
            case UpdateStatus.Ready:
                AddSep();
                Add("⬆  Update Ready — Install Now", () => _updateWindow?.Show());
                break;
            default:
                AddSep();
                Add("Check for Updates", ManualCheckForUpdates);
                break;
        }

        AddSep();
        Add("Start Server",  () => _ = _viewModel!.StartServerAsync());
        Add("Stop Server",   () => _ = _viewModel!.StopServerAsync());
        AddSep();
        Add("Exit", ExitApplication);

        _trayIcon.ContextMenu = menu;
        _trayIcon.ToolTipText = _updateStatus == UpdateStatus.Available
            ? $"RTMP Projector — Update Available (v{_pendingUpdateInfo?.Version})"
            : "RTMP Projector";
    }

    // ── Stream projection ─────────────────────────────────────────────────

    private void ShowMainWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void OnStreamBecameActive(StreamKey key)
    {
        if (!_settingsService!.Settings.AutoProjectOnConnect) return;

        if (_projectionWindow != null && _projectionWindow.CurrentKey?.Key != key.Key)
        {
            _projectionWindow.Close();
            _projectionWindow = null;
        }

        if (_projectionWindow == null || !_projectionWindow.IsLoaded)
        {
            var monitor = _viewModel!.SelectedMonitor;
            _projectionWindow = new ProjectionWindow(
                rtmpUrl: _viewModel.BuildRtmpUrl(key),
                key: key,
                monitor: monitor?.Screen);
            _projectionWindow.Show();
        }

        _trayIcon?.ShowBalloonTip("Stream Live", $"Now projecting: {key.Name}", BalloonIcon.Info);
    }

    private void OnStreamBecameInactive(StreamKey key)
    {
        if (_projectionWindow?.CurrentKey?.Key == key.Key)
            _projectionWindow.StopPlayback();

        _trayIcon?.ShowBalloonTip("Stream Ended", $"{key.Name} disconnected.", BalloonIcon.Info);
    }

    public void OpenProjectionManually(string rtmpUrl, StreamKey key)
    {
        _projectionWindow?.Close();
        var monitor = _viewModel!.SelectedMonitor;
        _projectionWindow = new ProjectionWindow(rtmpUrl, key, monitor?.Screen);
        _projectionWindow.Show();
    }

    // ── Shutdown ──────────────────────────────────────────────────────────

    private async void ExitApplication()
    {
        _updateTimer?.Dispose();
        _projectionWindow?.Close();
        if (_viewModel != null) await _viewModel.StopServerAsync();
        if (_mediaMtx  != null) await _mediaMtx.DisposeAsync();
        if (_monitor   != null) await _monitor.DisposeAsync();
        _trayIcon?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    private static void WriteCrashLog(string text)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "crash.log"),
                $"[{DateTime.Now:u}]\n{text}\n");
        }
        catch { }
    }

    private enum UpdateStatus { Idle, Available, Downloading, Ready }
}
