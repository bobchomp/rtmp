using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using RTMPProjector.Models;
using WinForms = System.Windows.Forms;

namespace RTMPProjector;

public partial class ProjectionWindow : Window
{
    private static LibVLC? _libVlc;
    private MediaPlayer? _player;
    private LibVLCSharp.Shared.Media? _media;

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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var vlcDir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        try
        {
            // Pass the path explicitly — relying on auto-detection fails in published apps.
            Core.Initialize(Directory.Exists(vlcDir) ? vlcDir : null);

            _libVlc ??= new LibVLC(enableDebugLogs: false,
                "--network-caching=150",
                "--live-caching=150",
                "--rtmp-caching=150",
                "--no-video-title-show");

            _player = new MediaPlayer(_libVlc);
            VideoView.MediaPlayer = _player;

            _player.Playing    += (_, _) => Dispatcher.BeginInvoke(() => WaitingPanel.Visibility = Visibility.Collapsed);
            _player.Stopped    += (_, _) => Dispatcher.BeginInvoke(() => WaitingPanel.Visibility = Visibility.Visible);
            _player.EndReached += (_, _) => Dispatcher.BeginInvoke(() => WaitingPanel.Visibility = Visibility.Visible);

            if (!string.IsNullOrEmpty(_rtmpUrl))
                // Delay until after the first render pass so the VideoView's
                // internal WindowsFormsHost HWND exists before VLC tries to use it.
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, BeginPlay);
        }
        catch (Exception ex)
        {
            var hasDll    = File.Exists(Path.Combine(vlcDir, "libvlc.dll"));
            var hasCore   = File.Exists(Path.Combine(vlcDir, "libvlccore.dll"));
            var hasPlugin = Directory.Exists(Path.Combine(vlcDir, "plugins"));

            WaitingUrlLabel.Text = hasDll
                ? $"VLC DLLs found but failed to load — a dependency may be missing " +
                  $"(try installing the Visual C++ Redistributable 2015-2022 x64 from Microsoft).\n\n" +
                  $"libvlc.dll ✓  libvlccore.dll {(hasCore ? "✓" : "✗")}  plugins/ {(hasPlugin ? "✓" : "✗")}\n\n" +
                  $"Detail: {ex.Message}"
                : $"libvlc DLLs are missing from: {vlcDir}\n\n" +
                  $"Re-download the latest release and make sure the libvlc folder " +
                  $"is next to RTMPProjector.exe.\n\nDetail: {ex.Message}";
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
