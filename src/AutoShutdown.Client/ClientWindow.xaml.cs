using AutoShutdown.Client.Services;
using AutoShutdown.Shared;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AutoShutdown.Client;

public partial class ClientWindow : Window
{
    private readonly ClientAgent _agent;
    private readonly DispatcherTimer _timer;
    private IdleOverlayWindow? _idleOverlay;
    private bool _startupLoaded;
    private volatile bool _exiting;

    public ClientWindow(ClientAgent agent)
    {
        InitializeComponent();
        _agent = agent;
        Title = "L&T Client 0.5.5 recovered";
        MachineNameText.Text = $"CLIENT  •  {agent.ComputerName}";

        _agent.StateChanged += () => QueueUi(UpdateUi);
        _agent.SettingsChanged += _ => QueueUi(() =>
        {
            if (!_agent.Settings.IdleScreenEnabled) CloseIdleOverlay();
            UpdateUi();
        });
        _agent.TextMessageReceived += async envelope =>
        {
            if (_exiting || AppExitState.IsClosing) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_exiting || AppExitState.IsClosing) return;
                CloseIdleOverlay();
                new MessagePopupWindow(envelope.Title, envelope.Text).Show();
            });
        };
        _agent.ImageMessageReceived += async envelope =>
        {
            if (_exiting || AppExitState.IsClosing) return;
            var bytes = await _agent.DownloadMediaAsync(envelope.MediaId!);
            if (_exiting || AppExitState.IsClosing) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_exiting || AppExitState.IsClosing) return;
                CloseIdleOverlay();
                new ImagePopupWindow(envelope.Title, envelope.Text, bytes).Show();
            });
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();

        Loaded += (_, _) =>
        {
            _startupLoaded = true;
            StartupCheck.IsChecked = StartupManager.IsEnabled();
            StartupCheck.IsEnabled = !StartupManager.IsManaged;
            if (StartupManager.IsManaged) StartupCheck.ToolTip = "Startup is enabled for all users by Client setup.";
            UpdateUi();
            SleepGuard.SetActive(_agent.Settings.MonitoringEnabled && !_agent.RemotePaused);
            _timer.Start();
            _ = _agent.SendLogAsync("CLIENT_STARTED", "Client UI started");
        };

        Closing += (_, args) =>
        {
            AppExitState.BeginClosing();
            PrepareForExit();
            if (AppExitState.CancelIdleOnWindowClose(_agent.ShutdownScheduled
                && string.Equals(_agent.ShutdownReason, "idle", StringComparison.OrdinalIgnoreCase), AppExitState.IsSessionEnding))
                _agent.CancelShutdown();
        };
        Closed += (_, _) => PrepareForExit();
    }

    public void PrepareForExit()
    {
        _exiting = true;
        _timer.Stop();
        SleepGuard.Release();
        CloseIdleOverlay();
    }
    private void QueueUi(Action update)
    {
        if (!AppExitState.CanUpdateUi(_exiting || AppExitState.IsClosing, Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)) return;
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!AppExitState.CanUpdateUi(_exiting || AppExitState.IsClosing, Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)) return;
                update();
            }));
        }
        catch (InvalidOperationException ex) { AppDiagnostics.Record("SystemB", "EXIT_UI_UPDATE", ex); }
    }

    private void Tick()
    {
        if (_exiting || AppExitState.IsClosing) return;
        var settings = _agent.Settings;
        var idle = IdleMonitor.GetIdleTime();
        var installing = DeploymentActivity.IsInstalling();
        if (installing && _agent.ShutdownScheduled) _agent.CancelShutdown();
        var effectiveMonitoring = settings.MonitoringEnabled && !_agent.RemotePaused && !installing;
        SleepGuard.SetActive(effectiveMonitoring || installing);

        IdleTimeText.Text = Format(idle);
        var shutdownLimit = TimeSpan.FromHours(Math.Max(0.001, settings.ShutdownHours));
        var remaining = shutdownLimit - idle;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        RemainingTimeText.Text = effectiveMonitoring ? Format(remaining) : installing ? "INSTALLING" : _agent.RemotePaused ? "PAUSED" : "OFF";

        if (!effectiveMonitoring)
        {
            CloseIdleOverlay();
            if (_agent.ShutdownScheduled && _agent.ShutdownReason == "idle") _agent.CancelShutdown();
            UpdateUi();
            return;
        }

        if (_idleOverlay is not null && idle < TimeSpan.FromSeconds(2)) CloseIdleOverlay();
        if (_agent.ShutdownScheduled && _agent.ShutdownReason == "idle" && idle < TimeSpan.FromSeconds(5)) _agent.CancelShutdown();

        var idleThreshold = TimeSpan.FromMinutes(Math.Max(0.001, settings.IdleScreenMinutes));
        if (settings.IdleScreenEnabled && idle >= idleThreshold && idle < shutdownLimit && _idleOverlay is null)
            OpenIdleOverlay();
        if (!settings.IdleScreenEnabled) CloseIdleOverlay();

        _idleOverlay?.UpdateCountdown(remaining);

        if (InactivityPolicy.ShouldShutdown(settings, idle, _agent.RemotePaused, installing) && !_agent.ShutdownScheduled)
        {
            CloseIdleOverlay();
            _agent.ScheduleShutdown("idle");
        }

        UpdateUi();
    }

    private void OpenIdleOverlay()
    {
        if (_idleOverlay is not null) return;
        _idleOverlay = new IdleOverlayWindow(_agent);
        _idleOverlay.Closed += (_, _) =>
        {
            _idleOverlay = null;
            _agent.IdleScreenVisible = false;
        };
        _agent.IdleScreenVisible = true;
        _ = _agent.SendLogAsync("IDLE_SCREEN_SHOWN", "Idle threshold reached");
        _idleOverlay.Show();
    }

    private void CloseIdleOverlay()
    {
        if (_idleOverlay is null) return;
        var window = _idleOverlay;
        _idleOverlay = null;
        _agent.IdleScreenVisible = false;
        _ = _agent.SendLogAsync("IDLE_SCREEN_CLOSED", "Activity or policy change");
        try { window.Close(); } catch { }
    }

    private void UpdateUi()
    {
        DeploymentStatusText.Text = ClientDeploymentStatus.ReadSummary();
        var settings = _agent.Settings;
        ConnectionText.Text = _agent.Connected ? "CONNECTED" : "OFFLINE / RECONNECTING";
        ConnectionText.Foreground = FindBrush(_agent.Connected ? "BrushSuccess" : "BrushWarning");
        ConnectionDot.Fill = FindBrush(_agent.Connected ? "BrushSuccess" : "BrushWarning");

        var effective = settings.MonitoringEnabled && !_agent.RemotePaused;
        CancelShutdownButton.IsEnabled = true;
        MonitoringText.Text = _agent.RemotePaused ? "PAUSED BY ADMIN" : effective ? "ACTIVE" : "OFF";
        MonitoringText.Foreground = FindBrush(effective ? "BrushSuccess" : "BrushWarning");
        IdleDisplayText.Text = settings.IdleScreenEnabled ? "ON" : "OFF";
        IdleDisplayText.Foreground = FindBrush(settings.IdleScreenEnabled ? "BrushCyan" : "BrushMuted");
        IdleDelayText.Text = $"{settings.IdleScreenMinutes:0.###} min";
        ShutdownPolicyText.Text = $"{settings.ShutdownHours:0.###} hr / {settings.ShutdownWarningSeconds} sec";
        StatusDetailText.Text = _agent.ShutdownScheduled
            ? $"Shutdown pending ({_agent.ShutdownReason})."
            : _agent.Connected ? "Live control link established with Admin." : "Using cached policy until Admin reconnects.";
    }

    private Brush FindBrush(string key) => (Brush)FindResource(key);

    private void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_startupLoaded) return;
        try { StartupManager.SetEnabled(StartupCheck.IsChecked == true); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Startup setting", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void CancelShutdownButton_Click(object sender, RoutedEventArgs e)
    { _agent.CancelShutdown(); }
    private void ExitButton_Click(object sender, RoutedEventArgs e)
    { Close(); }

    private static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        var hours = (int)value.TotalHours;
        return $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }
}
