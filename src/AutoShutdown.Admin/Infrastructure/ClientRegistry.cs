using AutoShutdown.Shared;
using System.Collections.Concurrent;

namespace AutoShutdown.Admin.Infrastructure;

public sealed class ClientRegistry
{
    public string? GetComputerName(string connectionId) =>
        _clients.Values.FirstOrDefault(x => x.ConnectionId == connectionId)?.ComputerName;

    public string GetDeviceId(string connectionId) =>
        _clients.Values.FirstOrDefault(client => client.ConnectionId == connectionId)?.DeviceId ?? "";

    private sealed class RuntimeClient
    {
        public required string ComputerName { get; init; }
        public string DeviceId { get; set; } = "";
        public string? ConnectionId { get; set; }
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.Now;
        public ClientHeartbeat Heartbeat { get; set; } = new() { ComputerName = string.Empty };
    }

    private readonly ConcurrentDictionary<string, RuntimeClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string computerName, string connectionId, string deviceId = "")
    {
        var client = _clients.GetOrAdd(computerName, name => new RuntimeClient
        {
            ComputerName = name,
            Heartbeat = new ClientHeartbeat { ComputerName = name }
        });

        client.ConnectionId = connectionId;
        client.DeviceId = deviceId;
        client.LastSeen = DateTimeOffset.Now;
    }

    public void UnregisterConnection(string connectionId)
    {
        foreach (var pair in _clients)
        {
            if (string.Equals(pair.Value.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                pair.Value.ConnectionId = null;
                break;
            }
        }
    }

    public void UpdateHeartbeat(ClientHeartbeat heartbeat, string connectionId)
    {
        var client = _clients.GetOrAdd(heartbeat.ComputerName, name => new RuntimeClient
        {
            ComputerName = name,
            Heartbeat = heartbeat
        });

        client.ConnectionId = connectionId;
        client.LastSeen = DateTimeOffset.Now;
        client.DeviceId = heartbeat.DeviceId;
        client.Heartbeat = heartbeat with { At = DateTimeOffset.Now };
    }

    public IReadOnlyList<ClientSnapshot> GetSnapshots(IReadOnlyList<GroupDefinition> groups, IReadOnlyList<ClientHeartbeat>? known = null)
    {
        var now = DateTimeOffset.Now;
        var names = new HashSet<string>(_clients.Keys, StringComparer.OrdinalIgnoreCase);
        if (known is not null)
        {
            foreach (var item in known) names.Add(item.ComputerName);
        }

        var result = new List<ClientSnapshot>();
        foreach (var name in names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            _clients.TryGetValue(name, out var runtime);
            var persisted = known?.FirstOrDefault(x => string.Equals(x.ComputerName, name, StringComparison.OrdinalIgnoreCase));
            var hb = runtime?.Heartbeat ?? persisted ?? new ClientHeartbeat { ComputerName = name };
            var lastSeen = runtime?.LastSeen ?? persisted?.At ?? DateTimeOffset.MinValue;
            var online = runtime?.ConnectionId is not null && (now - lastSeen).TotalSeconds <= 20;
            var memberships = groups
                .Where(g => g.Members.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Select(g => g.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            result.Add(new ClientSnapshot
            {
                DeviceId = runtime?.DeviceId ?? persisted?.DeviceId ?? "",
                ComputerName = name,
                Online = online,
                LastSeen = lastSeen,
                IdleSeconds = hb.IdleSeconds,
                MonitoringEnabled = hb.MonitoringEnabled,
                RemotePaused = hb.RemotePaused,
                IdleScreenEnabled = hb.IdleScreenEnabled,
                IdleScreenVisible = hb.IdleScreenVisible,
                ShutdownScheduled = hb.ShutdownScheduled,
                ShutdownReason = hb.ShutdownReason,
                ClientVersion = hb.ClientVersion,
                Groups = memberships
            });
        }

        return result;
    }

    public IReadOnlyList<(string ComputerName, string ConnectionId)> ResolveOnline(TargetSpec target, IReadOnlyList<GroupDefinition> groups)
    {
        var online = _clients.Values
            .Where(x => x.ConnectionId is not null && (DateTimeOffset.Now - x.LastSeen).TotalSeconds <= 20)
            .ToArray();

        IEnumerable<RuntimeClient> selected = target.Kind switch
        {
            TargetKind.All => online,
            TargetKind.Client => online.Where(x => string.Equals(x.ComputerName, target.Value, StringComparison.OrdinalIgnoreCase)),
            TargetKind.Group => online.Where(x => groups
                .FirstOrDefault(g => string.Equals(g.Name, target.Value, StringComparison.OrdinalIgnoreCase))?
                .Members.Contains(x.ComputerName, StringComparer.OrdinalIgnoreCase) == true),
            _ => Array.Empty<RuntimeClient>()
        };

        return selected
            .Where(x => x.ConnectionId is not null)
            .Select(x => (x.ComputerName, x.ConnectionId!))
            .ToArray();
    }

    public int ConnectedCount => _clients.Values.Count(x => x.ConnectionId is not null && (DateTimeOffset.Now - x.LastSeen).TotalSeconds <= 20);
}
