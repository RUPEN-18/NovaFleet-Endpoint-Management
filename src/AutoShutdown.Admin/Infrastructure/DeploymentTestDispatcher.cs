using AutoShutdown.Shared;
using Microsoft.AspNetCore.SignalR;

namespace AutoShutdown.Admin.Infrastructure;

public sealed class DeploymentTestDispatcher
{
    private readonly DeploymentTestStore _tests;
    private readonly ClientRegistry _registry;
    private readonly StateStore _state;
    private readonly IHubContext<ClientHub> _hub;

    public DeploymentTestDispatcher(DeploymentTestStore tests, ClientRegistry registry, StateStore state, IHubContext<ClientHub> hub)
    {
        _tests = tests;
        _registry = registry;
        _state = state;
        _hub = hub;
    }

    public async Task<DeploymentTestJob> SendAsync(DeploymentTestRequest request)
    {
        if (!Guid.TryParseExact(request.Id, "N", out _) || string.IsNullOrWhiteSpace(request.ComputerName) || request.ComputerName.Length > 255)
            throw new ArgumentException("Select a client and provide a valid test job ID.");

        var computer = request.ComputerName.Trim();
        var target = _registry.ResolveOnline(TargetSpec.Client(computer), await _state.GetGroupsAsync()).FirstOrDefault();
        var online = !string.IsNullOrEmpty(target.ConnectionId);
        var now = DateTimeOffset.UtcNow;
        var result = await _tests.CreateAsync(new DeploymentTestJob
        {
            Id = request.Id,
            ComputerName = computer,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(30),
            Status = online ? DeploymentTestStatus.Pending : DeploymentTestStatus.Offline,
            Details = online ? "Waiting for client acknowledgement. Connection test only." : "Client is offline. This test was not queued for later delivery."
        });
        if (!result.Created) return result.Job;

        await _state.AppendLogAsync(new LogEntry { Event = "DEPLOYMENT_TEST_CREATED", Target = computer, Details = $"Test job {request.Id}: {result.Job.Status}. No installer execution." });
        if (!online) return result.Job;

        try
        {
            await _hub.Clients.Client(target.ConnectionId).SendAsync("DeploymentProbe", new DeploymentProbe
            {
                JobId = result.Job.Id,
                TargetComputer = computer,
                ExpiresAt = result.Job.ExpiresAt
            });
        }
        catch
        {
            await _tests.MarkSendFailedAsync(result.Job.Id);
            await _state.AppendLogAsync(new LogEntry { Event = "DEPLOYMENT_TEST_SEND_FAILED", Target = computer, Details = $"Test job {request.Id} could not be sent." });
        }
        return (await _tests.GetAsync()).First(x => x.Id == result.Job.Id);
    }

    public async Task AcknowledgeAsync(DeploymentProbeAck ack, string computer)
    {
        if (!Guid.TryParseExact(ack.JobId, "N", out _)) return;
        var job = await _tests.AcknowledgeAsync(ack.JobId, computer);
        if (job is null) return;
        await _state.AppendLogAsync(new LogEntry
        {
            Source = computer,
            Event = "DEPLOYMENT_TEST_RECEIVED",
            Target = job.Id,
            Details = job.Details
        });
    }
}
