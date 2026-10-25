using AutoShutdown.Shared;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace AutoShutdown.Admin;

public partial class MainWindow
{
    private readonly AdminIdentity _identity;
    private readonly DateTimeOffset _sessionExpires;
    private bool _loggingOut;
    private bool _managementBusy;
    private readonly ObservableCollection<DeviceAdminRow> _deviceRows = new();
    private readonly ObservableCollection<DeviceAccessRow> _accountDevices = new();
    private string _editingAccount = "";

    private void InitializeManagement()
    {
        ManagedDevicesGrid.ItemsSource = _deviceRows;
        AccountDevicesGrid.ItemsSource = _accountDevices;
        NavAccounts.Visibility = _identity.IsSuper ? Visibility.Visible : Visibility.Collapsed;
        NavSettings.Visibility = _identity.IsSuper ? Visibility.Visible : Visibility.Collapsed;
        NavAutomation.Visibility = _identity.IsSuper ? Visibility.Visible : Visibility.Collapsed;
        NavDeployments.IsEnabled = _identity.Can(AdminPermission.Install);
        NavDeployments.ToolTip = _identity.Can(AdminPermission.Install) ? "Install software on assigned PCs." : "Your account has no software installation permission.";
        NavMessaging.IsEnabled = _identity.Can(AdminPermission.Control);
        NavMessaging.ToolTip = _identity.Can(AdminPermission.Control) ? "Send a message or image." : "Your account has no messaging permission.";
        NavLogs.IsEnabled = _identity.Can(AdminPermission.ReadLogs);
        NavLogs.ToolTip = _identity.Can(AdminPermission.ReadLogs) ? "View authorized activity." : "Your account has no activity log permission.";
        NavGroups.Visibility = NavSchedules.Visibility = _identity.IsSuper ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { PauseClientButton, ResumeClientButton, ShutdownClientButton, CancelClientShutdownButton })
        {
            button.IsEnabled = _identity.Can(AdminPermission.Automation);
            button.ToolTip = button.IsEnabled ? "Apply to the selected connected Client app." : "Your account has no shutdown-control permission.";
        }
        AccountRoleCombo.SelectedIndex = 0;
    }

    private List<InstallTargetRow> SelectedDevices() => _installTargets.Where(row => row.Selected).ToList();

    private async Task RefreshManagementAsync()
    {
        if (_managementBusy || _loggingOut) return;
        _managementBusy = true;
        try
        {
            var devices = await _api.DevicesAsync();
            var focus = (ManagedDevicesGrid.SelectedItem as DeviceAdminRow)?.Id;
            _deviceRows.Clear();
            foreach (var device in devices)
            {
                var service = _installOverview?.Devices.FirstOrDefault(row => row.Id == device.Id);
                var gui = _clients.FirstOrDefault(row => row.ComputerName.Equals(device.ComputerName, StringComparison.OrdinalIgnoreCase));
                _deviceRows.Add(new()
                {
                    Id = device.Id, Computer = device.ComputerName,
                    Display = string.IsNullOrWhiteSpace(device.DisplayName) ? device.ComputerName : device.DisplayName,
                    InstalledSoftware = string.Join("; ", (_installOverview?.Jobs ?? new List<InstallJob>())
                        .Where(job => job.DeviceId == device.Id && job.State is InstallState.Installed or InstallState.AlreadyInstalled)
                        .Select(job => job.Package.Name + " " + job.InstalledVersion).Distinct().Take(3)),
                    Enabled = device.Enabled, Online = service?.Online == true,
                    OnlineText = !device.Enabled ? "DISABLED" : service?.Online == true ? "ONLINE" : "OFFLINE",
                    ControlText = gui?.OnlineText ?? "OFFLINE",
                    ControlLastSeen = gui?.LastSeenText ?? "Never",
                    MonitoringText = gui?.OnlineText == "ONLINE" ? gui.MonitoringText : "OFFLINE",
                    LastSeen = device.LastSeenUtc?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "Never"
                });
            }
            if (focus is not null) ManagedDevicesGrid.SelectedItem = _deviceRows.FirstOrDefault(row => row.Id == focus);
            MetricKnown.Text = _deviceRows.Count.ToString();
            MetricOnline.Text = _deviceRows.Count(row => row.ControlText == "ONLINE").ToString();
            MetricOffline.Text = _deviceRows.Count(row => row.ControlText != "ONLINE").ToString();
            DashboardClientsGrid.Height = Math.Clamp(_deviceRows.Count * 40 + 42, 122, 362);
            DashboardEmptyText.Visibility = _deviceRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DashboardEmptyText.Text = _identity.IsSuper ? "No client PCs registered yet. Add one in Settings." : "No client PCs are assigned to your account.";
            // Keep the current form's selections while newly registered PCs become available.
            if (_identity.IsSuper)
            {
                AccountDevicesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                AccountDevicesGrid.CommitEdit(DataGridEditingUnit.Row, true);
                foreach (var device in devices)
                {
                    var row = _accountDevices.FirstOrDefault(item => item.Id == device.Id);
                    if (row is null) _accountDevices.Add(new() { Id = device.Id, Computer = device.ComputerName });
                }
                foreach (var obsolete in _accountDevices.Where(item => !devices.Any(device => device.Id == item.Id)).ToArray()) _accountDevices.Remove(obsolete);
            }
            SetInstallButtons();
        }
        catch (Exception ex) { if (!_loggingOut) HeaderStatusText.Text = "Device refresh: " + ex.Message; }
        finally { _managementBusy = false; }
    }

    public void StopForLogout() { _loggingOut = true; PrepareForExit(); }
    private async void Logout_Click(object sender, RoutedEventArgs e) => await ((App)Application.Current).LogoutAsync();
    private void NavAutomation_Click(object sender, RoutedEventArgs e) => Navigate(PolicyPage, "Control Panel", "Super Admin settings for the Admin PC and all Clients");
    private async void NavAccounts_Click(object sender, RoutedEventArgs e)
    {
        Navigate(AccountsPage, "Admin Management", "Create accounts, assign permissions and choose authorized PCs");
        try { AccountsGrid.ItemsSource = await _api.AccountsAsync(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void InstalledStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (!_startupCheckLoaded) return;
        try { Infrastructure.StartupManager.SetEnabled(InstalledAdminStartupCheck.IsChecked == true); AdminStartupCheck.IsChecked = InstalledAdminStartupCheck.IsChecked; }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private bool _packagesUpdating;
    private void ApprovedPackage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_packagesUpdating) return;
        _pendingBatchRequest = null;
        if (ApprovedPackagesCombo.SelectedItem is ApprovedPackage package)
        {
            _applicationPath = null; _installerInspection = null;
            SelectedInstallerText.Text = "Selected: " + package.Name;
            InstallerDetectionText.Text = "Approved SHA-256: " + package.Sha256;
        }
        UpdateInstallerSelectionText();
        SetInstallButtons();
    }
    private void ClearSoftwareSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_installBusy) return;
        _pendingBatchRequest = null;
        _applicationPath = null; _installerInspection = null; _selectedInstallerSha256 = "";
        ApprovedPackagesCombo.SelectedItem = null;
        SelectedInstallerText.Text = "Choose an approved package or select an EXE/MSI installer.";
        InstallerDetectionText.Text = "";
        UpdateInstallerSelectionText();
        SetInstallButtons();
    }
    private void UpdateInstallerSelectionText()
    {
        if (ApprovedPackagesCombo.SelectedItem is ApprovedPackage selected)
            ApprovedPackageText.Text = "Ready: " + selected.Name + ". Installed version will be verified on the Client.";
        else if (_applicationPath is not null)
            ApprovedPackageText.Text = "Ready: " + System.IO.Path.GetFileName(_applicationPath);
        else
        {
            ApprovedPackageText.Text = "No software selected. Choose a package or select an EXE/MSI installer.";
            SelectedInstallerText.Text = "Choose an approved package or select an EXE/MSI installer.";
        }
    }
    private void ManagedDevice_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (ManagedDevicesGrid.SelectedItem is not DeviceAdminRow device) return;
        ClientTargetCombo.SelectedValue = "CLIENT:" + device.Computer;

    }
    private void Account_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (AccountsGrid.SelectedItem is not AdminIdentity account) return;
        _editingAccount = account.Id;
        AccountUsernameBox.Text = account.Username; AccountDisplayBox.Text = account.DisplayName;
        AccountRoleCombo.SelectedIndex = account.IsSuper || account.Role == "SuperAdmin" ? 1 : 0;
        AccountEnabledCheck.IsChecked = account.Enabled;
        AccountInstallCheck.IsChecked = account.Can(AdminPermission.Install);
        AccountControlCheck.IsChecked = account.Can(AdminPermission.Control);
        AccountAutomationCheck.IsChecked = account.Can(AdminPermission.Automation);
        AccountLogsCheck.IsChecked = account.Can(AdminPermission.ReadLogs);
        foreach (var row in _accountDevices) row.Selected = account.DeviceIds.Contains(row.Id, StringComparer.Ordinal);
        AccountDevicesGrid.Items.Refresh(); AccountPasswordBox.Clear();
        AccountStatusText.Text = "Editing " + account.Label;
    }
    private void AccountRole_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AccountDevicesGrid is null || AccountInstallCheck is null) return;
        var limited = AccountRoleCombo.SelectedIndex != 1;
        AccountDevicesGrid.IsEnabled = limited;
        foreach (var check in new[] { AccountInstallCheck, AccountControlCheck, AccountAutomationCheck, AccountLogsCheck }) check.IsEnabled = limited;
        AccountAccessText.Text = limited ? "Check each PC this Admin may control. No selection grants access to no PCs." : "Super Admin has all permissions and access to every registered PC.";
    }
    private void NewAccount_Click(object sender, RoutedEventArgs e)
    {
        _editingAccount = ""; AccountsGrid.SelectedItem = null;
        AccountUsernameBox.Clear(); AccountDisplayBox.Clear(); AccountPasswordBox.Clear();
        AccountRoleCombo.SelectedIndex = 0; AccountEnabledCheck.IsChecked = AccountInstallCheck.IsChecked = true;
        AccountControlCheck.IsChecked = AccountAutomationCheck.IsChecked = AccountLogsCheck.IsChecked = false;
        foreach (var row in _accountDevices) row.Selected = false;
        AccountDevicesGrid.Items.Refresh(); AccountStatusText.Text = "Creating a new administrator account.";
    }
    private async void SaveAccount_Click(object sender, RoutedEventArgs e)
    {
        SaveAccountButton.IsEnabled = false;
        try
        {
            AccountDevicesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            AccountDevicesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var permissions = AdminPermission.None;
            if (AccountInstallCheck.IsChecked == true) permissions |= AdminPermission.Install;
            if (AccountControlCheck.IsChecked == true) permissions |= AdminPermission.Control;
            if (AccountAutomationCheck.IsChecked == true) permissions |= AdminPermission.Automation;
            if (AccountLogsCheck.IsChecked == true) permissions |= AdminPermission.ReadLogs;
            var isSuper = AccountRoleCombo.SelectedIndex == 1;
            var ids = isSuper ? new List<string>() : _accountDevices.Where(row => row.Selected).Select(row => row.Id).ToList();
            var saved = await _api.AccountAsync(new(_editingAccount, AccountUsernameBox.Text.Trim(), AccountDisplayBox.Text.Trim(),
                isSuper ? "SuperAdmin" : "Admin", permissions, ids, AccountEnabledCheck.IsChecked == true, AccountPasswordBox.Password));
            AccountPasswordBox.Clear(); _editingAccount = saved.Id;
            AccountStatusText.Text = "Account saved. Its previous sessions have been revoked.";
            if (saved.Id == _identity.Id) { await ((App)Application.Current).LogoutAsync(); return; }
            AccountsGrid.ItemsSource = await _api.AccountsAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SaveAccountButton.IsEnabled = true; }
    }
    private sealed class DeviceAccessRow
    {
        public string Id { get; init; } = "";
        public string Computer { get; init; } = "";
        public bool Selected { get; set; }
    }
    private sealed class DeviceAdminRow
    {
        public string Id { get; init; } = ""; public string Computer { get; init; } = ""; public string Display { get; init; } = "";
        public bool Online { get; init; } public bool Enabled { get; init; }
        public string InstalledSoftware { get; init; } = ""; public string OnlineText { get; init; } = "";
        public string ControlLastSeen { get; init; } = "";
        public string MonitoringText { get; init; } = "";
        public string ControlText { get; init; } = ""; public string LastSeen { get; init; } = "";
    }
}
