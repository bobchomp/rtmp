using System.Windows;
using System.Windows.Controls;
using RTMPProjector.ViewModels;

namespace RTMPProjector;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;

        // Register converters that are referenced inline in XAML
        Resources["BoolToVisConverter"] = new BooleanToVisibilityConverter();

        InitializeComponent();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Minimise to tray instead of closing
        e.Cancel = true;
        Hide();
    }

    private void MinimiseToTray_Click(object sender, RoutedEventArgs e) => Hide();

    private void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        => (Application.Current as App)?.ManualCheckForUpdates();
}
