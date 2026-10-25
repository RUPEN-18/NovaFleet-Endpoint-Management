using System.Text.Json.Serialization;

namespace AutoShutdown.Shared;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TargetKind
{
    All,
    Client,
    Group
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionKind
{
    TextMessage,
    ImageMessage,
    Pause,
    Resume,
    Shutdown,
    CancelShutdown
}

public sealed record TargetSpec(TargetKind Kind, string Value)
{
    public static TargetSpec All() => new(TargetKind.All, "ALL");
    public static TargetSpec Client(string computerName) => new(TargetKind.Client, computerName);
    public static TargetSpec Group(string groupName) => new(TargetKind.Group, groupName);
    public override string ToString() => Kind switch
    {
        TargetKind.All => "ALL CLIENTS",
        TargetKind.Group => $"GROUP: {Value}",
        _ => Value
    };
}

public sealed record AdminActionRequest
{
    public required TargetSpec Target { get; init; }
    public required ActionKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string? MediaId { get; init; }
}

public sealed record CommandEnvelope
{
    public string AdminId { get; init; } = "";
    public string AdminUsername { get; init; } = "";
    public string AdminDisplayName { get; init; } = "";
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.Now.AddMinutes(5);
    public required ActionKind Kind { get; init; }
    public required string TargetComputer { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string? MediaId { get; init; }
}

public sealed record CommandAck
{
    public required string CommandId { get; init; }
    public required string ComputerName { get; init; }
    public bool Success { get; init; }
    public string Details { get; init; } = string.Empty;
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

public sealed record DeliveryResult
{
    public int RecipientCount { get; init; }
    public IReadOnlyList<string> Recipients { get; init; } = Array.Empty<string>();
    public string Summary { get; init; } = string.Empty;
}

public sealed record GlobalSettings
{
    public double IdleScreenMinutes { get; init; } = 10;
    public double ShutdownHours { get; init; } = 3;
    public int ShutdownWarningSeconds { get; init; } = 60;
    public bool MonitoringEnabled { get; init; } = true;
    public bool IdleScreenEnabled { get; init; } = true;
    public string IdleMessage { get; init; } = "This computer is currently inactive.";
    public string? IdleImageMediaId { get; init; }
}

public sealed record ClientHeartbeat
{
    public string DeviceId { get; init; } = "";
    public required string ComputerName { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public double IdleSeconds { get; init; }
    public bool MonitoringEnabled { get; init; }
    public bool RemotePaused { get; init; }
    public bool IdleScreenEnabled { get; init; }
    public bool IdleScreenVisible { get; init; }
    public bool ShutdownScheduled { get; init; }
    public string ShutdownReason { get; init; } = string.Empty;
    public string ClientVersion { get; init; } = "2.0.0";
}

public sealed record ClientSnapshot
{
    public string DeviceId { get; init; } = "";
    public required string ComputerName { get; init; }
    public bool Online { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    public double IdleSeconds { get; init; }
    public bool MonitoringEnabled { get; init; }
    public bool RemotePaused { get; init; }
    public bool IdleScreenEnabled { get; init; }
    public bool IdleScreenVisible { get; init; }
    public bool ShutdownScheduled { get; init; }
    public string ShutdownReason { get; init; } = string.Empty;
    public string ClientVersion { get; init; } = string.Empty;
    public IReadOnlyList<string> Groups { get; init; } = Array.Empty<string>();
}

public sealed record GroupDefinition
{
    public string Name { get; init; } = string.Empty;
    public List<string> Members { get; init; } = new();
}

public sealed record ScheduleDefinition
{
    public string AdminId { get; init; } = "";
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public TargetSpec Target { get; init; } = TargetSpec.All();
    public ActionKind Action { get; init; } = ActionKind.TextMessage;
    public string Time { get; init; } = "20:00";
    public List<string> Days { get; init; } = new() { "Mon", "Tue", "Wed", "Thu", "Fri" };
    public string Message { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public string LastRunKey { get; set; } = string.Empty;
    public DateTimeOffset? LastRun { get; set; }
}

public sealed record LogEntry
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public string Source { get; init; } = "ADMIN";
    public string Event { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
}

public sealed record MediaAsset
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public long Length { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record ServerHealth
{
    public bool Online { get; init; }
    public string Version { get; init; } = "2.0.0";
    public DateTimeOffset StartedAt { get; init; }
    public int ConnectedClients { get; init; }
}
