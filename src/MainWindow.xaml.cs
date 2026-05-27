using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using RTMPProjector.Services;
using RTMPProjector.ViewModels;
using RTMPProjector.Windows;

namespace RTMPProjector;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;

        Resources["BoolToVisConverter"] = new BooleanToVisibilityConverter();

        InitializeComponent();

        // Auto-scroll log to bottom when new entries arrive
        ((INotifyCollectionChanged)vm.Log).CollectionChanged += (_, _) =>
        {
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    // Feature 8: Restore window bounds when source is initialized (handle available)
    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        var settings = _vm.Settings;
        if (!double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop))
        {
            // Verify the position is on a visible screen
            var rect = new System.Drawing.Rectangle(
                (int)settings.WindowLeft, (int)settings.WindowTop,
                (int)settings.WindowWidth, (int)settings.WindowHeight);

            bool onScreen = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect));
            if (onScreen)
            {
                Left   = settings.WindowLeft;
                Top    = settings.WindowTop;
                Width  = settings.WindowWidth;
                Height = settings.WindowHeight;
            }
        }
        else
        {
            Width  = settings.WindowWidth;
            Height = settings.WindowHeight;
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Feature 8: Save window bounds before hiding
        _vm.SaveWindowBounds(Left, Top, Width, Height);

        e.Cancel = true;
        Hide();
    }

    private void MinimiseToTray_Click(object sender, RoutedEventArgs e) => Hide();

    private void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        => (System.Windows.Application.Current as App)?.ManualCheckForUpdates();

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Log.Count > 0)
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, _vm.Log));
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
        => _vm.ClearLog();

    private void SetupWebStream_Click(object sender, RoutedEventArgs e)
    {
        var cf = new CloudflaredService();
        var wizard = new WebStreamSetupWizard(cf, _vm.Settings) { Owner = this };
        wizard.SetupCompleted += (tunnelId, hostname) =>
        {
            _vm.Settings.TunnelId = tunnelId;
            _vm.Settings.TunnelHostname = hostname;
            _vm.Settings.TunnelConfigured = true;
            _vm.SaveSettingsFromView();
        };
        wizard.ShowDialog();
    }
}
