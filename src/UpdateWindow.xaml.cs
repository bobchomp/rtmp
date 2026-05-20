using System.Windows;
using RTMPProjector.Models;
using RTMPProjector.Services;

namespace RTMPProjector;

public partial class UpdateWindow : Window
{
    private UpdateInfo? _info;
    private string? _downloadedZipPath;
    private readonly UpdaterService _updater;

    public UpdateWindow(UpdaterService updater)
    {
        _updater = updater;
        InitializeComponent();
    }

    // ── Public entry points (called from App.xaml.cs) ─────────────────────

    /// <summary>Show with a spinner while the check is in progress.</summary>
    public void ShowChecking()
    {
        ShowScreen(null); // blank while waiting
        Show();
        Activate();
    }

    /// <summary>Mirrors onUpdateAvailable(info) — info is null when up to date.</summary>
    public void ShowUpdateInfo(UpdateInfo? info)
    {
        _info = info;

        if (info == null)
        {
            UpToDateVersion.Text = $"You are running the latest version (v{_updater.CurrentVersion}).";
            ShowScreen(ScreenUpToDate);
        }
        else
        {
            ChipCurrent.Text  = $"v{info.CurrentVersion}";
            ChipNew.Text      = $"v{info.Version}";
            SizeLabel.Text    = info.SizeMb;
            ReleaseNotes.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                                    ? "No release notes provided."
                                    : info.ReleaseNotes;
            ShowScreen(ScreenAvailable);
        }

        Show();
        Activate();
    }

    /// <summary>Mirrors onUpdateProgress — called on the UI thread by App.</summary>
    public void ShowProgress(DownloadProgress p)
    {
        ShowScreen(ScreenDownload);
        DownloadBar.Value   = p.Percent;
        ProgressLabel.Text  = $"{p.MbDone:F1} / {p.MbTotal:F1} MB  ({p.Percent:F0}%)";
        SpeedLabel.Text     = $"{p.SpeedMbps:F1} MB/s";
    }

    /// <summary>Mirrors onUpdateComplete.</summary>
    public void ShowDone(string zipPath)
    {
        _downloadedZipPath = zipPath;
        InstallLogHint.Text = $"Install log: {UpdaterService.InstallLogPath}";
        ShowScreen(ScreenDone);
    }

    /// <summary>Mirrors onUpdateError.</summary>
    public void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ShowScreen(ScreenError);
        Show();
        Activate();
    }

    // ── Screen switching ─────────────────────────────────────────────────

    private void ShowScreen(FrameworkElement? screen)
    {
        foreach (var s in new FrameworkElement[]
            { ScreenAvailable, ScreenDownload, ScreenDone, ScreenUpToDate, ScreenError })
        {
            s.Visibility = s == screen ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ── Button handlers ───────────────────────────────────────────────────

    private void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        ShowScreen(ScreenDownload);
        DownloadBar.Value  = 0;
        ProgressLabel.Text = "Starting…";
        SpeedLabel.Text    = "";
        _ = _updater.DownloadUpdateAsync(_info);
    }

    private void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_downloadedZipPath)) return;
        _updater.LaunchUpdateScript(_downloadedZipPath);

        // App exits after a brief delay so the script can detect our PID closing
        Task.Delay(1500).ContinueWith(_ =>
            Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown()));
    }

    private void BtnRetry_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(null);
        _ = _updater.CheckForUpdateAsync(silent: false);
    }

    private void BtnCheckAgain_Click(object sender, RoutedEventArgs e)
    {
        ShowChecking();
        _ = _updater.CheckForUpdateAsync(silent: false);
    }

    private void BtnLater_Click(object sender, RoutedEventArgs e) => Hide();
}
