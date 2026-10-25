using System.Windows;

namespace AutoShutdown.Client;

public partial class MessagePopupWindow : Window
{
    public MessagePopupWindow(string title, string body)
    {
        InitializeComponent();
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "ADMIN MESSAGE" : title.ToUpperInvariant();
        BodyText.Text = body;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
