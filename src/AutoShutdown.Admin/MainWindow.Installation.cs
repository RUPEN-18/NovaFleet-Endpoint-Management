using AutoShutdown.Admin.Infrastructure;
using AutoShutdown.Admin.Services;
using AutoShutdown.Shared;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;

namespace AutoShutdown.Admin;

public partial class MainWindow
{
    private readonly InstallApiClient _installApi;
    private readonly ObservableCollection<InstallRow> _installRows = new();
    private DeploymentOverview? _installOverview;
    private bool _refreshingInstall;
    private bool _installBusy;
    private string? _applicationPath;
    private string _selectedInstallerSha256 = "";
    private InstallerInspection? _installerInspection;
    private DeploymentLanInfo? _lanInfo;
    private bool _lanSettingsLoaded;
    private void InitializeInstallPage()
    {
        InstallJobsGrid.ItemsSource = _installRows;
        // Capture wheel input on the parent before nested controls can consume it.
        MainPageScrollViewer.AddHandler(UIElement.PreviewMouseWheelEvent,
            new System.Windows.Input.MouseWheelEventHandler(InstallationPage_PreviewMouseWheel), handledEventsToo: true);
        foreach (var table in new[] { InstallTargetsGrid, BatchResultsGrid, InstallJobsGrid })
            table.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
                new RequestBringIntoViewEventHandler(InstallationTable_RequestBringIntoView), handledEventsToo: true);
        InstallTargetsGrid.ItemsSource = _installTargets;
        BatchResultsGrid.ItemsSource = _batchRows;
        SetInstallButtons();
    }
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    private void SetInstallButtons()
    {
        if(InstallPackageButton is null || ImportApplicationButton is null || _identity is null)return;
        var active=_installOverview?.Jobs.Any(x=>!SevenZipPolicy.IsTerminal(x.State))==true;
        ImportApplicationButton.IsEnabled=_identity.IsSuper&&!_installBusy&&!active;
        ImportApplicationButton.ToolTip=_identity.IsSuper?(active?"Wait for current jobs before approving a new package.":"Select and approve an installer."):"Super Admin approves packages. Choose an approved package below.";
        PairLocalServiceButton.IsEnabled=_identity.IsSuper&&!_installBusy&&!active&&IsElevated();
        PairLocalServiceButton.ToolTip="Run Admin as administrator only for one-time local service pairing.";
        SaveLanButton.IsEnabled=DisableLanButton.IsEnabled=_identity.IsSuper&&!_installBusy&&!active;
        ExportRemotePairingButton.IsEnabled=_identity.IsSuper&&!_installBusy&&!active&&_lanInfo?.ActiveUrl is not null;
        var chosen=SelectedDevices();
        var packageReady=ApprovedPackagesCombo.SelectedItem is ApprovedPackage || _applicationPath is not null;
        if(_applicationPath is not null)try{ApplicationPackagePolicy.Validate(Path.GetFileName(_applicationPath),SelectedApplicationOptions());}catch(Exception){packageReady=false;}
        // Only explicit PC and software selections enable installation; the server validates every target again.
        InstallPackageButton.IsEnabled=_identity.Can(AdminPermission.Install)&&!_installBusy&&packageReady;
        InstallPackageButton.IsEnabled = InstallPackageButton.IsEnabled && chosen.Count > 0;
        InstallPackageButton.Content = chosen.Count == 0 ? "Install on selected PCs" : $"Install on {chosen.Count} PC(s)";
        InstallPackageButton.ToolTip = "Each PC is checked again on the server. Offline or busy PCs are shown as Not queued.";
        InstallTargetsGrid.IsEnabled = !_installBusy;
        SelectOnlineTargetsButton.IsEnabled = ClearInstallTargetsButton.IsEnabled = BatchHistoryCombo.IsEnabled = !_installBusy;
        RetryFailedButton.IsEnabled = _identity.Can(AdminPermission.Install) && !_installBusy &&
            (_pendingRetryRequest is not null || RetryableBatchJobs().Count > 0);
        var selectedHistory = InstallJobsGrid.SelectedItem as InstallRow;
        RetrySelectedFailureButton.IsEnabled = _identity.Can(AdminPermission.Install) && !_installBusy && selectedHistory is { Status: "Failed" }
            && _installOverview?.Jobs.FirstOrDefault(job => job.DeviceId == selectedHistory.DeviceId)?.Id == selectedHistory.Id
            && !(_installOverview?.Jobs.Any(job => job.RetryOfJobId == selectedHistory.Id) == true);
        RetryFailedButton.ToolTip = "Retry confirmed Failed attempts in this batch. Successful, active and NeedsReview attempts are excluded.";
    }
    private async Task RefreshInstallDataAsync()
    {
        if (_refreshingInstall || _installBusy) return;
        _refreshingInstall = true;
        try
        {
            var overview = await _installApi.OverviewAsync();
            if (_installBusy) return;
            _installOverview = overview;
            if (_identity.IsSuper && !_lanSettingsLoaded)
            {
                var lanInfo = await _installApi.NetworkAsync();
                if (_installBusy) return;
                _lanInfo = lanInfo;
                LanAddressBox.Text = _lanInfo.ConfiguredAddress ?? "";
                LanCertificateBox.Text = _lanInfo.CertificateSha256;
                LanStatusText.Text = _lanInfo.ActiveUrl is not null ? "Client service endpoint: " + _lanInfo.ActiveUrl
                    : _lanInfo.ConfiguredAddress is not null ? "Saved LAN address is unavailable. Enter this PC's current IPv4 address; local pairing still works."
                    : "Enter this Admin PC's IPv4 address and save it, then reopen Admin to enable LAN deployment.";
                _lanSettingsLoaded = true;
            }
            SynchronizeInstallTargets(_installOverview.Devices);
            var historyRows = _installOverview.Jobs.Select(InstallRow.From).ToList();
            if (!_installRows.SequenceEqual(historyRows))
            {
                SuppressInstallRefreshBringIntoView();
                var historyId = (InstallJobsGrid.SelectedItem as InstallRow)?.Id;
                _installHistoryUpdating = true;
                try
                {
                    _installRows.Clear();
                    foreach (var row in historyRows) _installRows.Add(row);
                    InstallJobsGrid.SelectedItem = _installRows.FirstOrDefault(row => row.Id == historyId);
                }
                finally { _installHistoryUpdating = false; }
            }
            var packages=await _installApi.PackagesAsync();
            if (_installBusy) return;
            var saved=ApprovedPackagesCombo.SelectedValue as string;
            if (ApprovedPackagesCombo.ItemsSource is not IEnumerable<ApprovedPackage> currentPackages || !currentPackages.SequenceEqual(packages))
            {
                _packagesUpdating = true;
                try
                {
                    ApprovedPackagesCombo.ItemsSource = packages;
                    ApprovedPackagesCombo.SelectedValue = _applicationPath is null ? saved : null;
                }
                finally { _packagesUpdating = false; }
            }
            UpdateInstallerSelectionText();
            SynchronizeBatchChoices(_installOverview.Batches);
            await RefreshCurrentBatchAsync();
        }
        catch (Exception ex) { InstallStatusText.Text = "Could not refresh installation data: " + ex.Message; }
        finally { _refreshingInstall = false; SetInstallButtons(); }
    }
    private async void PairLocalServiceButton_Click(object sender, RoutedEventArgs e)
    {
        _installBusy = true; SetInstallButtons();
        try
        {
            var profile = await _installApi.PairAsync();
            DeploymentBootstrap.SaveLocalServiceConnection(profile);
            InstallStatusText.Text = "Pairing saved. Wait a few seconds for the service to show ONLINE.";
        }
        catch (Exception ex) { ShowError(ex.ToString()); }
        finally { _installBusy = false; await RefreshInstallDataAsync(); }
    }
    private async void InstallPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installBusy) return;
        var devices = SelectedDevices();
        if (devices.Count == 0) { ShowError("Select at least one PC using its checkbox."); return; }
        if (devices.Count > InstallStore.MaximumBatchSize) { ShowError("Choose at most 100 PCs per batch."); return; }
        if (MessageBox.Show(this, $"Install the chosen software on {devices.Count} selected PC(s)? Offline, disabled or busy PCs will be listed as Not queued.",
            "Install software", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _installBusy = true; SetInstallButtons();
        try
        {
            if (_pendingBatchRequest is null)
            {
                var package = ApprovedPackagesCombo.SelectedItem as ApprovedPackage;
                if (_applicationPath is not null)
                    package = await _installApi.ImportApplicationAsync(_applicationPath, SelectedApplicationOptions() with { VerificationMode = ApplicationPackagePolicy.Auto });
                if (package is null) throw new InvalidOperationException("Choose an approved package or select an installer.");
                _pendingBatchRequest = new(Guid.NewGuid().ToString("N"), package.Id, devices.Select(device => device.Id).ToList());
            }
            var result = await _installApi.CreateBatchAsync(_pendingBatchRequest);
            _currentBatchId = result.Id;
            _pendingBatchRequest = null;
            _pendingRetryRequest = null;
            _applicationPath = null; _installerInspection = null;
            ApplyBatchResult(result);
        }
        catch (Exception ex)
        {
            InstallStatusText.Text = "Request did not complete: " + ex.Message + " Refresh to inspect results, or submit the same selection again to reuse its request identifier.";
            ShowError(InstallStatusText.Text);
        }
        finally { _installBusy = false; await RefreshInstallDataAsync(); }
    }
    private ApplicationImportOptions SelectedApplicationOptions()
    {
        var requested = new ApplicationImportOptions(ApplicationNameBox.Text.Trim(), InstallerArgumentsBox.Text.Trim(),
            InstalledDisplayNameBox.Text.Trim(), InstalledVersionBox.Text.Trim(),
            InstallerShaBox.Text.Trim().Length == 0 ? _selectedInstallerSha256 : InstallerShaBox.Text.Trim(),
            VerificationMode: ApplicationPackagePolicy.Auto);
        return InstallerCatalog.Resolve(_installerInspection ?? throw new InvalidOperationException("Select an installer first."), requested);
    }
    private async void ImportApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select application installer", Filter = "Application installers|*.msi;*.exe",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") };
        if (dialog.ShowDialog(this) != true) return;
        _installBusy = true; SetInstallButtons();
        _pendingBatchRequest = null;
        _applicationPath = null; _installerInspection = null; _selectedInstallerSha256 = "";

        SelectedInstallerText.Text = "Reading " + Path.GetFileName(dialog.FileName) + "...";
        try
        {
            // Keep the chosen bytes stable while both metadata and SHA-256 are read.
            await using var input = File.OpenRead(dialog.FileName);
            var inspection = await Task.Run(() => InstallerCatalog.Inspect(dialog.FileName));
            _selectedInstallerSha256 = Convert.ToHexString(await SHA256.HashDataAsync(input));
            _installerInspection = inspection;
            _applicationPath = dialog.FileName;
            ApprovedPackagesCombo.SelectedItem=null;

            // Never carry manual overrides from the previous selection into a different package.
            ApplicationNameBox.Text = InstalledDisplayNameBox.Text = InstalledVersionBox.Text = "";
            InstallerArgumentsBox.Text = InstallerShaBox.Text = "";
            var automatic = inspection.Automatic;
            ApplicationNameBox.IsReadOnly = false;
            InstalledDisplayNameBox.IsReadOnly = automatic;
            InstalledVersionBox.IsReadOnly = inspection.Options.VerificationMode == ApplicationPackagePolicy.MsiProduct;
            InstallerArgumentsBox.IsReadOnly = inspection.Options.VerificationMode == ApplicationPackagePolicy.BraveMachine;
            SelectedInstallerText.Text = "Selected: " + inspection.Options.Name + " — " + Path.GetFileName(dialog.FileName);
            InstallerDetectionText.Text = inspection.Summary;
            InstallerAdvancedText.Text = automatic
                ? "Detected product details are used automatically. " + (inspection.Options.VerificationMode == ApplicationPackagePolicy.MsiProduct
                    ? "You may add MSI properties or a vendor checksum." : "Brave's saved silent settings are used; you may add a vendor checksum.")
                : "Name and version overrides are optional. L&T Admin needs this unfamiliar EXE's unattended arguments before it can start installation.";
            ApplicationInstallerExpander.IsExpanded = !automatic;
            UpdateInstallerSelectionText();
        }
        catch (Exception ex)
        {
            SelectedInstallerText.Text = "Select another installer.";
            InstallerDetectionText.Text = ex.Message;
            ShowError(ex.Message);
        }
        finally { _installBusy = false; SetInstallButtons(); }
    }
    private void InstallerOptions_TextChanged(object sender, TextChangedEventArgs e) => SetInstallButtons();
    private async void RefreshInstallButton_Click(object sender, RoutedEventArgs e) => await RefreshInstallDataAsync();
    private async Task SaveLanAsync(string? address)
    {
        _installBusy = true; SetInstallButtons();
        try
        {
            await _installApi.SaveNetworkAsync(address);
            // Prevent exporting a profile with settings awaiting application, including when disabling LAN.
            _lanInfo = null;
            LanStatusText.Text = "Settings saved. Close and reopen Admin to apply the LAN change.";
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _installBusy = false; SetInstallButtons(); }
    }
    private async void SaveLanButton_Click(object sender, RoutedEventArgs e) => await SaveLanAsync(LanAddressBox.Text);
    private async void DisableLanButton_Click(object sender, RoutedEventArgs e) => await SaveLanAsync(null);
    private async void ExportRemotePairingButton_Click(object sender, RoutedEventArgs e)
    {
        var computer = RemoteComputerBox.Text.Trim();
        try { DeploymentNetwork.ValidateComputerName(computer); }
        catch (Exception ex) { ShowError(ex.Message); return; }
        var dialog = new SaveFileDialog { Title = "Save pairing file for " + computer,
            Filter = "Client pairing file|*.nova-pairing.json", FileName = computer + ".nova-pairing.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        _installBusy = true; SetInstallButtons();
        try
        {
            var profile = await _installApi.PairRemoteAsync(computer);
            profile = profile with { ControlServerUrl = $"https://{new Uri(profile.ServerUrl).Host}:5058/" };
            await DeploymentBootstrap.SaveExportedConnectionAsync(dialog.FileName, profile);
            LanStatusText.Text = "Client connection file saved for " + computer + ". Use Setup-NovaClient.ps1 on that PC to connect both features.";
        }
        catch (Exception ex) { ShowError(ex.ToString()); }
        finally { _installBusy = false; await RefreshInstallDataAsync(); }
    }
    private void ShowInstallationButton_Click(object sender, RoutedEventArgs e)
    { SoftwareInstallPanel.Visibility = Visibility.Visible; ConnectionTestPanel.Visibility = Visibility.Collapsed; }
    private void ShowConnectionTestButton_Click(object sender, RoutedEventArgs e)
    { SoftwareInstallPanel.Visibility = Visibility.Collapsed; ConnectionTestPanel.Visibility = Visibility.Visible; }
    private async void ShutdownInstallClientButton_Click(object sender, RoutedEventArgs e)
    {
        var devices = SelectedDevices(); if (devices.Count != 1) return; var device = devices[0];
        if (MessageBox.Show($"Send shutdown to {device.ComputerName}?", "L&T Admin", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await SendActionAsync(ActionKind.Shutdown, TargetSpec.Client(device.ComputerName));
    }
    private async void CancelInstallShutdownButton_Click(object sender, RoutedEventArgs e)
    {
        var devices = SelectedDevices(); if (devices.Count == 1)
            await SendActionAsync(ActionKind.CancelShutdown, TargetSpec.Client(devices[0].ComputerName));
    }
    private sealed record InstallRow
    {
        public string DeviceId { get; init; } = "";
        public string Attempt { get; init; } = "";
        public string Id { get; init; } = "";
        public string Administrator { get; init; } = "";
        public string Created { get; init; } = "";
        public string Computer { get; init; } = "";
        public string Application { get; init; } = "";
        public string Status { get; init; } = "";
        public string Version { get; init; } = "";
        public string Details { get; init; } = "";
        public static InstallRow From(InstallJob job) => new() { DeviceId = job.DeviceId, Attempt = job.RetryOfJobId.Length > 0 ? "Retry" : job.BatchId.Length > 0 ? "Batch" : "Single", Id=job.Id, Administrator=job.AdminUsername.Length==0?"Legacy (unattributed)":$"{job.AdminDisplayName} ({job.AdminUsername})", Created = job.CreatedUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            Computer = job.ComputerName, Application = job.Package.Name, Status = job.State.ToString(), Version = job.InstalledVersion, Details = job.Details };
    }
}
