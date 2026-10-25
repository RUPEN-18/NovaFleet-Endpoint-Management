using AutoShutdown.Client.Services;
using AutoShutdown.Shared;
using System.Text.Json;
using System.Windows;
using System.IO;

namespace AutoShutdown.Client;

public partial class App : Application
{
    private ClientAgent? _agent;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => AppDiagnostics.Record("SystemB", "UI_EXCEPTION", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) AppDiagnostics.Record("SystemB", "UNHANDLED_EXCEPTION", exception);
        };
        try
        {
            var options = LoadOptions();
            _agent = new ClientAgent(options, new LocalSettingsCache());
            await _agent.StartAsync();
            var window = new ClientWindow(_agent);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Record("SystemB", "STARTUP", ex);
            if (AppExitState.IsClosing) return;
            MessageBox.Show($"L&T Client could not start.\n\n{ex.Message}", "L&T Admin", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        AppExitState.BeginSessionEnding();
        if (MainWindow is ClientWindow window) window.PrepareForExit();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppExitState.BeginClosing();
        if (_agent is not null) AppDiagnostics.FinishExit("SystemB", () => _agent.DisposeAsync().AsTask());
        base.OnExit(e);
    }

    private static ClientOptions LoadOptions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var section = doc.RootElement.GetProperty("Client");
        return new ClientOptions
        {
            ServerUrl = section.GetProperty("ServerUrl").GetString() ?? "http://127.0.0.1:5055",
            ApiKey = section.GetProperty("ApiKey").GetString() ?? string.Empty,
            CertificateSha256 = section.TryGetProperty("CertificateSha256", out var pin) ? pin.GetString() ?? "" : "",
            DeviceId = section.TryGetProperty("DeviceId", out var device) ? device.GetString() ?? "" : ""
        };
    }
}
