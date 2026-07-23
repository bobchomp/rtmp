using System.Windows;

namespace RTMPProjector.Windows;

public enum CloseDialogResult { MinimiseToTray, Quit, Cancel }

public partial class CloseDialog : Window
{
    public CloseDialogResult Choice { get; private set; } = CloseDialogResult.Cancel;

    public CloseDialog() => InitializeComponent();

    private void MinimiseToTray_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseDialogResult.MinimiseToTray;
        Close();
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseDialogResult.Quit;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseDialogResult.Cancel;
        Close();
    }
}
