using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using RTMPProjector.Models;
using WinForms = System.Windows.Forms;

namespace RTMPProjector;

public partial class ProjectionWindow : Window
{
    private static LibVLC? _libVlc;
    private MediaPlayer? _player;
    private LibVLCSharp.Shared.Media? _media;
    private WinForms.Panel? _videoPanel;

    private readonly string _rtmpUrl;
    private readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public StreamKey? CurrentKey { get; }

    public ProjectionWindow(string rtmpUrl, StreamKey key, WinForms.Screen? monitor = null)
    {
        _rtmpUrl = rtmpUrl;
        CurrentKey = key;

        InitializeComponent();

        // Position on the requested monitor
        PositionOnMonitor(monitor);

        StreamNameLabel.Text = $"LIVE  ·  {key.Name}";
        WaitingUrlLabel.Text = rtmpUrl;

        _hudTimer.Tick += (_, _) => FadeHud(false);
        _hudTimer.Start();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // ── Startup ────────────────────────────────────────────────────────────

    private static readonly string VlcLogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "RTMPProjector", "vlc-init.log");

    private static void VlcLog(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(VlcLogPath)!);
            File.AppendAllText(VlcLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Clear log from previous attempt
        try { File.Delete(VlcLogPath); } catch { }

        var vlcDir     = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        var pluginsDir = Path.Combine(vlcDir, "plugins");

        VlcLog($"BaseDirectory : {AppContext.BaseDirectory}");
        VlcLog($"vlcDir        : {vlcDir}");
        VlcLog($"vlcDir exists : {Directory.Exists(vlcDir)}");

        // Dump everything inside libvlc\win-x64 so we can see exactly what was packaged
        if (Directory.Exists(vlcDir))
        {
            foreach (var f in Directory.GetFiles(vlcDir, "*", SearchOption.AllDirectories))
                VlcLog($"  FILE: {f.Substring(vlcDir.Length)}");
        }
        else
        {
            VlcLog("  (directory does not exist)");
        }

        var pluginDlls = Directory.Exists(pluginsDir)
            ? Directory.GetFiles(pluginsDir, "*.dll", SearchOption.AllDirectories)
            : [];
        VlcLog($"Plugin DLLs (recursive): {pluginDlls.Length}");

        try
        {
            VlcLog("Calling Core.Initialize...");
            Core.Initialize(Directory.Exists(vlcDir) ? vlcDir : null);
            VlcLog("Core.Initialize OK");

            if (_libVlc == null)
            {
                var opts = new[]
                {
                    $"--plugin-path={pluginsDir}",
                    "--network-caching=150",
                    "--live-caching=150",
                    "--rtmp-caching=150",
                    "--no-video-title-show"
                };
                VlcLog($"Creating LibVLC with options: {string.Join(" ", opts)}");
                _libVlc = new LibVLC(enableDebugLogs: false, opts);
                VlcLog("LibVLC created OK");
            }

            _player = new MediaPlayer(_libVlc);

            _player.Playing    += (_, _) => Dispatcher.BeginInvoke(() => WaitingPanel.Visibility = Visibility.Collapsed);
            _player.Stopped    += (_, _) => Dispatcher.BeginInvoke(() => WaitingPanel.Visibility = Visibility.Visible);
            _player.EndReached += (_, _) => Dispatcher.BeginInvoke(() => WaitingPanel.Visibility = Visibility.Visible);

            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                _videoPanel = new WinForms.Panel { BackColor = System.Drawing.Color.Black };
                VlcHost.Child = _videoPanel;
                _player.Hwnd  = _videoPanel.Handle;

                if (!string.IsNullOrEmpty(_rtmpUrl))
                    BeginPlay();
            });
        }
        catch (Exception ex)
        {
            VlcLog($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null) VlcLog($"  InnerException: {ex.InnerException.Message}");

            var hasDll  = File.Exists(Path.Combine(vlcDir, "libvlc.dll"));
            var hasCore = File.Exists(Path.Combine(vlcDir, "libvlccore.dll"));

            WaitingUrlLabel.Text = hasDll
                ? $"VLC failed to load.\n\n" +
                  $"libvlc.dll ✓  libvlccore.dll {(hasCore ? "✓" : "✗")}  " +
                  $"plugins/ {(pluginDlls.Length > 0 ? $"✓ ({pluginDlls.Length} DLLs)" : "✗ (0 DLLs)")}\n\n" +
                  $"Detail: {ex.Message}\n\n" +
                  $"Full log: {VlcLogPath}"
                : $"libvlc DLLs missing from:\n{vlcDir}\n\n" +
                  $"Re-download the release and make sure the libvlc folder is next to RTMPProjector.exe.\n\n" +
                  $"Full log: {VlcLogPath}";
        }
    }

    private void BeginPlay()
    {
        _media?.Dispose();
        _media = new LibVLCSharp.Shared.Media(_libVlc!, _rtmpUrl, FromType.FromLocation);
        _player!.Play(_media);
    }

    // ── Cleanup ────────────────────────────────────────────────────────────

    public void StopPlayback()
    {
        _player?.Stop();
        WaitingPanel.Visibility = Visibility.Visible;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hudTimer.Stop();
        _player?.Stop();
        _player?.Dispose();
        _media?.Dispose();
        _videoPanel?.Dispose();
    }

    // ── Monitor positioning ────────────────────────────────────────────────

    private static WinForms.Screen NonPrimaryOrFirst() =>
        WinForms.Screen.AllScreens.Length > 1
            ? WinForms.Screen.AllScreens.First(s => !s.Primary)
            : WinForms.Screen.AllScreens[0];

    private void PositionOnMonitor(WinForms.Screen? screen)
    {
        screen ??= NonPrimaryOrFirst();

        var bounds = screen.Bounds;
        Left   = bounds.Left;
        Top    = bounds.Top;
        Width  = bounds.Width;
        Height = bounds.Height;

        WindowState = WindowState.Normal; // must set position before maximising
        // For borderless fullscreen, we manually size to the monitor rather than using Maximized
        // (Maximized on a non-primary monitor with WindowStyle=None can cover the wrong display)
    }

    // ── HUD fade ───────────────────────────────────────────────────────────

    private void FadeHud(bool visible)
    {
        var targetOpacity = visible ? 1.0 : 0.0;
        Cursor = visible ? Cursors.Arrow : Cursors.None;
        var anim = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(300));
        Hud.BeginAnimation(OpacityProperty, anim);
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        FadeHud(true);
        _hudTimer.Stop();
        _hudTimer.Start();
    }

    // ── Keyboard ───────────────────────────────────────────────────────────

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.F:
                ToggleFullscreen();
                break;
        }
    }

    private bool _isFullBorderless = true;

    private void ToggleFullscreen()
    {
        // Already borderless — toggle to a normal resizable window and back
        if (_isFullBorderless)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode  = ResizeMode.CanResize;
            Width  = 960;
            Height = 540;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            ResizeMode  = ResizeMode.NoResize;
            PositionOnMonitor(NonPrimaryOrFirst());
        }
        _isFullBorderless = !_isFullBorderless;
    }

    // ── Buttons ────────────────────────────────────────────────────────────

    private void StopBtn_Click(object sender, RoutedEventArgs e) => Close();
}
