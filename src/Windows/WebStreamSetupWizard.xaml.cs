using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using RTMPProjector.Models;
using RTMPProjector.Services;

namespace RTMPProjector.Windows;

public partial class WebStreamSetupWizard : Window, INotifyPropertyChanged
{
    private readonly CloudflaredService _cf;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;

    // ── Step model ────────────────────────────────────────────────────────────

    public enum StepStatus { Waiting, Running, Done, Skipped, Error }

    public class WizardStep : INotifyPropertyChanged
    {
        public string Label { get; init; } = "";

        private StepStatus _status = StepStatus.Waiting;
        public StepStatus Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public ObservableCollection<WizardStep> Steps { get; } =
    [
        new() { Label = "Download cloudflared" },
        new() { Label = "Authorize with Cloudflare (opens browser)" },
        new() { Label = "Create tunnel" },
        new() { Label = "Configure DNS — player subdomain" },
        new() { Label = "Configure DNS — stream subdomain" },
    ];

    // ── ViewModel state ───────────────────────────────────────────────────────

    private bool _isIdle = true;
    public bool IsIdle
    {
        get => _isIdle;
        private set { _isIdle = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanAct)); }
    }

    private bool _isDone;
    public bool IsDone
    {
        get => _isDone;
        private set { _isDone = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActionLabel)); OnPropertyChanged(nameof(CanAct)); }
    }

    public string ActionLabel => IsDone ? "Done" : "Set Up";
    public bool CanAct => IsIdle;

    // Fired when setup completes
    public event Action<string /*tunnelId*/, string /*streamHostname*/, string /*playerHostname*/>? SetupCompleted;

    public WebStreamSetupWizard(CloudflaredService cf, AppSettings settings)
    {
        _cf = cf;
        _settings = settings;
        DataContext = this;
        InitializeComponent();

        PlayerHostnameBox.Text = string.IsNullOrWhiteSpace(settings.PlayerHostname)
            ? "live.yourhost.co.uk"
            : settings.PlayerHostname;
        StreamHostnameBox.Text = string.IsNullOrWhiteSpace(settings.TunnelHostname)
            ? "stream.yourhost.co.uk"
            : settings.TunnelHostname;
    }

    // ── Buttons ───────────────────────────────────────────────────────────────

    private async void ActionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (IsDone) { Close(); return; }
        await RunSetupAsync();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }

    // ── Setup flow ────────────────────────────────────────────────────────────

    private async Task RunSetupAsync()
    {
        var playerHost = PlayerHostnameBox.Text.Trim().ToLowerInvariant();
        var streamHost = StreamHostnameBox.Text.Trim().ToLowerInvariant();

        if (!playerHost.Contains('.') || !streamHost.Contains('.'))
        {
            Log("Enter valid subdomains for both fields (e.g. live.yourdomain.co.uk).");
            return;
        }

        IsIdle = false;
        _cts = new CancellationTokenSource();
        LogBox.Text = "";

        try
        {
            // ── Step 0: Download cloudflared ──────────────────────────────────
            var stepDl = Steps[0];
            if (_cf.HasBinary)
            {
                stepDl.Status = StepStatus.Skipped;
                Log("cloudflared already present — skipping download.");
            }
            else
            {
                stepDl.Status = StepStatus.Running;
                var ok = await _cf.EnsureBinaryAsync(new Progress<string>(Log));
                stepDl.Status = ok ? StepStatus.Done : StepStatus.Error;
                if (!ok) { Fail("Download failed. Check your internet connection and try again."); return; }
            }

            // ── Step 1: Login ─────────────────────────────────────────────────
            var stepLogin = Steps[1];
            if (_cf.IsLoggedIn)
            {
                stepLogin.Status = StepStatus.Skipped;
                Log("Already authorized with Cloudflare — skipping login.");
            }
            else
            {
                stepLogin.Status = StepStatus.Running;
                Log("Your browser will open — sign in to Cloudflare and click Authorize when prompted. Return here when done.");
                var ok = await _cf.LoginAsync(new Progress<string>(Log), _cts.Token);
                stepLogin.Status = ok ? StepStatus.Done : StepStatus.Error;
                if (!ok) { Fail("Authorization failed. Make sure you clicked Authorize in the browser."); return; }
            }

            // ── Step 2: Create tunnel ─────────────────────────────────────────
            const string tunnelName = "rtmp-projector";
            var stepCreate = Steps[2];
            string? tunnelId = null;

            if (_cf.HasCredentials(_settings.TunnelId))
            {
                stepCreate.Status = StepStatus.Skipped;
                tunnelId = _settings.TunnelId;
                Log($"Tunnel already exists ({tunnelId}) — skipping creation.");
            }
            else
            {
                stepCreate.Status = StepStatus.Running;
                tunnelId = await _cf.CreateTunnelAsync(tunnelName, new Progress<string>(Log));
                stepCreate.Status = tunnelId != null ? StepStatus.Done : StepStatus.Error;
                if (tunnelId == null) { Fail("Tunnel creation failed. Check the log above for details."); return; }
            }

            // ── Step 3: DNS — player subdomain ────────────────────────────────
            var stepDnsPlayer = Steps[3];
            stepDnsPlayer.Status = StepStatus.Running;
            var playerDnsOk = await _cf.RouteDnsAsync(tunnelName, playerHost, new Progress<string>(Log));
            // DNS fails gracefully if record already exists
            stepDnsPlayer.Status = StepStatus.Done;
            if (!playerDnsOk) Log("Player DNS may already exist — continuing.");

            // ── Step 4: DNS — stream subdomain ────────────────────────────────
            var stepDnsStream = Steps[4];
            stepDnsStream.Status = StepStatus.Running;
            var streamDnsOk = await _cf.RouteDnsAsync(tunnelName, streamHost, new Progress<string>(Log));
            stepDnsStream.Status = StepStatus.Done;
            if (!streamDnsOk) Log("Stream DNS may already exist — continuing.");

            // ── Write config & notify ─────────────────────────────────────────
            _cf.WriteConfig(tunnelId, streamHost, _settings.HlsPort, playerHost, _settings.PlayerPort);

            Log($"\nSetup complete!");
            Log($"  Player:  https://{playerHost}/");
            Log($"  Stream:  https://{streamHost}/live/[key]/index.m3u8");
            Log($"\nEnable \"Web Stream\" and start the server to go live.");

            IsDone = true;
            SetupCompleted?.Invoke(tunnelId, streamHost, playerHost);
        }
        catch (OperationCanceledException)
        {
            Log("Setup cancelled.");
            ResetSteps();
        }
        catch (Exception ex)
        {
            Log($"Unexpected error: {ex.Message}");
        }
        finally
        {
            if (!IsDone) IsIdle = true;
        }
    }

    private void Fail(string message)
    {
        Log($"\n{message}");
        IsIdle = true;
    }

    private void ResetSteps()
    {
        foreach (var s in Steps)
            if (s.Status == StepStatus.Running)
                s.Status = StepStatus.Waiting;
        IsIdle = true;
    }

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.Text += (LogBox.Text.Length > 0 ? "\n" : "") + message;
            LogScroll.ScrollToBottom();
        });
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
