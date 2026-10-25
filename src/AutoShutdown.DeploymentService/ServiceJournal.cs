using System.Text.Json;

namespace AutoShutdown.DeploymentService;

public sealed record ServiceHealth
{
    public string ServiceName { get; init; } = ServiceConstants.Name;
    public string ComputerName { get; init; } = Environment.MachineName;
    public string Version { get; init; } = ServiceConstants.Version;
    public string Mode { get; init; } = "WindowsService";
    public required string State { get; init; }
    public required string Identity { get; init; }
    public required string IdentitySid { get; init; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset HeartbeatUtc { get; init; }
    public bool DeploymentEnabled { get; init; } = false;
    public string Details { get; init; } = "Client background installer: approved EXE/MSI packages and the verified 7-Zip test.";
}

public sealed class ServiceJournal
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root = ServiceConstants.DataPath;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public ServiceJournal()
    {
        // The administrator installer provisions this folder and its permissions.
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("Install the deployment service before starting it.");
        RejectLink(_root);
    }

    public async Task WriteHealthAsync(ServiceHealth health, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var destination = Path.Combine(_root, "status.json");
            var temporary = Path.Combine(_root, "status.json.tmp");
            RejectLink(destination);
            RejectLink(temporary);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(health, _json), token);
            // Windows readers can briefly hold status.json without allowing deletion.
            // Retry the atomic replacement instead of failing the service for a short read.
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, destination, overwrite: true); break; }
                catch (IOException) when (attempt < 9) { await Task.Delay(100, token); }
            }
            var publicPath = Path.Combine(AutoShutdown.Shared.DeploymentActivity.DirectoryPath, "service-status.json");
            RejectLink(publicPath);
            RejectLink(publicPath + ".tmp");
            await File.WriteAllTextAsync(publicPath + ".tmp", JsonSerializer.Serialize(new
            { state = health.State, heartbeatUtc = health.HeartbeatUtc, deploymentEnabled = health.DeploymentEnabled }, _json), token);
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(publicPath + ".tmp", publicPath, overwrite: true); break; }
                catch (IOException) when (attempt < 9) { await Task.Delay(100, token); }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task AppendAsync(string eventName, string details, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var path = Path.Combine(_root, "service.jsonl");
            var previous = Path.Combine(_root, "service.previous.jsonl");
            RejectLink(path);
            RejectLink(previous);
            if (File.Exists(path) && new FileInfo(path).Length >= 5 * 1024 * 1024)
                File.Move(path, previous, overwrite: true);
            var line = JsonSerializer.Serialize(new
            {
                atUtc = DateTimeOffset.UtcNow, computerName = Environment.MachineName,
                eventName, details, processId = Environment.ProcessId
            }, _json);
            await File.AppendAllTextAsync(path, line + Environment.NewLine, token);
        }
        finally { _gate.Release(); }
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Service data path must not be a link: {path}");
    }
}
