using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AutoShutdown.Client;

public partial class ImagePopupWindow : Window
{
    public ImagePopupWindow(string title, string caption, byte[] imageBytes)
    {
        InitializeComponent();
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "ADMIN IMAGE" : title.ToUpperInvariant();
        CaptionText.Text = caption;
        PopupImage.Source = LoadBitmap(imageBytes);
    }

    private static BitmapImage LoadBitmap(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
