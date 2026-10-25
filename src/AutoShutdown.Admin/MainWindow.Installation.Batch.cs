using AutoShutdown.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace AutoShutdown.Admin;

public partial class MainWindow
{
    private void InstallationPage_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (!SoftwareInstallPanel.IsVisible || e.Delta == 0 || ApprovedPackagesCombo.IsDropDownOpen || BatchHistoryCombo.IsDropDownOpen
            || (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0) return;
        // Handle on the parent tunneling route before DataGrid, ComboBox or TextBox handles it.
        // Ctrl+wheel remains available for local table/field scrolling.
        e.Handled = true;
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0 || MainPageScrollViewer.ScrollableHeight <= 0) return;
        var distance = lines < 0 ? MainPageScrollViewer.ViewportHeight : lines * 16d;
        MainPageScrollViewer.ScrollToVerticalOffset(MainPageScrollViewer.VerticalOffset - e.Delta / 120d * distance);
    }

    private bool _suppressInstallBringIntoView;
    private void SuppressInstallRefreshBringIntoView()
    {
        _suppressInstallBringIntoView = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => _suppressInstallBringIntoView = false));
    }
    private void InstallationTable_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // Refreshing a selected row must not pull the whole page back to the table.
        if (_suppressInstallBringIntoView) e.Handled = true;
    }

    private readonly ObservableCollection<InstallTargetRow> _installTargets = new();
    private readonly ObservableCollection<BatchResultRow> _batchRows = new();
    private string? _currentBatchId;
    private InstallBatchResult? _currentBatch;
    private InstallBatchRequest? _pendingBatchRequest;
    private InstallRetryRequest? _pendingRetryRequest;
    private bool _installTargetsUpdating;
    private bool _batchChoicesUpdating;

    private void SynchronizeInstallTargets(List<DeploymentDevice> devices)
    {
        _installTargetsUpdating = true;
        try
        {
            var visible = devices.Where(device => !device.Removed).OrderBy(device => device.ComputerName).ToList();
            var ids = visible.Select(device => device.Id).ToHashSet();
            foreach (var obsolete in _installTargets.Where(row => !ids.Contains(row.Id)).ToArray())
            {
                SuppressInstallRefreshBringIntoView();
                _installTargets.Remove(obsolete);
            }
            foreach (var device in visible)
            {
                var row = _installTargets.FirstOrDefault(row => row.Id == device.Id);
                if (row is null)
                {
                    SuppressInstallRefreshBringIntoView();
                    row = new(InstallTargetSelectionChanged) { Id = device.Id };
                    _installTargets.Add(row);
                }
                var latest = _installOverview?.Jobs.FirstOrDefault(job => job.DeviceId == device.Id);
                var available = device.Enabled && device.Online && !(_installOverview?.Jobs.Any(job => job.DeviceId == device.Id && !SevenZipPolicy.IsTerminal(job.State)) == true);
                row.Update(device, latest, available);
            }
        }
        finally { _installTargetsUpdating = false; }
        UpdateInstallSelectionText();
    }

    private void InstallTargetSelectionChanged()
    {
        if (_installTargetsUpdating) return;
        _pendingBatchRequest = null;
        UpdateInstallSelectionText();
        SetInstallButtons();
    }
    private void UpdateInstallSelectionText()
    {
        var selected = SelectedDevices();
        InstallSelectionText.Text = selected.Count == 0 ? "No PCs selected." : $"{selected.Count} selected - {selected.Count(row => row.Online)} installer service(s) online.";
    }
    private void SelectOnlineInstallTargets_Click(object sender, RoutedEventArgs e)
    {
        _installTargetsUpdating = true;
        foreach (var row in _installTargets) row.Selected = row.Available;
        _installTargetsUpdating = false;
        InstallTargetSelectionChanged();
    }
    private void ClearInstallTargets_Click(object sender, RoutedEventArgs e)
    {
        _installTargetsUpdating = true;
        foreach (var row in _installTargets) row.Selected = false;
        _installTargetsUpdating = false;
        InstallTargetSelectionChanged();
    }
    private void SynchronizeBatchChoices(List<InstallBatchSummary> batches)
    {
        _batchChoicesUpdating = true;
        try
        {
            var choices = batches.Select(batch => new BatchChoice(batch.Id,
                $"{batch.CreatedUtc.LocalDateTime:dd MMM HH:mm} - {batch.PackageName} - {batch.Accepted} queued / {batch.Rejected} not queued")).ToList();
            if (BatchHistoryCombo.ItemsSource is not IEnumerable<BatchChoice> currentChoices || !currentChoices.SequenceEqual(choices))
                BatchHistoryCombo.ItemsSource = choices;
            if (_currentBatchId is null || !choices.Any(choice => choice.Id == _currentBatchId)) _currentBatchId = choices.FirstOrDefault()?.Id;
            BatchHistoryCombo.SelectedValue = _currentBatchId;
        }
        finally { _batchChoicesUpdating = false; }
    }
    private async void BatchHistory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_batchChoicesUpdating || _installBusy) return;
        _currentBatchId = (BatchHistoryCombo.SelectedItem as BatchChoice)?.Id;
        _pendingRetryRequest = null;
        try { await RefreshCurrentBatchAsync(); }
        catch (Exception ex) { InstallStatusText.Text = "Batch refresh failed: " + ex.Message; }
        SetInstallButtons();
    }
    private async Task RefreshCurrentBatchAsync()
    {
        var id = _currentBatchId;
        if (id is null)
        {
            _currentBatch = null; _batchRows.Clear();
            InstallStageText.Text = "No batch selected"; InstallProgress.Value = 0;
            InstallStatusText.Text = "Select PCs and software to begin. Each PC reports its own verified result.";
            return;
        }
        var result = await _installApi.BatchAsync(id);
        if (_currentBatchId == id && !_installBusy) ApplyBatchResult(result);
    }
    private void ApplyBatchResult(InstallBatchResult result)
    {
        _currentBatch = result;
        var rows = result.Items.Select(item => new BatchResultRow(item.ComputerName, item.Job?.State.ToString() ?? "Not queued", item.Job?.InstalledVersion ?? "",
            item.Job?.Details ?? item.Details, item.Job?.State switch
            {
                InstallState.Downloading => 1, InstallState.Verifying => 2, InstallState.Installing => 3,
                InstallState.Installed or InstallState.AlreadyInstalled => 4, _ => 0
            })).ToList();
        if (!_batchRows.SequenceEqual(rows))
        {
            SuppressInstallRefreshBringIntoView();
            _batchRows.Clear();
            foreach (var row in rows) _batchRows.Add(row);
        }
        var jobs = result.Items.Where(item => item.Job is not null).Select(item => item.Job!).ToList();
        var verified = jobs.Count(job => job.State is InstallState.Installed or InstallState.AlreadyInstalled);
        var failed = jobs.Count(job => job.State == InstallState.Failed);
        var review = jobs.Count(job => job.State == InstallState.NeedsReview);
        var active = jobs.Count(job => !SevenZipPolicy.IsTerminal(job.State));
        var rejected = result.Items.Count - jobs.Count;
        InstallStageText.Text = $"{result.Items.Count} PC(s): {verified} verified, {active} in progress, {failed} failed, {review} need review, {rejected} not queued";
        var terminal = jobs.Count(job => SevenZipPolicy.IsTerminal(job.State));
        InstallProgress.Value = result.Items.Count == 0 ? 0 : 100d * (terminal + rejected) / result.Items.Count;
        InstallStatusText.Text = active > 0 ? "Installation results update as the Client services report their stages."
            : failed > 0 ? "Batch finished with failures. Check the details, then retry confirmed failed PCs."
            : review > 0 ? "Check NeedsReview PCs before submitting another installation."
            : rejected > 0 ? "Some PCs were not queued. Resolve their listed issue and submit those PCs again."
            : jobs.Count > 0 ? "The Client services verified all installations in this batch." : "No installation was queued.";
    }
    private List<InstallJob> RetryableBatchJobs() => _currentBatch?.Items.Where(item => item.Job?.State == InstallState.Failed)
        .Select(item => item.Job!).Where(job => _installOverview?.Jobs.FirstOrDefault(attempt => attempt.DeviceId == job.DeviceId)?.Id == job.Id
            && !(_installOverview?.Jobs.Any(attempt => attempt.RetryOfJobId == job.Id) == true)).ToList() ?? new();

    private bool _installHistoryUpdating;
    private void InstallHistory_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_installHistoryUpdating) return;
        _pendingRetryRequest = null;
        SetInstallButtons();
    }
    private async void RetrySelectedFailure_Click(object sender, RoutedEventArgs e)
    {
        var selected = InstallJobsGrid.SelectedItem as InstallRow;
        var job = _installOverview?.Jobs.FirstOrDefault(job => job.Id == selected?.Id);
        if (job is not { State: InstallState.Failed }) { ShowError("Select a confirmed Failed attempt in Installation history."); return; }
        await QueueFailedRetryAsync(new() { job });
    }
    private async void RetryFailedInstallations_Click(object sender, RoutedEventArgs e) => await QueueFailedRetryAsync(RetryableBatchJobs());
    private async Task QueueFailedRetryAsync(List<InstallJob> failed)
    {
        if (_installBusy) return;
        if (_pendingRetryRequest is null && failed.Count == 0) { ShowError("No confirmed failed attempts are available to retry."); return; }
        var count = _pendingRetryRequest?.JobIds.Count ?? failed.Count;
        if (MessageBox.Show(this, $"Retry {count} failed PC(s)? Successful installations will remain unchanged.",
            "Retry failed installations", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _installBusy = true; SetInstallButtons();
        try
        {
            _pendingRetryRequest ??= new(Guid.NewGuid().ToString("N"), failed.Select(job => job.Id).ToList());
            var result = await _installApi.RetryFailedAsync(_pendingRetryRequest);
            _currentBatchId = result.Id; _pendingRetryRequest = null;
            ApplyBatchResult(result);
        }
        catch (Exception ex)
        {
            InstallStatusText.Text = "Retry request did not complete: " + ex.Message + " Refresh to inspect results; retrying this request reuses its identifier.";
            ShowError(InstallStatusText.Text);
        }
        finally { _installBusy = false; await RefreshInstallDataAsync(); }
    }

    private sealed class InstallTargetRow(Action changed) : INotifyPropertyChanged
    {
        private bool _selected;
        public string Id { get; init; } = "";
        public string ComputerName { get; private set; } = "";
        public string Display => ComputerName;
        public bool Online { get; private set; }
        public bool Enabled { get; private set; }
        public bool Available { get; private set; }
        public string ServiceVersion { get; private set; } = "";
        public string LatestStatus { get; private set; } = "";
        public string StatusText => !Enabled ? "DISABLED" : Online ? "ONLINE" : "OFFLINE";
        public bool Selected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); changed(); }
        }
        public void Update(DeploymentDevice device, InstallJob? latest, bool available)
        {
            var status = latest is null ? "No installation yet" : latest.Package.Name + ": " + latest.State;
            if (ComputerName == device.ComputerName && Online == device.Online && Enabled == device.Enabled
                && Available == available && ServiceVersion == device.ServiceVersion && LatestStatus == status) return;
            ComputerName = device.ComputerName; Online = device.Online; Enabled = device.Enabled;
            Available = available; ServiceVersion = device.ServiceVersion; LatestStatus = status;
            if (!Enabled) Selected = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private sealed record BatchChoice(string Id, string Display);
    private sealed record BatchResultRow(string Computer, string Status, string Version, string Details, int Stage);
}
