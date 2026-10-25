using AutoShutdown.Admin.Infrastructure;
using AutoShutdown.Admin.Services;
using AutoShutdown.Shared;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AutoShutdown.Admin;

public partial class MainWindow : Window
{
    private readonly AdminApiClient _api;
    private readonly ServerOptions _serverOptions;
    private readonly DispatcherTimer _refreshTimer;
    private readonly ObservableCollection<ClientRow> _clients = new();
    private readonly ObservableCollection<ScheduleRow> _scheduleRows = new();
    private readonly ObservableCollection<LogRow> _logRows = new();
    private List<GroupDefinition> _groups = new();
    private List<ScheduleDefinition> _schedules = new();
    private GlobalSettings _settings = new();
    private string? _selectedMessageImagePath;
    private string? _pendingIdleImageMediaId;
    private bool _sidebarCollapsed;
    private bool _startupCheckLoaded;
    private bool _refreshingLogs;
    private bool _refreshCycleBusy;

    public MainWindow(AdminApiClient api, ServerOptions serverOptions, InstallApiClient installApi, AdminIdentity identity, DateTimeOffset expiresUtc)
    {
        InitializeComponent();
        Title = "L&T Admin";
        _identity=identity;_sessionExpires=expiresUtc;
        AdminIdentityText.Text=identity.Label;
        DashboardWelcomeText.Text="Welcome, " + identity.DisplayName;
        InitializeManagement();
        _api = api;
        _serverOptions = serverOptions;
        _installApi = installApi;

        ClientsGrid.ItemsSource = _clients;
        DashboardClientsGrid.ItemsSource = _deviceRows;
        SchedulesGrid.ItemsSource = _scheduleRows;
        LogsGrid.ItemsSource = _logRows;
        InitializeDeploymentPage();
        InitializeInstallPage();
        InitializeAdminInactivity();

        ScheduleActionCombo.ItemsSource = new[] { "SEND MESSAGE", "PAUSE", "RESUME", "SHUTDOWN", "CANCEL SHUTDOWN" };
        ScheduleActionCombo.SelectedIndex = 0;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _refreshTimer.Tick += async (_, _) =>
        {
            if(_refreshCycleBusy || _loggingOut || AppExitState.IsClosing)return;
            _refreshCycleBusy=true;
            try{
                if(DateTimeOffset.UtcNow>=_sessionExpires){await ((App)Application.Current).LogoutAsync();return;}
                await RefreshLiveDataAsync();
                if(_loggingOut || AppExitState.IsClosing)return;
                await RefreshInstallDataAsync();
                if(_loggingOut || AppExitState.IsClosing)return;
                await RefreshManagementAsync();
                if(LogsPage.Visibility==Visibility.Visible)await RefreshLogsAsync();
            }finally{_refreshCycleBusy=false;}
        };

        Loaded += async (_, _) =>
        {
            SidebarEndpointText.Text = "Encrypted Admin session";
            await LoadAllAsync();
            if(_loggingOut || AppExitState.IsClosing)return;
            _startupCheckLoaded = true;
            AdminStartupCheck.IsChecked = InstalledAdminStartupCheck.IsChecked = StartupManager.IsEnabled();
            await RefreshInstallDataAsync();await RefreshManagementAsync();
            Navigate(DashboardPage,"Dashboard","Your connected PCs, at a glance.");
            if(!_loggingOut && !AppExitState.IsClosing)_refreshTimer.Start();
        };

        Closing += (_, _) => PrepareForExit();
        Closed += (_, _) => { _refreshTimer.Stop(); if(!_loggingOut)Application.Current.Shutdown(); };
    }

    private async Task LoadAllAsync()
    {
        try
        {
            _settings = await _api.GetSettingsAsync() ?? new GlobalSettings();
            _adminPolicyLoaded = true;
            _groups = await _api.GetGroupsAsync();
            _schedules = await _api.GetSchedulesAsync();
            PopulateSettings();
            PopulateGroups();
            PopulateSchedules();
            PopulateTargetSelectors();
            await RefreshLiveDataAsync();
            await RefreshIdleArtworkPreviewAsync();
            SetServerState(true);
        }
        catch (Exception ex)
        {
            if(_loggingOut)return;
            SetServerState(false);
            ShowError(ex.Message);
        }
    }

    private async Task RefreshLiveDataAsync()
    {
        try
        {
            var data = await _api.GetClientsAsync();
            _clients.Clear();
            foreach (var c in data) _clients.Add(ClientRow.From(c));
            PopulateTargetSelectors(preserveSelections: true);
            PopulateGroupClientCombo();
            PopulateDeploymentClients();
            SetServerState(true);
        }
        catch(System.Net.Http.HttpRequestException ex) when(ex.StatusCode==System.Net.HttpStatusCode.Unauthorized)
        {await ((App)Application.Current).LogoutAsync();}
        catch
        {
            SetServerState(false);
        }
    }

    private void SetServerState(bool online)
    {
        SidebarHealthText.Text = online ? "SERVER ONLINE" : "SERVER OFFLINE";
        HeaderStatusText.Text = online ? "Ready" : "Connection unavailable";
        DashboardServerStatus.Text = online ? "ONLINE" : "OFFLINE";
        SidebarHealthDot.Fill = (System.Windows.Media.Brush)FindResource(online ? "BrushSuccess" : "BrushDanger");
        HeaderStatusText.Foreground = (System.Windows.Media.Brush)FindResource(online ? "BrushCyan" : "BrushDanger");
        DashboardServerStatus.Foreground = (System.Windows.Media.Brush)FindResource(online ? "BrushSuccess" : "BrushDanger");
    }

    private void PopulateSettings()
    {
        MonitoringEnabledCheck.IsChecked = _settings.MonitoringEnabled;
        IdleScreenEnabledCheck.IsChecked = _settings.IdleScreenEnabled;
        IdleMinutesBox.Text = _settings.IdleScreenMinutes.ToString("0.###", CultureInfo.InvariantCulture);
        ShutdownHoursBox.Text = _settings.ShutdownHours.ToString("0.###", CultureInfo.InvariantCulture);
        WarningSecondsBox.Text = _settings.ShutdownWarningSeconds.ToString(CultureInfo.InvariantCulture);
        IdleMessageBox.Text = _settings.IdleMessage;
        _pendingIdleImageMediaId = _settings.IdleImageMediaId;
        UpdateRemoveIdleImageButton();
    }

    private void PopulateTargetSelectors(bool preserveSelections = false)
    {
        var oldClient = preserveSelections ? (ClientTargetCombo.SelectedItem as TargetChoice)?.Key : null;
        var oldMessage = preserveSelections ? (MessageTargetCombo.SelectedItem as TargetChoice)?.Key : null;
        var oldSchedule = preserveSelections ? (ScheduleTargetCombo.SelectedItem as TargetChoice)?.Key : null;

        var choices = new List<TargetChoice> { new("ALL", "ALL CLIENTS", TargetSpec.All()) };
        choices.AddRange(_clients.Select(c => new TargetChoice("CLIENT:" + c.ComputerName, c.ComputerName + (c.OnlineText == "ONLINE" ? " (ONLINE - " + c.MonitoringText + ")" : " (OFFLINE)"), TargetSpec.Client(c.ComputerName))));
        choices.AddRange(_groups.OrderBy(g => g.Name).Select(g => new TargetChoice("GROUP:" + g.Name, "GROUP: " + g.Name, TargetSpec.Group(g.Name))));

        SetComboItems(ClientTargetCombo, choices, oldClient);
        SetComboItems(MessageTargetCombo, choices, oldMessage);
        SetComboItems(ScheduleTargetCombo, choices, oldSchedule);
    }

    private static void SetComboItems(ComboBox combo, IReadOnlyList<TargetChoice> choices, string? preferred)
    {
        combo.ItemsSource = choices;
        combo.DisplayMemberPath = nameof(TargetChoice.Display);
        combo.SelectedValuePath = nameof(TargetChoice.Key);
        combo.SelectedValue = preferred is not null && choices.Any(x => x.Key == preferred) ? preferred : "ALL";
    }

    private TargetSpec SelectedTarget(ComboBox combo) => (combo.SelectedItem as TargetChoice)?.Target ?? TargetSpec.All();

    private async Task SendActionAsync(ActionKind kind, TargetSpec? target = null, string title = "", string text = "", string? mediaId = null)
    {
        try
        {
            var result = await _api.SendActionAsync(new AdminActionRequest
            {
                Target = target ?? SelectedTarget(ClientTargetCombo),
                Kind = kind,
                Title = title,
                Text = text,
                MediaId = mediaId
            });
            HeaderStatusText.Text = result.Summary.ToUpperInvariant();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e) => await SendActionAsync(ActionKind.Pause);
    private async void ResumeButton_Click(object sender, RoutedEventArgs e) => await SendActionAsync(ActionKind.Resume);

    private async void ShutdownButton_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedTarget(ClientTargetCombo);
        if (MessageBox.Show($"Send shutdown to {target}?", "Confirm shutdown", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            await SendActionAsync(ActionKind.Shutdown, target);
    }

    private async void CancelShutdownButton_Click(object sender, RoutedEventArgs e) => await SendActionAsync(ActionKind.CancelShutdown);

    private async void SendTextButton_Click(object sender, RoutedEventArgs e)
    {
        var text = MessageTextBox.Text.Trim();
        if (text.Length == 0) { ShowError("Enter a message first."); return; }
        await SendActionAsync(ActionKind.TextMessage, SelectedTarget(MessageTargetCombo), "Message from " + _identity.Label, text);
        MessageTextBox.Clear();
    }

    private void SelectMessageImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp" };
        if (dialog.ShowDialog() != true) return;
        _selectedMessageImagePath = dialog.FileName;
        SelectedImageNameText.Text = Path.GetFileName(dialog.FileName);
        MessageImagePreview.Source = LoadBitmap(dialog.FileName);
    }

    private async void SendImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedMessageImagePath) || !File.Exists(_selectedMessageImagePath))
        {
            ShowError("Select an image first.");
            return;
        }

        try
        {
            HeaderStatusText.Text = "UPLOADING MEDIA…";
            var asset = await _api.UploadImageAsync(_selectedMessageImagePath);
            await SendActionAsync(ActionKind.ImageMessage, SelectedTarget(MessageTargetCombo), "Image from " + _identity.Label, ImageCaptionTextBox.Text.Trim(), asset.Id);
            _selectedMessageImagePath = null;
            SelectedImageNameText.Text = "No file selected";
            MessageImagePreview.Source = null;
            ImageCaptionTextBox.Clear();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void PopulateGroups()
    {
        var selected = GroupsListBox.SelectedItem as string;
        GroupsListBox.ItemsSource = _groups.Select(g => g.Name).OrderBy(x => x).ToArray();
        if (selected is not null && _groups.Any(g => g.Name == selected)) GroupsListBox.SelectedItem = selected;
        PopulateGroupClientCombo();
        RefreshGroupMembers();
    }

    private void PopulateGroupClientCombo()
    {
        GroupClientCombo.ItemsSource = _clients.Select(x => x.ComputerName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        if (GroupClientCombo.Items.Count > 0 && GroupClientCombo.SelectedIndex < 0) GroupClientCombo.SelectedIndex = 0;
    }

    private void RefreshGroupMembers()
    {
        var name = GroupsListBox.SelectedItem as string;
        var group = _groups.FirstOrDefault(g => g.Name == name);
        GroupMembersListBox.ItemsSource = group?.Members.OrderBy(x => x).ToArray() ?? Array.Empty<string>();
    }

    private async void CreateGroupButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NewGroupNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_groups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))) { ShowError("That group already exists."); return; }
        _groups.Add(new GroupDefinition { Name = name });
        _groups = await _api.SaveGroupsAsync(_groups);
        NewGroupNameBox.Clear();
        PopulateGroups();
        PopulateTargetSelectors(true);
    }

    private async void DeleteGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (GroupsListBox.SelectedItem is not string name) return;
        if (MessageBox.Show($"Delete group '{name}'?", "Delete group", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _groups = _groups.Where(g => !string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        _groups = await _api.SaveGroupsAsync(_groups);
        PopulateGroups();
        PopulateTargetSelectors(true);
    }

    private async void AddGroupMemberButton_Click(object sender, RoutedEventArgs e)
    {
        if (GroupsListBox.SelectedItem is not string groupName || GroupClientCombo.SelectedItem is not string computer) return;
        var group = _groups.First(g => g.Name == groupName);
        if (!group.Members.Contains(computer, StringComparer.OrdinalIgnoreCase)) group.Members.Add(computer);
        _groups = await _api.SaveGroupsAsync(_groups);
        PopulateGroups();
    }

    private async void RemoveGroupMemberButton_Click(object sender, RoutedEventArgs e)
    {
        if (GroupsListBox.SelectedItem is not string groupName || GroupMembersListBox.SelectedItem is not string computer) return;
        var group = _groups.First(g => g.Name == groupName);
        group.Members.RemoveAll(x => string.Equals(x, computer, StringComparison.OrdinalIgnoreCase));
        _groups = await _api.SaveGroupsAsync(_groups);
        PopulateGroups();
    }

    private void GroupsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshGroupMembers();

    private void PopulateSchedules()
    {
        _scheduleRows.Clear();
        foreach (var schedule in _schedules.OrderBy(x => x.Time).ThenBy(x => x.Name))
            _scheduleRows.Add(ScheduleRow.From(schedule));
    }

    private async void AddScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        var name = ScheduleNameBox.Text.Trim();
        if (name.Length == 0) { ShowError("Enter a schedule name."); return; }
        if (!TimeSpan.TryParseExact(ScheduleTimeBox.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out _)) { ShowError("Time must be HH:mm, for example 20:00."); return; }
        var days = new List<string>();
        if (DayMon.IsChecked == true) days.Add("Mon"); if (DayTue.IsChecked == true) days.Add("Tue"); if (DayWed.IsChecked == true) days.Add("Wed");
        if (DayThu.IsChecked == true) days.Add("Thu"); if (DayFri.IsChecked == true) days.Add("Fri"); if (DaySat.IsChecked == true) days.Add("Sat"); if (DaySun.IsChecked == true) days.Add("Sun");
        if (days.Count == 0) { ShowError("Select at least one day."); return; }
        var action = ActionFromText(ScheduleActionCombo.SelectedItem?.ToString());
        if (action == ActionKind.TextMessage && string.IsNullOrWhiteSpace(ScheduleMessageBox.Text)) { ShowError("Enter the scheduled message."); return; }

        _schedules.Add(new ScheduleDefinition
        {
            Name = name,
            Target = SelectedTarget(ScheduleTargetCombo),
            Action = action,
            Time = ScheduleTimeBox.Text.Trim(),
            Days = days,
            Message = ScheduleMessageBox.Text.Trim(),
            Enabled = ScheduleEnabledCheck.IsChecked == true
        });
        _schedules = await _api.SaveSchedulesAsync(_schedules);
        ScheduleNameBox.Clear(); ScheduleMessageBox.Clear();
        PopulateSchedules();
    }

    private async void ToggleScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SchedulesGrid.SelectedItem is not ScheduleRow row) return;
        var item = _schedules.FirstOrDefault(x => x.Id == row.Id); if (item is null) return;
        var replacement = item with { Enabled = !item.Enabled };
        _schedules[_schedules.IndexOf(item)] = replacement;
        _schedules = await _api.SaveSchedulesAsync(_schedules);
        PopulateSchedules();
    }

    private async void DeleteScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SchedulesGrid.SelectedItem is not ScheduleRow row) return;
        _schedules = _schedules.Where(x => x.Id != row.Id).ToList();
        _schedules = await _api.SaveSchedulesAsync(_schedules);
        PopulateSchedules();
    }

    private void ScheduleActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ScheduleMessageBox.IsEnabled = ActionFromText(ScheduleActionCombo.SelectedItem?.ToString()) == ActionKind.TextMessage;
    }

    private static ActionKind ActionFromText(string? text) => text switch
    {
        "SEND MESSAGE" => ActionKind.TextMessage,
        "PAUSE" => ActionKind.Pause,
        "RESUME" => ActionKind.Resume,
        "SHUTDOWN" => ActionKind.Shutdown,
        "CANCEL SHUTDOWN" => ActionKind.CancelShutdown,
        _ => ActionKind.TextMessage
    };

    private async Task RefreshLogsAsync()
    {
        if(_refreshingLogs || !_identity.Can(AdminPermission.ReadLogs))return;
        _refreshingLogs=true;
        try{var audit=await _api.AuditAsync();LogsGrid.ItemsSource=audit;}
        catch(Exception ex){HeaderStatusText.Text="Activity log unavailable: "+ex.Message;}
        finally{_refreshingLogs=false;}
    }
    private async void RefreshLogsButton_Click(object sender, RoutedEventArgs e) => await RefreshLogsAsync();
    private async void LogSourceFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) await RefreshLogsAsync(); }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_idleArtworkBusy) return;
        if (!double.TryParse(IdleMinutesBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var idle) || !double.IsFinite(idle) || idle <= 0 || idle > 10080) { ShowError("Idle minutes must be a positive number."); return; }
        if (!double.TryParse(ShutdownHoursBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) || !double.IsFinite(hours) || hours <= 0 || hours > 168) { ShowError("Shutdown hours must be a positive number."); return; }
        if (!int.TryParse(WarningSecondsBox.Text, out var warning) || warning < 1 || warning > 3600) { ShowError("Warning seconds must be at least 1."); return; }

        var proposed = new GlobalSettings
        {
            IdleScreenMinutes = idle,
            ShutdownHours = hours,
            ShutdownWarningSeconds = warning,
            MonitoringEnabled = MonitoringEnabledCheck.IsChecked == true,
            IdleScreenEnabled = IdleScreenEnabledCheck.IsChecked == true,
            IdleMessage = IdleMessageBox.Text.Trim(),
            IdleImageMediaId = _pendingIdleImageMediaId
        };
        try
        {
            if(!_identity.IsSuper)throw new UnauthorizedAccessException("Super Admin permission is required for global inactivity settings.");
            if(proposed.MonitoringEnabled && MessageBox.Show(this,
                "Save and broadcast automatic shutdown settings to this Admin PC and all connected Clients?",
                "Confirm automatic shutdown",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            _idleArtworkBusy = true;
            UpdateRemoveIdleImageButton();
            _settings=await _api.SaveSettingsAsync(proposed);
            _adminPolicyLoaded=true;
            PolicyStatusText.Text="Settings saved and broadcast. Offline Clients receive them when they reconnect.";
            PopulateSettings();
        }
        catch(Exception ex){ShowError(ex.Message);}
        finally { _idleArtworkBusy = false; UpdateRemoveIdleImageButton(); }

    }

    private async void UploadIdleImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_idleArtworkBusy) return;
        var dialog = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _idleArtworkBusy = true;
            UpdateRemoveIdleImageButton();
            var asset = await _api.UploadImageAsync(dialog.FileName);
            _pendingIdleImageMediaId = asset.Id;
            UpdateRemoveIdleImageButton();
            IdleArtworkPreview.Source = LoadBitmap(dialog.FileName);
            HeaderStatusText.Text = "IDLE IMAGE UPLOADED — SAVE SETTINGS TO BROADCAST";
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _idleArtworkBusy = false; UpdateRemoveIdleImageButton(); }
    }

    private bool _idleArtworkBusy;
    private void UpdateRemoveIdleImageButton() => RemoveIdleImageButton.IsEnabled =
        _identity.IsSuper && !_idleArtworkBusy && !string.IsNullOrWhiteSpace(_pendingIdleImageMediaId);

    private async void RemoveIdleImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_idleArtworkBusy || !_identity.IsSuper) return;
        _idleArtworkBusy = true;
        UpdateRemoveIdleImageButton();
        try
        {
            // Preserve the saved thresholds and monitoring state; this removes only the artwork.
            var saved = await _api.GetSettingsAsync() ?? throw new InvalidOperationException("Could not read the current settings.");
            _settings = await _api.SaveSettingsAsync(saved with { IdleImageMediaId = null });
            _pendingIdleImageMediaId = null;
            IdleArtworkPreview.Source = null;
            PolicyStatusText.Text = "Idle image removed and saved. Clients receive the change when connected; an already open idle screen refreshes when it next opens.";
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _idleArtworkBusy = false; UpdateRemoveIdleImageButton(); }
    }

    private async Task RefreshIdleArtworkPreviewAsync()
    {
        var mediaId = _settings.IdleImageMediaId;
        if (string.IsNullOrWhiteSpace(mediaId)) { IdleArtworkPreview.Source = null; return; }
        var bytes = await _api.GetMediaBytesAsync(mediaId);
        if (bytes is not null && _pendingIdleImageMediaId == mediaId) IdleArtworkPreview.Source = LoadBitmap(bytes);
    }

    private void AdminStartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_startupCheckLoaded) return;
        try { StartupManager.SetEnabled(AdminStartupCheck.IsChecked == true); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void ClientsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClientsGrid.SelectedItem is ClientRow row)
            ClientTargetCombo.SelectedValue = "CLIENT:" + row.ComputerName;
    }

    private void ClientTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void Navigate(UIElement page, string title, string subtitle)
    {
        foreach (var item in new UIElement[] { DashboardPage, ClientsPage, MessagingPage, GroupsPage, SchedulesPage, DeploymentsPage, LogsPage, SettingsPage, PolicyPage, AccountsPage }) item.Visibility = Visibility.Collapsed;
        var navigation=new (Button Button, UIElement Page)[]{(NavDashboard,DashboardPage),(NavClients,ClientsPage),(NavDeployments,DeploymentsPage),
            (NavAutomation,PolicyPage),(NavAccounts,AccountsPage),(NavLogs,LogsPage),(NavSettings,SettingsPage),(NavMessaging,MessagingPage),(NavGroups,GroupsPage),(NavSchedules,SchedulesPage)};
        foreach(var item in navigation)item.Button.Style=(Style)FindResource(item.Page==page?"ActiveNavButton":"NavButton");
        page.Visibility = Visibility.Visible;
        PageTitleText.Text = title;
        PageSubtitleText.Text = subtitle;
    }

    private void NavDashboard_Click(object sender, RoutedEventArgs e) => Navigate(DashboardPage, "Dashboard", "Your connected PCs, at a glance.");
    private void NavClients_Click(object sender, RoutedEventArgs e) => Navigate(ClientsPage, "Client PCs", "Connection status and controls for your assigned PCs");
    private void NavMessaging_Click(object sender, RoutedEventArgs e) => Navigate(MessagingPage, "Messages & images", "Send a message or image to authorized PCs");
    private void NavGroups_Click(object sender, RoutedEventArgs e) => Navigate(GroupsPage, "Groups", "Organize client PCs into groups");
    private void NavSchedules_Click(object sender, RoutedEventArgs e) => Navigate(SchedulesPage, "Schedules", "Authorized timed actions and message delivery");
    private async void NavLogs_Click(object sender, RoutedEventArgs e) { Navigate(LogsPage, "Activity Logs", "Administrator actions, client acknowledgements and installation results"); await RefreshLogsAsync(); }
    private void NavSettings_Click(object sender, RoutedEventArgs e) => Navigate(SettingsPage, "Settings", "Network, client registration and startup");

    private void CollapseSidebarButton_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        SidebarColumn.Width = new GridLength(_sidebarCollapsed ? 76 : 250);
        NavDashboard.Content = _sidebarCollapsed ? "◈" : "◈   DASHBOARD";
        NavClients.Content = _sidebarCollapsed ? "▦" : "▦   CLIENTS";
        NavMessaging.Content = _sidebarCollapsed ? "✦" : "✦   MESSAGING";
        NavGroups.Content = _sidebarCollapsed ? "⬡" : "⬡   GROUPS";
        NavSchedules.Content = _sidebarCollapsed ? "◷" : "◷   SCHEDULES";
        NavDeployments.Content = _sidebarCollapsed ? "↓" : "↓   DEPLOYMENTS";
        NavLogs.Content = _sidebarCollapsed ? "≡" : "≡   LOGS";
        NavSettings.Content = _sidebarCollapsed ? "⚙" : "⚙   SETTINGS";
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void RestoreButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private static BitmapImage LoadBitmap(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return LoadBitmap(bytes);
    }

    private static BitmapImage LoadBitmap(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = ms; image.EndInit(); image.Freeze();
        return image;
    }

    private static void ShowError(string message)
    {
        if (AppExitState.IsClosing) { AppDiagnostics.Write("SystemA", "EXIT_UI_ERROR", message); return; }
        MessageBox.Show(message, "L&T Admin", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private sealed record TargetChoice(string Key, string Display, TargetSpec Target);

    private sealed record ClientRow
    {
        public required string ComputerName { get; init; }
        public string GroupsText { get; init; } = "";
        public string OnlineText { get; init; } = "";
        public string IdleText { get; init; } = "";
        public string MonitoringText { get; init; } = "";
        public string IdleScreenText { get; init; } = "";
        public string ShutdownText { get; init; } = "";
        public string LastSeenText { get; init; } = "";

        public static ClientRow From(ClientSnapshot x) => new()
        {
            ComputerName = x.ComputerName,
            GroupsText = string.Join(", ", x.Groups),
            OnlineText = x.Online ? "ONLINE" : "OFFLINE",
            IdleText = TimeSpan.FromSeconds(Math.Max(0, x.IdleSeconds)).ToString(@"hh\:mm\:ss"),
            MonitoringText = x.RemotePaused ? "PAUSED" : x.MonitoringEnabled ? "ACTIVE" : "OFF",
            IdleScreenText = !x.IdleScreenEnabled ? "DISABLED" : x.IdleScreenVisible ? "VISIBLE" : "READY",
            ShutdownText = x.ShutdownScheduled ? "PENDING" : "—",
            LastSeenText = x.LastSeen.LocalDateTime.ToString("HH:mm:ss")
        };
    }

    private sealed record ScheduleRow
    {
        public required string Id { get; init; }
        public string Name { get; init; } = "";
        public string TargetText { get; init; } = "";
        public string ActionText { get; init; } = "";
        public string Time { get; init; } = "";
        public string DaysText { get; init; } = "";
        public string EnabledText { get; init; } = "";
        public string LastRunText { get; init; } = "—";
        public static ScheduleRow From(ScheduleDefinition x) => new()
        {
            Id = x.Id, Name = x.Name, TargetText = x.Target.ToString(), ActionText = x.Action.ToString(), Time = x.Time,
            DaysText = string.Join(" ", x.Days), EnabledText = x.Enabled ? "YES" : "NO", LastRunText = x.LastRun?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "—"
        };
    }

    private sealed record LogRow
    {
        public string TimeText { get; init; } = "";
        public string Source { get; init; } = "";
        public string Event { get; init; } = "";
        public string Target { get; init; } = "";
        public string Details { get; init; } = "";
        public static LogRow From(LogEntry x) => new() { TimeText = x.At.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), Source = x.Source, Event = x.Event, Target = x.Target, Details = x.Details };
    }
}
