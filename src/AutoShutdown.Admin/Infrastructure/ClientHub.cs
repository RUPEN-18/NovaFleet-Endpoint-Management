using AutoShutdown.Shared;
using Microsoft.AspNetCore.SignalR;

namespace AutoShutdown.Admin.Infrastructure;

public sealed class ClientHub : Hub
{
    private readonly AdminSecurityStore _security;
    private readonly ActionDispatcher _actions;
    private readonly ClientRegistry _registry;
    private readonly StateStore _store;
    private readonly DeploymentTestDispatcher _deploymentTests;

    public ClientHub(ClientRegistry registry, StateStore store, DeploymentTestDispatcher deploymentTests, AdminSecurityStore security, ActionDispatcher actions)
    {
        _security=security; _actions=actions;
        _registry = registry;
        _store = store;
        _deploymentTests = deploymentTests;
    }

    public override async Task OnConnectedAsync()
    {
        var computer = Context.GetHttpContext()?.Request.Query["client"].ToString();
        if (!string.IsNullOrWhiteSpace(computer))
        {
            _registry.Register(computer, Context.ConnectionId, DeviceId);
            await _store.AppendLogAsync(new LogEntry
            {
                Source = computer,
                Event = "CLIENT_CONNECTED",
                Target = "SYSTEM A",
                Details = "SignalR connection established"
            });
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.UnregisterConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task Heartbeat(ClientHeartbeat heartbeat)
    {
        var computer = _registry.GetComputerName(Context.ConnectionId);
        if (!string.Equals(computer, heartbeat.ComputerName, StringComparison.OrdinalIgnoreCase)) throw new HubException("Client identity mismatch.");
        if(heartbeat.DeviceId.Length>0 && heartbeat.DeviceId!=DeviceId)throw new HubException("Device identity mismatch.");
        heartbeat=heartbeat with{DeviceId=DeviceId};
        _registry.UpdateHeartbeat(heartbeat, Context.ConnectionId);
        await _store.UpsertKnownClientAsync(heartbeat);
    }

    private string DeviceId => Context.GetHttpContext()?.Items["DeviceId"] as string ?? "";
    public Task<GlobalSettings> GetSettings() => _store.GetSettingsAsync();

    public Task DeploymentProbeAck(DeploymentProbeAck ack)
    {
        // Use the connection's registered identity, not an identity in the acknowledgement.
        var computer = _registry.GetComputerName(Context.ConnectionId);
        return computer is null ? Task.CompletedTask : _deploymentTests.AcknowledgeAsync(ack, computer);
    }


    public async Task ClientLog(string eventName, string details)
    {
        var computer = Context.GetHttpContext()?.Request.Query["client"].ToString();
        if (string.IsNullOrWhiteSpace(computer)) computer = "UNKNOWN";
        await _store.AppendLogAsync(new LogEntry
        {
            Source = computer,
            Event = eventName,
            Target = computer,
            Details = details
        });
    }

    public async Task Ack(CommandAck ack)
    {
        var computer = _registry.GetComputerName(Context.ConnectionId);
        if (!string.Equals(computer, ack.ComputerName, StringComparison.OrdinalIgnoreCase)) throw new HubException("Client identity mismatch.");
        await _actions.AcknowledgeAsync(ack, DeviceId);
        await _store.AppendLogAsync(new LogEntry
        {
            Source = ack.ComputerName,
            Event = ack.Success ? "COMMAND_ACK" : "COMMAND_FAILED",
            Target = ack.CommandId,
            Details = ack.Details
        });
    }
}
