using AutoShutdown.Shared;
using System.Collections.ObjectModel;
using System.Windows;

namespace AutoShutdown.Admin;

public partial class MainWindow
{
    private readonly ObservableCollection<DeploymentTestRow> _deploymentTests = new();
    private bool _refreshingDeploymentTests;

    private void InitializeDeploymentPage() => DeploymentTestsGrid.ItemsSource = _deploymentTests;

    private void PopulateDeploymentClients()
    {
        var selected = DeploymentClientCombo.SelectedValue as string;
        var choices = _clients.Select(x => new TargetChoice(x.ComputerName,
            $"{x.ComputerName} ({x.OnlineText})", TargetSpec.Client(x.ComputerName))).ToArray();
        DeploymentClientCombo.ItemsSource = choices;
        DeploymentClientCombo.SelectedValue = choices.Any(x => x.Key == selected) ? selected
            : _clients.FirstOrDefault(x => x.OnlineText == "ONLINE")?.ComputerName ?? choices.FirstOrDefault()?.Key;
    }

    private async void NavDeployments_Click(object sender, RoutedEventArgs e)
    {
        Navigate(DeploymentsPage, "Software Installation", "Select PCs and software, then follow each verified installation result.");
        SoftwareInstallPanel.Visibility=Visibility.Visible;
        await RefreshInstallDataAsync();
    }

    private async void SendDeploymentTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeploymentClientCombo.SelectedValue is not string computer || string.IsNullOrWhiteSpace(computer))
        {
            DeploymentStatusText.Text = "Start the client app, then select its computer name.";
            return;
        }
        SendDeploymentTestButton.IsEnabled = false;
        try
        {
            var job = await _api.SendDeploymentTestAsync(new DeploymentTestRequest
            {
                Id = Guid.NewGuid().ToString("N"), ComputerName = computer
            });
            DeploymentStatusText.Text = $"Test for {job.ComputerName}: {job.Status}. {job.Details}";
            await RefreshDeploymentTestsAsync();
        }
        catch (Exception ex)
        {
            DeploymentStatusText.Text = "Could not create the test job. Refresh history before trying again.";
            ShowError(ex.Message);
        }
        finally { SendDeploymentTestButton.IsEnabled = true; }
    }

    private async Task RefreshDeploymentTestsAsync()
    {
        if (_refreshingDeploymentTests) return;
        _refreshingDeploymentTests = true;
        try
        {
            var jobs = await _api.GetDeploymentTestsAsync();
            _deploymentTests.Clear();
            foreach (var job in jobs) _deploymentTests.Add(DeploymentTestRow.From(job));
            var latest = jobs.FirstOrDefault();
            DeploymentStatusText.Text = latest is null ? "Select a client to test the connection."
                : $"Latest test for {latest.ComputerName}: {latest.Status}. {latest.Details}";
        }
        catch (Exception ex) { DeploymentStatusText.Text = $"Could not refresh test history: {ex.Message}"; }
        finally { _refreshingDeploymentTests = false; }
    }

    private async void RefreshDeploymentTestsButton_Click(object sender, RoutedEventArgs e) => await RefreshDeploymentTestsAsync();

    private sealed record DeploymentTestRow
    {
        public string Id { get; init; } = "";
        public string CreatedText { get; init; } = "";
        public string ComputerName { get; init; } = "";
        public string StatusText { get; init; } = "";
        public string Details { get; init; } = "";
        public static DeploymentTestRow From(DeploymentTestJob job) => new()
        {
            Id = job.Id, ComputerName = job.ComputerName,
            CreatedText = job.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            StatusText = job.Status.ToString(), Details = job.Details
        };
    }
}
