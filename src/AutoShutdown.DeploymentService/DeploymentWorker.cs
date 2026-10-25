using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Principal;

namespace AutoShutdown.DeploymentService;

public sealed class DeploymentWorker : BackgroundService
{
    private readonly ServiceJournal _journal;
    private readonly ILogger<DeploymentWorker> _logger;
    private ServiceHealth? _health;
    private readonly DeviceSession _session;

    public DeploymentWorker(ServiceJournal journal, ILogger<DeploymentWorker> logger, DeviceSession session)
    {
        _journal = journal;
        _logger = logger;
        _session = session;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var now = DateTimeOffset.UtcNow;
            _health = new ServiceHealth
            {
                State = "Running", Identity = identity.Name,
                IdentitySid = identity.User?.Value ?? "", StartedAtUtc = now, HeartbeatUtc = now
            };
            await _journal.WriteHealthAsync(_health, stoppingToken);
            await _journal.AppendAsync("SERVICE_STARTED", "LocalSystem service started; waiting for paired Admin connection.", stoppingToken);
            _logger.LogInformation("Nova deployment service started on {ComputerName}.", Environment.MachineName);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _health = _health with { HeartbeatUtc = DateTimeOffset.UtcNow, DeploymentEnabled = _session.Connected };
                await _journal.WriteHealthAsync(_health, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Deployment service failed. Windows service recovery will restart it.");
            // StopHost alone can exit with 0, preventing SCM recovery from treating it as a failure.
            Environment.Exit(1);
        }
        finally
        {
            if (_health is not null)
            {
                try
                {
                    await _journal.WriteHealthAsync(_health with { State = "Stopped", HeartbeatUtc = DateTimeOffset.UtcNow });
                    await _journal.AppendAsync("SERVICE_STOPPED", "Windows service stopped.");
                }
                catch (Exception ex) { _logger.LogError(ex, "Could not record the final service state."); }
            }
        }
    }
}
