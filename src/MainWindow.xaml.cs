using System.Collections.Specialized;
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

        Resources["BoolToVisConverter"] = new BooleanToVisibilityConverter();

        InitializeComponent();

        // Auto-scroll log to bottom when new entries arrive
        ((INotifyCollectionChanged)vm.Log).CollectionChanged += (_, _) =>
        {
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void MinimiseToTray_Click(object sender, RoutedEventArgs e) => Hide();

    private void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        => (Application.Current as App)?.ManualCheckForUpdates();

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Log.Count > 0)
            Clipboard.SetText(string.Join(Environment.NewLine, _vm.Log));
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
        => _vm.ClearLog();
}
