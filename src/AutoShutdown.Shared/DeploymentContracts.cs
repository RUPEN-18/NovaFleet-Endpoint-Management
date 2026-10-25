using System.Text.Json.Serialization;

namespace AutoShutdown.Shared;

// Step 1 verifies transport only. These contracts contain no installer or command.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeploymentTestStatus { Pending, Received, Offline, TimedOut, SendFailed }

public sealed record DeploymentTestRequest
{
    public required string Id { get; init; }
    public required string ComputerName { get; init; }
}

public sealed record DeploymentTestJob
{
    public required string Id { get; init; }
    public required string ComputerName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? AcknowledgedAt { get; init; }
    public DeploymentTestStatus Status { get; init; }
    public string Details { get; init; } = "";
}

public sealed record DeploymentProbe
{
    public required string JobId { get; init; }
    public required string TargetComputer { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed record DeploymentProbeAck
{
    public required string JobId { get; init; }
}
