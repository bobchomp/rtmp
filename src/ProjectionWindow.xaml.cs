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

    private string _rtmpUrl;
    private readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    // Auto-reconnect state
    private bool _isClosingIntentionally;
    private bool _isSwitching;
    private readonly DispatcherTimer _reconnectTimer;
    private int _reconnectAttempts;

    // Stream switching
    private StreamKey? _currentKey;
    private readonly Func<StreamKey, string>? _buildRtmpUrl;
    private readonly Func<IReadOnlyList<StreamKey>>? _getStreamKeys;

    public StreamKey? CurrentKey => _currentKey;
    public int MonitorIndex { get; private set; }

    public ProjectionWindow(string rtmpUrl, StreamKey key, WinForms.Screen? monitor = null,
        Func<StreamKey, string>? buildRtmpUrl = null,
        Func<IReadOnlyList<StreamKey>>? getStreamKeys = null)
    {
        _rtmpUrl = rtmpUrl;
        _currentKey = key;
        _buildRtmpUrl = buildRtmpUrl;
        _getStreamKeys = getStreamKeys;

        // Resolve monitor index for multi-stream tracking
        var allScreens = WinForms.Screen.AllScreens;
        var resolvedScreen = monitor ?? (allScreens.Length > 1
            ? allScreens.First(s => !s.Primary)
            : allScreens[0]);
        MonitorIndex = Array.IndexOf(allScreens, resolvedScreen);
        if (MonitorIndex < 0) MonitorIndex = 0;

        InitializeComponent();
        PositionOnMonitor(resolvedScreen);

        StreamNameLabel.Text = $"LIVE  ·  {key.Name}";
        WaitingUrlLabel.Text = rtmpUrl;

        _hudTimer.Tick += (_, _) => FadeHud(false);
        _hudTimer.Start();

        _reconnectTimer = new DispatcherTimer();
        _reconnectTimer.Tick += OnReconnectTick;

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
        try { File.Delete(VlcLogPath); } catch { }

        var vlcDir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        VlcLog($"vlcDir: {vlcDir}  exists: {Directory.Exists(vlcDir)}");

        try
        {
            Core.Initialize(Directory.Exists(vlcDir) ? vlcDir : null);
            VlcLog("Core.Initialize OK");

            // Do NOT pass --network-caching / --live-caching / --rtmp-caching here.
            // Those are per-media options (prefixed ':') that belong on the Media
            // object. Passing them to the LibVLC constructor causes libvlc_new()
            // to return NULL in VLC 3.0.21 with this package layout.
            _libVlc ??= new LibVLC(enableDebugLogs: false);
            VlcLog("LibVLC created OK");

            _player = new MediaPlayer(_libVlc);

            _player.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                WaitingPanel.Visibility = Visibility.Collapsed;
                _reconnectTimer.Stop();
                _reconnectAttempts = 0;
                StreamNameLabel.Text = $"LIVE  ·  {_currentKey?.Name}";
            });

            _player.Stopped += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                WaitingPanel.Visibility = Visibility.Visible;
                if (_isSwitching) { _isSwitching = false; BeginPlay(); return; }
                if (!_isClosingIntentionally) StartReconnect();
            });

            _player.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                WaitingPanel.Visibility = Visibility.Visible;
                if (_isSwitching) { _isSwitching = false; BeginPlay(); return; }
                if (!_isClosingIntentionally) StartReconnect();
            });

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

            var hasDll  = File.Exists(Path.Combine(vlcDir, "libvlc.dll"));
            var hasCore = File.Exists(Path.Combine(vlcDir, "libvlccore.dll"));

            WaitingUrlLabel.Text = hasDll
                ? $"VLC failed to load.\n\n" +
                  $"libvlc.dll ✓  libvlccore.dll {(hasCore ? "✓" : "✗")}\n\n" +
                  $"Detail: {ex.Message}\n\nLog: {VlcLogPath}"
                : $"VLC DLLs missing from:\n{vlcDir}\n\n" +
                  $"Re-download the release.\n\nLog: {VlcLogPath}";
        }
    }

    private void BeginPlay()
    {
        _media?.Dispose();
        // Caching options are per-media (prefixed ':'), not global LibVLC args.
        _media = new LibVLCSharp.Shared.Media(_libVlc!, _rtmpUrl, FromType.FromLocation,
            ":network-caching=150",
            ":live-caching=150",
            ":rtmp-caching=150");
        _player!.Play(_media);
    }

    // ── Auto-reconnect ─────────────────────────────────────────────────────

    private void StartReconnect()
    {
        if (_isClosingIntentionally) return;
        _reconnectAttempts++;
        var intervalSeconds = Math.Min(30, 3 * _reconnectAttempts);
        _reconnectTimer.Interval = TimeSpan.FromSeconds(intervalSeconds);
        _reconnectTimer.Start();
        StreamNameLabel.Text = $"Reconnecting… (attempt {_reconnectAttempts})";
    }

    private void OnReconnectTick(object? sender, EventArgs e)
    {
        _reconnectTimer.Stop();
        if (_isClosingIntentionally) return;
        BeginPlay();
    }

    // ── Stream switching ───────────────────────────────────────────────────

    public void SwitchToStream(string rtmpUrl, StreamKey key)
    {
        _rtmpUrl = rtmpUrl;
        _currentKey = key;
        _reconnectTimer.Stop();
        _reconnectAttempts = 0;
        StreamNameLabel.Text = $"LIVE  ·  {key.Name}";
        WaitingUrlLabel.Text = rtmpUrl;
        WaitingPanel.Visibility = Visibility.Visible;
        _isSwitching = true;
        _player?.Stop(); // Stopped handler fires BeginPlay() when _isSwitching is true
    }

    private void CycleStream()
    {
        if (_buildRtmpUrl == null || _getStreamKeys == null) return;
        var active = _getStreamKeys().Where(k => k.IsActive).ToList();
        if (active.Count < 2) return;
        var idx = _currentKey != null ? active.FindIndex(k => k.Key == _currentKey.Key) : -1;
        var next = active[(idx + 1) % active.Count];
        SwitchToStream(_buildRtmpUrl(next), next);
    }

    private void SwitchToStreamByIndex(int zeroBasedIndex)
    {
        if (_buildRtmpUrl == null || _getStreamKeys == null) return;
        var active = _getStreamKeys().Where(k => k.IsActive).ToList();
        if (zeroBasedIndex >= active.Count) return;
        var next = active[zeroBasedIndex];
        if (next.Key == _currentKey?.Key) return;
        SwitchToStream(_buildRtmpUrl(next), next);
    }

    // ── Cleanup ────────────────────────────────────────────────────────────

    public void StopPlayback()
    {
        _isClosingIntentionally = true;
        _reconnectTimer.Stop();
        _player?.Stop();
        WaitingPanel.Visibility = Visibility.Visible;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosingIntentionally = true;
        _reconnectTimer.Stop();
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

        WindowState = WindowState.Normal;
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
                _isClosingIntentionally = true;
                Close();
                break;
            case Key.F:
                ToggleFullscreen();
                break;
            case Key.M:
                if (_player != null)
                {
                    _player.Mute = !_player.Mute;
                    ShowMuteBadge(_player.Mute);
                }
                break;
            case Key.Tab:
                CycleStream();
                e.Handled = true;
                break;
            case Key.D1: SwitchToStreamByIndex(0); break;
            case Key.D2: SwitchToStreamByIndex(1); break;
            case Key.D3: SwitchToStreamByIndex(2); break;
            case Key.D4: SwitchToStreamByIndex(3); break;
            case Key.D5: SwitchToStreamByIndex(4); break;
            case Key.D6: SwitchToStreamByIndex(5); break;
            case Key.D7: SwitchToStreamByIndex(6); break;
            case Key.D8: SwitchToStreamByIndex(7); break;
            case Key.D9: SwitchToStreamByIndex(8); break;
        }
    }

    private void ShowMuteBadge(bool muted)
    {
        MuteBadge.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _isFullBorderless = true;

    private void ToggleFullscreen()
    {
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

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        _isClosingIntentionally = true;
        Close();
    }
}
