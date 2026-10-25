using AutoShutdown.Shared;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoShutdown.Admin.Infrastructure;

public sealed record InstallBatchRecord(string AdminId, string Signature, InstallBatchResult Result);

public sealed partial class InstallStore
{
    public const int MaximumBatchSize = 100;

    private static void ValidateOperation(string id, List<string>? targets)
    {
        if (!Guid.TryParseExact(id, "N", out _) || targets is null || targets.Count == 0 || targets.Count > MaximumBatchSize
            || targets.Any(target => !Guid.TryParseExact(target, "N", out _)))
            throw new ArgumentException("Choose between 1 and 100 PCs with valid identifiers.");
    }

    private static string BatchSignature(string kind, string packageId, IEnumerable<string> targets) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { kind, packageId, targets = targets.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray() }))));

    private InstallBatchResult CurrentBatch(InstallBatchRecord record, AdminIdentity actor) => record.Result with
    {
        Items = record.Result.Items.Where(item => actor.CanDevice(item.DeviceId)).Select(item => item.Job is null ? item
            : item with { Job = _data.Jobs.First(job => job.Id == item.Job.Id) }).ToList()
    };

    private InstallBatchRecord? ExistingBatch(string id, string signature, AdminIdentity actor)
    {
        var existing = _data.Batches.FirstOrDefault(batch => batch.Result.Id == id);
        if (existing is null) return null;
        if (existing.AdminId != actor.Id || existing.Signature != signature)
            throw new InvalidOperationException("This request identifier was already used for a different operation.");
        return existing;
    }

    // Caller holds _gate. Eligibility is checked again on the server for every selected PC.
    private InstallJob NewBatchJob(string batchId, EnrolledDevice device, ApprovedPackage package, AdminIdentity actor, string retryOf = "")
    {
        if (!device.Enabled || device.Removed) throw new InvalidOperationException("PC is disabled or removed.");
        if (!device.LastSeenUtc.HasValue || DateTimeOffset.UtcNow - device.LastSeenUtc.Value >= TimeSpan.FromSeconds(20))
            throw new InvalidOperationException("Installer service is offline. Reconnect this PC and submit it again.");
        if (!Version.TryParse(device.ServiceVersion, out var serviceVersion) || serviceVersion < new Version(0, 4, 0))
            throw new InvalidOperationException("Installer service 0.4.0 or newer is required.");
        if (ApplicationPackagePolicy.NeedsUpdatedService(package) && serviceVersion < new Version(0, 5, 0))
            throw new InvalidOperationException("Installer service 0.5.0 or newer is required for this package.");
        if (_data.Jobs.Any(job => job.DeviceId == device.Id && !SevenZipPolicy.IsTerminal(job.State)))
            throw new InvalidOperationException("An installation is already in progress on this PC.");
        var path = PackagePath(package);
        if (!File.Exists(path) || new FileInfo(path).Length != package.Length)
            throw new InvalidOperationException("Saved installer is unavailable. Approve the installer again.");
        return new InstallJob
        {
            Id = Guid.NewGuid().ToString("N"), DeviceId = device.Id, ComputerName = device.ComputerName,
            Package = package, AdminId = actor.Id, AdminUsername = actor.Username, AdminDisplayName = actor.DisplayName,
            BatchId = batchId, RetryOfJobId = retryOf
        };
    }

    public async Task<(InstallBatchResult Result, bool Created)> CreateBatchAsync(InstallBatchRequest request, AdminIdentity actor)
    {
        ValidateOperation(request.Id, request.DeviceIds);
        if (!actor.Can(AdminPermission.Install) || request.DeviceIds.Any(id => !actor.CanDevice(id)))
            throw new UnauthorizedAccessException("Installation permission is required for every selected PC.");
        if (string.IsNullOrWhiteSpace(request.PackageId)) throw new ArgumentException("Choose an approved package.");
        var signature = BatchSignature("install", request.PackageId, request.DeviceIds);
        await _gate.WaitAsync();
        try
        {
            var existing = ExistingBatch(request.Id, signature, actor);
            if (existing is not null) return (CurrentBatch(existing, actor), false);
            var package = _data.Packages.Concat(_data.Package is null ? Array.Empty<ApprovedPackage>() : new[] { _data.Package })
                .FirstOrDefault(package => package.Id == request.PackageId);
            if (package is null || !ApplicationPackagePolicy.IsGeneral(package) || !ApplicationPackagePolicy.IsApproved(package))
                throw new ArgumentException("Choose an ordinary approved EXE/MSI package.");
            var items = new List<InstallBatchItem>();
            foreach (var id in request.DeviceIds.Distinct())
            {
                var device = _data.Devices.FirstOrDefault(device => device.Id == id && !device.Removed);
                if (device is null) { items.Add(new(id, "Unknown PC", "PC is not registered.")); continue; }
                try { items.Add(new(id, device.ComputerName, "Queued for the installer service.", NewBatchJob(request.Id, device, package, actor))); }
                catch (InvalidOperationException ex) { items.Add(new(id, device.ComputerName, ex.Message)); }
            }
            var result = new InstallBatchResult(request.Id, package.Id, items, DateTimeOffset.UtcNow);
            await CommitBatchAsync(new(actor.Id, signature, result));
            return (result, true);
        }
        finally { _gate.Release(); }
    }

    public async Task<(InstallBatchResult Result, bool Created)> RetryFailedAsync(InstallRetryRequest request, AdminIdentity actor)
    {
        ValidateOperation(request.Id, request.JobIds);
        if (!actor.Can(AdminPermission.Install)) throw new UnauthorizedAccessException("Installation permission is required.");
        var signature = BatchSignature("retry", "", request.JobIds);
        await _gate.WaitAsync();
        try
        {
            // Check scope before returning either old results or new attempts.
            var originals = request.JobIds.Distinct().Select(id => _data.Jobs.FirstOrDefault(job => job.Id == id)
                ?? throw new ArgumentException("Installation attempt was not found.")).ToList();
            if (originals.Any(job => !actor.CanDevice(job.DeviceId)))
                throw new UnauthorizedAccessException("Installation permission is required for every selected PC.");
            var existing = ExistingBatch(request.Id, signature, actor);
            if (existing is not null) return (CurrentBatch(existing, actor), false);
            if (originals.Select(job => job.DeviceId).Distinct().Count() != originals.Count)
                throw new ArgumentException("Choose only one failed attempt per PC.");
            if (originals.Select(job => job.Package.Id).Distinct().Count() != 1)
                throw new ArgumentException("Retry one software package at a time.");
            var items = new List<InstallBatchItem>();
            foreach (var original in originals)
            {
                var device = _data.Devices.FirstOrDefault(device => device.Id == original.DeviceId && !device.Removed);
                try
                {
                    if (original.State != InstallState.Failed)
                        throw new InvalidOperationException("Only confirmed Failed attempts can be retried. Successful, active and NeedsReview attempts are excluded.");
                    if (_data.Jobs.Any(job => job.RetryOfJobId == original.Id)
                        || _data.Jobs.Last(job => job.DeviceId == original.DeviceId).Id != original.Id)
                        throw new InvalidOperationException("A newer attempt exists. Use its latest result.");
                    if (device is null) throw new InvalidOperationException("PC is no longer registered.");
                    if (!ApplicationPackagePolicy.IsGeneral(original.Package) || !ApplicationPackagePolicy.IsApproved(original.Package))
                        throw new InvalidOperationException("Approve this legacy package again before installing.");
                    items.Add(new(original.DeviceId, original.ComputerName, "Retry queued; original history retained.",
                        NewBatchJob(request.Id, device, original.Package, actor, original.Id)));
                }
                catch (InvalidOperationException ex) { items.Add(new(original.DeviceId, original.ComputerName, ex.Message)); }
            }
            var result = new InstallBatchResult(request.Id, originals[0].Package.Id, items, DateTimeOffset.UtcNow);
            await CommitBatchAsync(new(actor.Id, signature, result));
            return (result, true);
        }
        finally { _gate.Release(); }
    }

    private Task CommitBatchAsync(InstallBatchRecord batch) => CommitAsync(_data with
    {
        Jobs = _data.Jobs.Concat(batch.Result.Items.Where(item => item.Job is not null).Select(item => item.Job!)).ToList(),
        Batches = _data.Batches.Append(batch).ToList()
    });

    public async Task<InstallBatchResult> GetBatchAsync(string id, AdminIdentity actor)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid batch identifier.");
        await _gate.WaitAsync();
        try
        {
            var batch = _data.Batches.FirstOrDefault(batch => batch.Result.Id == id);
            if (batch is null || !batch.Result.Items.Any(item => actor.CanDevice(item.DeviceId)))
                throw new ArgumentException("Batch was not found in your assigned PCs.");
            return CurrentBatch(batch, actor);
        }
        finally { _gate.Release(); }
    }
}
