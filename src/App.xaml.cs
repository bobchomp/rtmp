using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using RTMPProjector.Services;
using RTMPProjector.ViewModels;

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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Initialize services
        _settingsService = new SettingsService();
        _settingsService.Load();

        _mediaMtx = new MediaMtxService();
        _monitor  = new StreamMonitorService();

        _viewModel = new MainViewModel(_settingsService, _mediaMtx, _monitor);

        // Wire stream events to projection window
        _viewModel.StreamBecameActive += OnStreamBecameActive;
        _viewModel.StreamBecameInactive += OnStreamBecameInactive;

        // Build main window (hidden until user opens it)
        _mainWindow = new MainWindow(_viewModel);

        // Build tray icon
        BuildTrayIcon();

        if (_settingsService.Settings.StartMinimized)
        {
            // Stay in tray
        }
        else
        {
            _mainWindow.Show();
        }

        // Auto-start server if configured
        if (_settingsService.Settings.AutoStartServer)
            _ = _viewModel.StartServerAsync();
    }

    private void BuildTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "RTMP Projector",
            // Icon is loaded from the embedded resource if present
        };

        try
        {
            var iconUri = new Uri("pack://application:,,,/Assets/tray.ico");
            _trayIcon.Icon = new System.Drawing.Icon(Application.GetResourceStream(iconUri).Stream);
        }
        catch { /* icon file optional during dev */ }

        var menu = new System.Windows.Controls.ContextMenu();

        var openItem = new System.Windows.Controls.MenuItem { Header = "Open Control Panel" };
        openItem.Click += (_, _) => ShowMainWindow();

        var startItem = new System.Windows.Controls.MenuItem { Header = "Start Server" };
        startItem.Click += (_, _) => _ = _viewModel!.StartServerAsync();

        var stopItem = new System.Windows.Controls.MenuItem { Header = "Stop Server" };
        stopItem.Click += (_, _) => _ = _viewModel!.StopServerAsync();

        var sepItem = new System.Windows.Controls.Separator();

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitApplication();

        menu.Items.Add(openItem);
        menu.Items.Add(startItem);
        menu.Items.Add(stopItem);
        menu.Items.Add(sepItem);
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenu = menu;
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void OnStreamBecameActive(Models.StreamKey key)
    {
        if (!_settingsService!.Settings.AutoProjectOnConnect) return;

        // Close existing projection window if showing a different stream
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

    private void OnStreamBecameInactive(Models.StreamKey key)
    {
        if (_projectionWindow?.CurrentKey?.Key == key.Key)
        {
            _projectionWindow.StopPlayback();
        }
        _trayIcon?.ShowBalloonTip("Stream Ended", $"{key.Name} disconnected.", BalloonIcon.Info);
    }

    public void OpenProjectionManually(string rtmpUrl, Models.StreamKey key)
    {
        _projectionWindow?.Close();
        var monitor = _viewModel!.SelectedMonitor;
        _projectionWindow = new ProjectionWindow(rtmpUrl, key, monitor?.Screen);
        _projectionWindow.Show();
    }

    private async void ExitApplication()
    {
        _projectionWindow?.Close();
        if (_viewModel != null) await _viewModel.StopServerAsync();
        if (_mediaMtx != null) await _mediaMtx.DisposeAsync();
        if (_monitor  != null) await _monitor.DisposeAsync();
        _trayIcon?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
