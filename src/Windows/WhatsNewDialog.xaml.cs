using System.Windows;

namespace RTMPProjector.Windows;

public partial class WhatsNewDialog : Window
{
    public WhatsNewDialog(string version, string notes)
    {
        InitializeComponent();
        TitleLabel.Text = $"What's New in v{version}";
        NotesText.Text  = string.IsNullOrWhiteSpace(notes) ? "No release notes provided." : notes;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
