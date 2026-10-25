using AutoShutdown.Client.Services;
using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AutoShutdown.Client;

public partial class IdleOverlayWindow : Window
{
    private readonly ClientAgent _agent;

    public IdleOverlayWindow(ClientAgent agent)
    {
        InitializeComponent();
        _agent = agent;
        IdleMessageText.Text = agent.Settings.IdleMessage;
        Opacity = 0;
        Loaded += async (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)));
            await LoadArtworkAsync();
        };
    }

    private async Task LoadArtworkAsync()
    {
        if (string.IsNullOrWhiteSpace(_agent.Settings.IdleImageMediaId)) return;
        try
        {
            var bytes = await _agent.DownloadMediaAsync(_agent.Settings.IdleImageMediaId);
            ArtworkImage.Source = LoadBitmap(bytes);
        }
        catch { }
    }

    public void UpdateCountdown(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        CountdownText.Text = $"Automatic shutdown in {Format(remaining)}";
        IdleMessageText.Text = _agent.Settings.IdleMessage;
    }

    private static string Format(TimeSpan value)
    {
        var hours = (int)value.TotalHours;
        return $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}";
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
}
