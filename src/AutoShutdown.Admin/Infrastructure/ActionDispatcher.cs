using AutoShutdown.Shared;
using Microsoft.AspNetCore.SignalR;

namespace AutoShutdown.Admin.Infrastructure;

public sealed class ActionDispatcher
{
    private readonly AdminSecurityStore _security;
    private readonly InstallStore _devices;
    private sealed record PendingCommand(AdminIdentity Actor,string DeviceId,string Computer,string Kind,DateTimeOffset Expires);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,PendingCommand> _pending = new();
    private readonly ClientRegistry _registry;
    private readonly StateStore _store;
    private readonly IHubContext<ClientHub> _hub;

    public ActionDispatcher(ClientRegistry registry, StateStore store, IHubContext<ClientHub> hub, AdminSecurityStore security, InstallStore devices)
    {
        _security=security;_devices=devices;
        _registry = registry;
        _store = store;
        _hub = hub;
    }

    public async Task AcknowledgeAsync(CommandAck ack, string deviceId = "")
    {
        if(!_pending.TryGetValue(ack.CommandId,out var command) || !command.Computer.Equals(ack.ComputerName,StringComparison.OrdinalIgnoreCase) || command.DeviceId!=deviceId)
            throw new InvalidOperationException("Unknown command acknowledgement.");
        _pending.TryRemove(ack.CommandId,out _);
        await _security.AuditAsync(command.Actor,command.Kind,ack.Success?"Acknowledged":"Failed",ack.Details,command.DeviceId,command.Computer);
    }
    public async Task<DeliveryResult> DispatchAsync(AdminActionRequest request, string source = "ADMIN", AdminIdentity? actor = null)
    {
        if (request.Target is null || !Enum.IsDefined(request.Target.Kind) || !Enum.IsDefined(request.Kind))
            throw new ArgumentException("Select a valid client target and action.");
        if(actor is null || !actor.Can(request.Kind is ActionKind.Pause or ActionKind.Resume or ActionKind.Shutdown or ActionKind.CancelShutdown?AdminPermission.Automation:AdminPermission.Control))
            throw new UnauthorizedAccessException("This account cannot perform the selected action.");
        var groups = await _store.GetGroupsAsync();
        var recipients = _registry.ResolveOnline(request.Target, groups);
        var devices=await _devices.DevicesAsync();
        bool Assigned((string ComputerName,string ConnectionId) recipient) => actor.IsSuper || devices.Any(device=>device.Enabled && device.Id==_registry.GetDeviceId(recipient.ConnectionId) && actor.CanDevice(device.Id));
        if(request.Target.Kind==TargetKind.Client && recipients.Any(recipient=>!Assigned(recipient)))
            throw new UnauthorizedAccessException("That client PC is not assigned to your account.");
        recipients=recipients.Where(Assigned).ToArray();
        if(recipients.Count==0)
            throw new NoConnectedClientException("No assigned, connected Client app matches this target. Open Client on the PC and wait for CONNECTED. The installation service connection is separate.");

        foreach(var expired in _pending.Where(x=>x.Value.Expires<=DateTimeOffset.UtcNow).Select(x=>x.Key))_pending.TryRemove(expired,out _);
        foreach (var recipient in recipients)
        {
            var envelope = new CommandEnvelope
            {
                AdminId=actor.Id, AdminUsername=actor.Username, AdminDisplayName=actor.DisplayName,
                Kind = request.Kind,
                TargetComputer = recipient.ComputerName,
                Title = request.Title,
                Text = request.Text,
                MediaId = request.MediaId,
                ExpiresAt = DateTimeOffset.Now.AddMinutes(request.Kind == ActionKind.Shutdown ? 2 : 10)
            };

            var device=devices.FirstOrDefault(x=>x.Id==_registry.GetDeviceId(recipient.ConnectionId));
            _pending[envelope.Id]=new(actor,device?.Id??"",recipient.ComputerName,request.Kind.ToString(),envelope.ExpiresAt);
            await _security.AuditAsync(actor,request.Kind.ToString(),"Sent",envelope.Id,device?.Id??"",recipient.ComputerName);
            await _hub.Clients.Client(recipient.ConnectionId).SendAsync("Command", envelope);
        }

        var names = recipients.Select(x => x.ComputerName).ToArray();
        await _store.AppendLogAsync(new LogEntry
        {
            Source = source,
            Event = request.Kind.ToString().ToUpperInvariant(),
            Target = request.Target.ToString(),
            Details = $"Delivered to {names.Length} online client(s): {string.Join(", ", names)}"
        });

        return new DeliveryResult
        {
            RecipientCount = names.Length,
            Recipients = names,
            Summary = $"Delivered to {names.Length} online client(s)."
        };
    }
}
