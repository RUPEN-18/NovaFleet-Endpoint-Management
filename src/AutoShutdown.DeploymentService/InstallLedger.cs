using AutoShutdown.Shared;
using System.Text.Json;

namespace AutoShutdown.DeploymentService;

public sealed record LocalInstall
{
    public required InstallJob Job { get; init; }
    public string Stage { get; init; } = "Received";
    public int ProcessId { get; init; }
    public DateTimeOffset? ProcessStartedUtc { get; init; }
    public InstallReport? Result { get; init; }
    public bool Reported { get; init; }
}

public sealed class InstallLedger
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string Root { get; } = Path.Combine(ServiceConstants.DataPath, "jobs");
    public InstallLedger()
    {
        Directory.CreateDirectory(Root);
        if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) throw new IOException("The job cache must not be a link.");
    }
    public string Folder(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid job ID.");
        var folder = Path.Combine(Root, id);
        Directory.CreateDirectory(folder);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("The job directory must not be a link.");
        return folder;
    }
    public string Installer(string id) => Path.Combine(Folder(id), SevenZipPolicy.FileName);
    public string Installer(InstallJob job)
    {
        if (!ApplicationPackagePolicy.IsApproved(job.Package)) throw new InvalidDataException("Invalid installer profile.");
        return Path.Combine(Folder(job.Id), job.Package.FileName);
    }
    public async Task SaveAsync(LocalInstall record)
    {
        var path = Path.Combine(Folder(record.Job.Id), "job.json");
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(record, _json));
        File.Move(path + ".tmp", path, overwrite: true);
    }
    public List<LocalInstall> ReadAll() => Directory.GetDirectories(Root).Select(folder =>
    {
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("A job directory is a link.");
        var path = Path.Combine(folder, "job.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<LocalInstall>(File.ReadAllText(path), _json)
            ?? throw new InvalidDataException("A local installation record is invalid.") : null;
    }).Where(x => x is not null).Select(x => x!).ToList();

    public static async Task SetActivityAsync(bool busy, string jobId = "", string application = "", string stage = "", string details = "")
    {
        if (!Directory.Exists(DeploymentActivity.DirectoryPath)
            || (File.GetAttributes(DeploymentActivity.DirectoryPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Install the service update to provision its public activity directory.");
        var temporary = DeploymentActivity.FilePath + ".tmp";
        // Idle polls keep the most recent terminal result visible in the Client app.
        if (!busy && jobId.Length == 0 && File.Exists(DeploymentActivity.FilePath))
        {
            using var previous = JsonDocument.Parse(await File.ReadAllTextAsync(DeploymentActivity.FilePath));
            if (!previous.RootElement.GetProperty("busy").GetBoolean()) return;
        }
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { busy, jobId, application, stage, details, atUtc = DateTimeOffset.UtcNow }));
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temporary, DeploymentActivity.FilePath, overwrite: true); break; }
            catch (IOException) when (attempt < 9) { await Task.Delay(100); }
        }
    }
}
