using System.Windows;
using RTMPProjector.Models;
using RTMPProjector.Services;

namespace RTMPProjector;

/// <summary>
/// Update UI. Holds no update state of its own — every button calls back into App,
/// which owns the state machine (Idle → Available → Downloading → Ready).
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdaterService _updater;
    private readonly Action _checkNow;
    private readonly Action _download;
    private readonly Action _install;

    public UpdateWindow(UpdaterService updater, Action checkNow, Action download, Action install)
    {
        _updater  = updater;
        _checkNow = checkNow;
        _download = download;
        _install  = install;
        InitializeComponent();
    }

    // ── Screens (called from App) ─────────────────────────────────────────

    public void ShowChecking() => ShowScreen(ScreenChecking);

    /// info is null when up to date.
    public void ShowUpdateInfo(UpdateInfo? info)
    {
        if (info == null)
        {
            UpToDateVersion.Text = $"You are running the latest version (v{_updater.CurrentVersion}).";
            ShowScreen(ScreenUpToDate);
            return;
        }

        ChipCurrent.Text  = $"v{info.CurrentVersion}";
        ChipNew.Text      = $"v{info.Version}";
        SizeLabel.Text    = info.SizeMb;
        ReleaseNotes.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                                ? "No release notes provided."
                                : info.ReleaseNotes;
        ShowScreen(ScreenAvailable);
    }

    public void ShowDownloadStarting()
    {
        DownloadBar.Value  = 0;
        ProgressLabel.Text = "Starting…";
        SpeedLabel.Text    = "";
        ShowScreen(ScreenDownload);
    }

    public void ShowProgress(DownloadProgress p)
    {
        ShowScreen(ScreenDownload);
        DownloadBar.Value   = p.Percent;
        ProgressLabel.Text  = $"{p.MbDone:F1} / {p.MbTotal:F1} MB  ({p.Percent:F0}%)";
        SpeedLabel.Text     = $"{p.SpeedMbps:F1} MB/s";
    }

    public void ShowDone() => ShowScreen(ScreenDone);

    public void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ShowScreen(ScreenError);
    }

    private void ShowScreen(FrameworkElement screen)
    {
        foreach (var s in new FrameworkElement[]
            { ScreenChecking, ScreenAvailable, ScreenDownload, ScreenDone, ScreenUpToDate, ScreenError })
        {
            s.Visibility = s == screen ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ── Buttons ───────────────────────────────────────────────────────────

    private void BtnDownload_Click(object sender, RoutedEventArgs e)   => _download();
    private void BtnInstall_Click(object sender, RoutedEventArgs e)    => _install();
    private void BtnRetry_Click(object sender, RoutedEventArgs e)      => _checkNow();
    private void BtnCheckAgain_Click(object sender, RoutedEventArgs e) => _checkNow();
    private void BtnLater_Click(object sender, RoutedEventArgs e)      => Hide();

    // Hide instead of close so the window (and any download progress) can be reopened
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (Application.Current is App { IsExiting: true }) return;
        e.Cancel = true;
        Hide();
    }
}
