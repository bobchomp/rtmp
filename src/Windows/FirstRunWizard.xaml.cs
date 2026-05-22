using System.Windows;
using RTMPProjector.Models;
using RTMPProjector.Services;

namespace RTMPProjector.Windows;

public partial class FirstRunWizard : Window
{
    private readonly SettingsService _settingsService;
    private readonly string _localIp;
    private int _currentPage = 1;

    // Validated port from page 1
    private int _rtmpPort = 1935;

    /// <summary>True when the user completed the wizard (clicked Finish).</summary>
    public bool WasCompleted { get; private set; }

    public FirstRunWizard(SettingsService settingsService, string localIp)
    {
        _settingsService = settingsService;
        _localIp = localIp;
        InitializeComponent();
    }

    private void ShowPage(int page)
    {
        _currentPage = page;

        Page1.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        Page2.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;

        BackBtn.Visibility = page > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextBtn.Content = page == 2 ? "Finish" : "Next →";

        if (page == 2)
            UpdateRtmpUrlLabel();
    }

    private void UpdateRtmpUrlLabel()
    {
        // Generate a preview URL using a placeholder key
        var previewKey = _settingsService.Settings.StreamKeys.FirstOrDefault()?.Key
                         ?? Guid.NewGuid().ToString("N");
        RtmpUrlLabel.Text = $"rtmp://{_localIp}:{_rtmpPort}/live/{previewKey}";
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage > 1)
            ShowPage(_currentPage - 1);
    }

    private void NextBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage == 1)
        {
            // Validate port
            PortErrorLabel.Visibility = Visibility.Collapsed;

            if (!int.TryParse(PortBox.Text.Trim(), out int port) || port < 1 || port > 65535)
            {
                PortErrorLabel.Text = "Please enter a valid port number between 1 and 65535.";
                PortErrorLabel.Visibility = Visibility.Visible;
                return;
            }

            _rtmpPort = port;
            _settingsService.Settings.RtmpPort = port;

            ShowPage(2);
        }
        else if (_currentPage == 2)
        {
            // Page 2 — Finish: create the stream key and save
            var name = StreamNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
                name = "My Stream";

            var key = new StreamKey { Name = name };
            _settingsService.Settings.StreamKeys.Add(key);
            _settingsService.Settings.FirstRunCompleted = true;
            _settingsService.Save();

            WasCompleted = true;
            Close();
        }
    }
}
