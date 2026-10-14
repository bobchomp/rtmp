using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using RTMPProjector.Models;
using RTMPProjector.Services;
using RTMPProjector.ViewModels;
using RTMPProjector.Windows;
using WinForms = System.Windows.Forms;

namespace RTMPProjector;

public partial class App : Application
{
    private const string MutexName       = "RTMPProjector_SingleInstance";
    private const string ActivateEvtName = "RTMPProjector_Activate";

    // Held for the lifetime of the process — prevents GC from releasing the mutex.
    private Mutex? _singleInstanceMutex;

    private TaskbarIcon? _trayIcon;
    private SettingsService? _settingsService;
    private MediaMtxService? _mediaMtx;
    private StreamMonitorService? _monitor;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private readonly List<ProjectionWindow> _projectionWindows = [];

    // ── Updater state (mirrors main.js) ───────────────────────────────────
    private UpdaterService? _updater;
    private UpdateWindow? _updateWindow;
    private UpdateInfo? _pendingUpdateInfo;
    private string? _downloadedAssetPath;
    private UpdateStatus _updateStatus = UpdateStatus.Idle;
    private DispatcherTimer? _updateTimer;

    private static readonly string AppVersion = UpdaterService.NormaliseVersion(
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Single-instance enforcement ───────────────────────────────────
        // Open/create the activation event *before* taking the mutex, so a second
        // instance can always signal the first one to show itself.
        var activateEvent = new EventWaitHandle(
            initialState: false, EventResetMode.AutoReset, ActivateEvtName);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance is already running — signal it to show itself, then quit.
            activateEvent.Set();
            activateEvent.Dispose();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        // Watch for activation signals from future instances.
        new Thread(() =>
        {
            while (activateEvent.WaitOne())
                Dispatcher.BeginInvoke(ShowMainWindow);
        }) { IsBackground = true, Name = "SingleInstanceActivation" }.Start();

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

        // Show changelog if we just auto-updated
        CheckAndShowWhatsNew();

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
        _viewModel.OpenProjectionRequested += OpenProjectionManually;

        _mainWindow = new MainWindow(_viewModel);

        StartUpdater();
        BuildTrayIcon();

        if (_settingsService.Settings.AutoStartServer)
            _ = _viewModel.StartServerAsync();

        if (!_settingsService.Settings.StartMinimized)
            _mainWindow.Show();
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

    private static void CheckAndShowWhatsNew()
    {
        var path = Path.Combine(Path.GetTempPath(), "rtmprojector_whatsnew.json");
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            File.Delete(path);

            using var doc = JsonDocument.Parse(json);
            var root    = doc.RootElement;
            var version = root.GetProperty("version").GetString() ?? "";
            var notes   = root.GetProperty("notes").GetString() ?? "";

            // The file is written just before installing — if the install failed we're
            // still on the old version, so don't claim the update happened
            if (UpdaterService.NormaliseVersion(version) != AppVersion) return;

            new WhatsNewDialog(version, notes).ShowDialog();
        }
        catch { }
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

    // ── Updater ───────────────────────────────────────────────────────────
    // State machine: Idle → Available → Downloading → Ready. App owns it; the
    // UpdateWindow just displays it and calls back in.

    private void StartUpdater()
    {
        _updater = new UpdaterService(AppVersion);
        _updater.OnProgress = p   => Dispatcher.BeginInvoke(() =>
        {
            if (_updateStatus == UpdateStatus.Downloading) _updateWindow?.ShowProgress(p);
        });
        _updater.LogMessage = msg => _viewModel?.AddLogEntry(msg);

        // Background check on startup, then every 60 minutes
        _ = CheckForUpdatesAsync(manual: false);
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(60) };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync(manual: false);
        _updateTimer.Start();
    }

    /// Tray / Server tab "Check for Updates".
    public void ManualCheckForUpdates() => _ = CheckForUpdatesAsync(manual: true);

    private bool _checking;
    private bool _manualCheckWaiting;   // user clicked "Check" while a background check was running

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updater == null) return;
        if (_checking)
        {
            if (manual)
            {
                _manualCheckWaiting = true;
                OpenUpdateWindow();
                _updateWindow!.ShowChecking();
            }
            return;
        }

        // Already downloading / downloaded — just show where we are
        if (_updateStatus is UpdateStatus.Downloading or UpdateStatus.Ready)
        {
            if (manual) OpenUpdateWindow();
            return;
        }

        if (manual)
        {
            OpenUpdateWindow();
            _updateWindow!.ShowChecking();
        }

        _checking = true;
        try
        {
            var info = await _updater.CheckForUpdateAsync();
            manual |= _manualCheckWaiting;
            if (_updateStatus is UpdateStatus.Downloading or UpdateStatus.Ready) return;

            if (info == null)
            {
                _updateStatus      = UpdateStatus.Idle;
                _pendingUpdateInfo = null;
                RebuildTrayMenu();
                if (manual) _updateWindow?.ShowUpdateInfo(null);
                return;
            }

            // Pop up once per new version — hourly re-checks of a version the user
            // already dismissed stay quiet (it's still in the tray menu)
            var isNewVersion = _pendingUpdateInfo?.Version != info.Version;
            _pendingUpdateInfo = info;
            _updateStatus      = UpdateStatus.Available;
            RebuildTrayMenu();

            if (manual)
            {
                _updateWindow?.ShowUpdateInfo(info);
            }
            else if (isNewVersion)
            {
                // Never steal focus from a live projection — just show a tray balloon
                if (_projectionWindows.Any(w => w.IsLoaded))
                    _trayIcon?.ShowBalloonTip("Update Available",
                        $"RTMP Projector v{info.Version} is available — see the tray menu.", BalloonIcon.Info);
                else
                {
                    OpenUpdateWindow();
                    _updateWindow!.ShowUpdateInfo(info);
                }
            }
        }
        catch (Exception ex)
        {
            if (manual || _manualCheckWaiting) _updateWindow?.ShowError(ex.Message);   // background checks just log
        }
        finally
        {
            _checking = false;
            _manualCheckWaiting = false;
        }
    }

    private async void DownloadUpdate()
    {
        if (_updater == null || _pendingUpdateInfo == null || _updateStatus != UpdateStatus.Available) return;

        _updateStatus = UpdateStatus.Downloading;
        RebuildTrayMenu();
        _updateWindow?.ShowDownloadStarting();

        try
        {
            _downloadedAssetPath = await _updater.DownloadUpdateAsync(_pendingUpdateInfo);
            _updateStatus = UpdateStatus.Ready;
            _updateWindow?.ShowDone();
        }
        catch (Exception ex)
        {
            _viewModel?.AddLogEntry($"[Updater] Download failed: {ex.Message}");
            _updateStatus = UpdateStatus.Available;   // can try again
            _updateWindow?.ShowError(ex.Message);
        }
        RebuildTrayMenu();
    }

    private void InstallUpdate()
    {
        if (_updater == null || _downloadedAssetPath == null || _updateStatus != UpdateStatus.Ready) return;

        try
        {
            WriteWhatsNewFile(_pendingUpdateInfo);
            _updater.LaunchSetupInstaller(_downloadedAssetPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to launch installer:\n\n{ex.Message}\n\n" +
                $"You can install manually from:\n{_downloadedAssetPath}",
                "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Shut down properly straight away (ends live streams, stops the server) so the
        // installer can replace our files — IsExiting is set before anything else, so
        // the installer's own close request won't trigger the "Minimise or Quit?" prompt
        ExitApplication();
    }

    private static void WriteWhatsNewFile(UpdateInfo? info)
    {
        if (info == null) return;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "rtmprojector_whatsnew.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { version = info.Version, notes = info.ReleaseNotes }));
        }
        catch { }
    }

    private void OpenUpdateWindow()
    {
        if (_updateWindow == null)
            _updateWindow = new UpdateWindow(_updater!, ManualCheckForUpdates, DownloadUpdate, InstallUpdate);

        switch (_updateStatus)
        {
            case UpdateStatus.Ready:       _updateWindow.ShowDone(); break;
            case UpdateStatus.Available:   _updateWindow.ShowUpdateInfo(_pendingUpdateInfo); break;
            // Downloading: the window is already showing progress
        }

        _updateWindow.Show();
        _updateWindow.Activate();
    }

    // ── Tray icon ─────────────────────────────────────────────────────────

    private void BuildTrayIcon()
    {
        _trayIcon = new TaskbarIcon { ToolTipText = "RTMP Projector" };

        try
        {
            // Load the ICO directly from the embedded WPF resource, forcing 16×16 so
            // Windows uses the correct small-size frame rather than scaling down a large one.
            var iconUri = new Uri("pack://application:,,,/Assets/tray.ico");
            using var stream = GetResourceStream(iconUri)?.Stream;
            if (stream != null)
                _trayIcon.Icon = new System.Drawing.Icon(stream, new System.Drawing.Size(16, 16));
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
                Add($"⬆  Update Available (v{_pendingUpdateInfo?.Version})", OpenUpdateWindow);
                break;
            case UpdateStatus.Downloading:
                AddSep();
                Add("⬆  Downloading Update…", OpenUpdateWindow);
                break;
            case UpdateStatus.Ready:
                AddSep();
                Add("⬆  Update Ready — Install Now", OpenUpdateWindow);
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

    // Open projection window currently showing this stream (it may have been switched with Tab/1–9)
    private ProjectionWindow? FindProjection(StreamKey key) =>
        _projectionWindows.FirstOrDefault(w => w.IsLoaded && w.CurrentKey?.RtmpPath == key.RtmpPath);

    private void OnStreamBecameActive(StreamKey key)
    {
        // A window already showing this stream (e.g. after a drone drop-out) just resumes
        if (FindProjection(key) is { } existing)
        {
            existing.ResumePlayback();
            return;
        }

        if (!_settingsService!.Settings.AutoProjectOnConnect) return;

        // Streams that don't match a configured key are never put fullscreen
        // automatically — anyone on the network could otherwise take over the screen
        if (key.ExplicitPath != null) return;

        // Prefer the monitor chosen on the Display tab; if a projection already uses it
        // (several drones at once), fall back to the next free monitor
        var usedIndices = _projectionWindows
            .Where(w => w.IsLoaded)
            .Select(w => w.MonitorIndex)
            .ToHashSet();

        var allScreens = WinForms.Screen.AllScreens;
        var chosen     = _viewModel!.SelectedMonitor;
        var screen = chosen != null && !usedIndices.Contains(chosen.Index) && chosen.Index < allScreens.Length
            ? allScreens[chosen.Index]
            : allScreens.Where((_, idx) => !usedIndices.Contains(idx)).FirstOrDefault()
              ?? chosen?.Screen ?? allScreens[0];

        ShowProjection(key, screen);
        _trayIcon?.ShowBalloonTip("Stream Live", $"Now projecting: {key.Name}", BalloonIcon.Info);
    }

    private void OnStreamBecameInactive(StreamKey key)
    {
        FindProjection(key)?.StopPlayback();
        _trayIcon?.ShowBalloonTip("Stream Ended", $"{key.Name} disconnected.", BalloonIcon.Info);
    }

    public void OpenProjectionManually(StreamKey key)
    {
        FindProjection(key)?.Close();
        ShowProjection(key, _viewModel!.SelectedMonitor?.Screen);
    }

    private void ShowProjection(StreamKey key, WinForms.Screen? screen)
    {
        var win = new ProjectionWindow(
            rtmpUrl: _viewModel!.BuildRtmpUrl(key),
            key: key,
            monitor: screen,
            buildRtmpUrl: k => _viewModel.BuildRtmpUrl(k),
            getStreamKeys: () => _viewModel.StreamKeys);
        _projectionWindows.Add(win);
        win.Closed += (_, _) => _projectionWindows.Remove(win);
        win.Show();

        // Opened while the stream is offline — wait for it rather than retrying forever
        if (key.ExplicitPath == null && !key.IsActive)
            win.StopPlayback();
    }

    // ── Shutdown ──────────────────────────────────────────────────────────

    /// True once ExitApplication has started — the main window skips its close prompt.
    public bool IsExiting { get; private set; }

    /// Full shutdown: ends live streams, stops the server, then quits.
    public async void ExitApplication()
    {
        if (IsExiting) return;
        IsExiting = true;
        try
        {
            _updateTimer?.Stop();
            foreach (var win in _projectionWindows.ToList())
                win.Close();
            _projectionWindows.Clear();
            if (_viewModel != null) await _viewModel.StopServerAsync();
            if (_mediaMtx  != null) await _mediaMtx.DisposeAsync();
            if (_monitor   != null) await _monitor.DisposeAsync();
        }
        catch (Exception ex)
        {
            WriteCrashLog($"Error during shutdown (continuing):\n{ex}");
        }
        finally
        {
            // Always actually quit — otherwise a ghost process holds the single-instance lock
            _trayIcon?.Dispose();
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Covers shutdowns that skip ExitApplication (e.g. Windows logoff) — never leave
        // FFmpeg pushing to YouTube/Facebook after the app has gone
        _viewModel?.EndAllLiveStreams();
        _trayIcon?.Dispose();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
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
