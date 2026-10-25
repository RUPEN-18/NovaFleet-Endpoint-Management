using AutoShutdown.Shared;
using System.IO;
using System.Text.Json;

namespace AutoShutdown.Admin.Infrastructure;

// A separate file keeps test history independent of shutdown settings and logs.
public sealed class DeploymentTestStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public DeploymentTestStore(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        _path = Path.Combine(rootPath, "deployment-tests.json");
    }

    public async Task<(DeploymentTestJob Job, bool Created)> CreateAsync(DeploymentTestJob job)
    {
        await _gate.WaitAsync();
        try
        {
            var jobs = await ReadAsync();
            var existing = jobs.FirstOrDefault(x => x.Id == job.Id);
            if (existing is not null)
            {
                if (!string.Equals(existing.ComputerName, job.ComputerName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("This job ID already belongs to a different client.");
                return (existing, false);
            }
            jobs.Add(job);
            await WriteAsync(jobs);
            return (job, true);
        }
        finally { _gate.Release(); }
    }

    public async Task<List<DeploymentTestJob>> GetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var jobs = await ReadAsync();
            var changed = false;
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < jobs.Count; i++)
            {
                if (jobs[i].Status != DeploymentTestStatus.Pending || now < jobs[i].ExpiresAt) continue;
                jobs[i] = jobs[i] with { Status = DeploymentTestStatus.TimedOut, Details = "No acknowledgement within 30 seconds. No software was installed." };
                changed = true;
            }
            if (changed) await WriteAsync(jobs);
            return jobs.OrderByDescending(x => x.CreatedAt).ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task<DeploymentTestJob?> AcknowledgeAsync(string id, string computer)
    {
        await _gate.WaitAsync();
        try
        {
            var jobs = await ReadAsync();
            var index = jobs.FindIndex(x => x.Id == id && string.Equals(x.ComputerName, computer, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || jobs[index].Status != DeploymentTestStatus.Pending) return null;
            var now = DateTimeOffset.UtcNow;
            if (now >= jobs[index].ExpiresAt)
            {
                jobs[index] = jobs[index] with { Status = DeploymentTestStatus.TimedOut, Details = "Acknowledgement arrived after the deadline. No software was installed." };
                await WriteAsync(jobs);
                return null;
            }
            jobs[index] = jobs[index] with
            {
                Status = DeploymentTestStatus.Received,
                AcknowledgedAt = now,
                Details = "Client acknowledged the connection test. No software was installed."
            };
            await WriteAsync(jobs);
            return jobs[index];
        }
        finally { _gate.Release(); }
    }

    public async Task MarkSendFailedAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var jobs = await ReadAsync();
            var index = jobs.FindIndex(x => x.Id == id && x.Status == DeploymentTestStatus.Pending);
            if (index < 0) return; // Never overwrite an acknowledgement that won the race.
            jobs[index] = jobs[index] with { Status = DeploymentTestStatus.SendFailed, Details = "The test could not be sent. No software was installed." };
            await WriteAsync(jobs);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<DeploymentTestJob>> ReadAsync()
    {
        if (!File.Exists(_path)) return new();
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<DeploymentTestJob>>(stream, _json)
            ?? throw new InvalidDataException("Deployment test history is invalid.");
    }

    private async Task WriteAsync(List<DeploymentTestJob> jobs)
    {
        var temporary = _path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(jobs, _json));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
