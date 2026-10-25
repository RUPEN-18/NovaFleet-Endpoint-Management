using AutoShutdown.Client.Services;
using AutoShutdown.Shared;
using System.Diagnostics;
using System.Windows.Threading;

namespace AutoShutdown.Admin;

public partial class MainWindow
{
    private readonly DispatcherTimer _adminIdleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _adminPolicyLoaded;
    private bool _adminShutdownPending;

    private void InitializeAdminInactivity()
    {
        _adminIdleTimer.Tick += (_, _) => UpdateAdminInactivity();
        Loaded += (_, _) => _adminIdleTimer.Start();
        Closed += (_, _) =>
        {
            PrepareForExit();
            if (AutoShutdown.Shared.AppExitState.CancelIdleOnWindowClose(_adminShutdownPending,
                AutoShutdown.Shared.AppExitState.IsSessionEnding)) RunAdminShutdown("/a");
        };
    }
    public void PrepareForExit()
    {
        if(!_loggingOut)AutoShutdown.Shared.AppExitState.BeginClosing();
        _adminIdleTimer.Stop();
        _refreshTimer.Stop();
        SleepGuard.Release();
    }
    private void UpdateAdminInactivity()
    {
        if (AutoShutdown.Shared.AppExitState.IsClosing) return;
        var idle = IdleMonitor.GetIdleTime();
        // Admin must remain available to deliver packages and receive installation results.
        var installing = _installBusy || _installOverview is null || DeploymentActivity.IsInstalling()
            || _installOverview.Jobs.Any(job => !SevenZipPolicy.IsTerminal(job.State));
        var active = _adminPolicyLoaded && _settings.MonitoringEnabled;
        SleepGuard.SetActive(active || installing);
        if (_adminShutdownPending && (!active || installing || idle < TimeSpan.FromSeconds(5)))
        {
            if (RunAdminShutdown("/a")) _adminShutdownPending = false;
        }
        AdminIdleStatusText.Text = !_adminPolicyLoaded ? "ADMIN MONITORING: loading"
            : installing ? "ADMIN MONITORING: installation hold"
            : !active ? "ADMIN MONITORING: OFF"
            : $"ADMIN IDLE: {idle:hh\\:mm\\:ss}" + (_adminShutdownPending ? " • shutdown pending" : "");
        if (!_adminShutdownPending && _adminPolicyLoaded && InactivityPolicy.ShouldShutdown(_settings, idle, false, installing))
        {
            var seconds = Math.Clamp(_settings.ShutdownWarningSeconds, 1, 3600);
            _adminShutdownPending = RunAdminShutdown($"/s /t {seconds} /c \"L&T Admin: Admin PC is inactive. Move the mouse or use the keyboard to cancel.\"");
        }
    }
    private static bool RunAdminShutdown(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = System.IO.Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                Arguments = arguments, UseShellExecute = false, CreateNoWindow = true
            });
            return process is not null && process.WaitForExit(3000) && process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AutoShutdown.Shared.AppDiagnostics.Record("SystemA", "SHUTDOWN_COMMAND", ex);
            Debug.WriteLine("Admin shutdown command failed: " + ex.Message); return false;
        }
    }
}
