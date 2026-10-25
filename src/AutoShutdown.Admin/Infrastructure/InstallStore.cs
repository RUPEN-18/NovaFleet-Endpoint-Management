using AutoShutdown.Shared;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoShutdown.Admin.Infrastructure;

public sealed record EnrolledDevice(string Id, string ComputerName, string TokenHash, DateTimeOffset? LastSeenUtc, string ControlTokenHash = "", string ServiceVersion = "0.2.0", string DisplayName = "", bool Enabled = true, bool Removed = false);
public sealed record InstallDatabase
{
    public List<InstallBatchRecord> Batches { get; init; } = new();
    public List<EnrolledDevice> Devices { get; init; } = new();
    public ApprovedPackage? Package { get; init; }
    public List<InstallJob> Jobs { get; init; } = new();
    public List<ApprovedPackage> Packages { get; init; } = new();
}

public sealed partial class InstallStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _databasePath;
    public string InstallerPath { get; }
    private InstallDatabase _data;
    private readonly string _root;

    public InstallStore(string root)
    {
        Directory.CreateDirectory(root);
        _root = root;
        _databasePath = Path.Combine(root, "install-jobs.json");
        InstallerPath = Path.Combine(root, SevenZipPolicy.FileName);
        if(File.Exists(_databasePath) && !File.Exists(_databasePath+".pre-step123.bak"))File.Copy(_databasePath,_databasePath+".pre-step123.bak");
        if (File.Exists(_databasePath) && !File.Exists(_databasePath + ".pre-step4.bak")) File.Copy(_databasePath, _databasePath + ".pre-step4.bak");
        _data = File.Exists(_databasePath) ? JsonSerializer.Deserialize<InstallDatabase>(File.ReadAllText(_databasePath), _json)
            ?? throw new InvalidDataException("Installation history is invalid.") : new();
    }

    private async Task CommitAsync(InstallDatabase next)
    {
        var temporary = _databasePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(next, _json));
        File.Move(temporary, _databasePath, overwrite: true);
        _data = next;
    }

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static bool SecretEquals(string supplied, string expected) => supplied.Length == expected.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));

    public async Task<DeviceConnection> PairAsync(string computer, string url, string certificateHash)
    {
        if (string.IsNullOrWhiteSpace(computer) || computer.Length > 255) throw new ArgumentException("Invalid computer name.");
        await _gate.WaitAsync();
        try
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var controlToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var old = _data.Devices.FirstOrDefault(x => string.Equals(x.ComputerName, computer, StringComparison.OrdinalIgnoreCase));
            if (old is not null && _data.Jobs.Any(x => x.DeviceId == old.Id && !SevenZipPolicy.IsTerminal(x.State)))
                throw new InvalidOperationException("Finish the active job before pairing this client again.");
            var device = new EnrolledDevice(old?.Id ?? Guid.NewGuid().ToString("N"), computer, HashToken(token), null, HashToken(controlToken), DisplayName: old?.DisplayName ?? computer);
            var devices = _data.Devices.Where(x => x.Id != device.Id).Append(device).ToList();
            await CommitAsync(_data with { Devices = devices });
            return new DeviceConnection { DeviceId = device.Id, ComputerName = computer, DeviceToken = token,
                ServerUrl = url, ServerCertificateSha256 = certificateHash, ControlApiKey = controlToken };
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> AuthenticateAsync(string id, string token)
    {
        if (!Guid.TryParseExact(id, "N", out _) || token.Length != 64) return null;
        await _gate.WaitAsync();
        try
        {
            var device = _data.Devices.FirstOrDefault(x => x.Id == id);
            return device is not null && device.Enabled && !device.Removed && SecretEquals(HashToken(token), device.TokenHash) ? device.Id : null;
        }
        finally { _gate.Release(); }
    }

    public async Task TouchAsync(string deviceId, string version = "0.2.0")
    {
        await _gate.WaitAsync();
        try
        {
            await CommitAsync(_data with { Devices = _data.Devices.Select(x => x.Id == deviceId
                ? x with { LastSeenUtc = DateTimeOffset.UtcNow, ServiceVersion = Version.TryParse(version, out _) ? version : "0.2.0" } : x).ToList() });
        }
        finally { _gate.Release(); }
    }
    public async Task<string?> AuthenticateComputerAsync(string id, string token)
    {
        if (!Guid.TryParseExact(id, "N", out _) || token.Length != 64) return null;
        await _gate.WaitAsync();
        try
        {
            var device = _data.Devices.FirstOrDefault(x => x.Id == id);
            return device is not null && device.Enabled && !device.Removed && device.ControlTokenHash.Length == 64 && SecretEquals(HashToken(token), device.ControlTokenHash)
                ? device.ComputerName : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<DeploymentOverview> OverviewAsync(AdminIdentity? actor = null)
    {
        await _gate.WaitAsync();
        try
        {
            return new(_data.Devices.Where(device => actor is null || actor.CanDevice(device.Id)).Select(x => new DeploymentDevice(x.Id, x.ComputerName,
                x.Enabled && !x.Removed && x.LastSeenUtc.HasValue && DateTimeOffset.UtcNow - x.LastSeenUtc.Value < TimeSpan.FromSeconds(20), x.LastSeenUtc, x.ServiceVersion) { Enabled = x.Enabled, Removed = x.Removed }).ToList(),
                _data.Package, _data.Jobs.Where(job => actor is null || actor.CanDevice(job.DeviceId)).OrderByDescending(x => x.CreatedUtc).Take(500).ToList())
            {
                Batches = _data.Batches.OrderByDescending(batch => batch.Result.CreatedUtc)
                    .Where(batch => batch.Result.Items.Any(item => actor is null || actor.CanDevice(item.DeviceId))).Take(50)
                    .Select(batch => new InstallBatchSummary(batch.Result.Id,
                        _data.Packages.FirstOrDefault(package => package.Id == batch.Result.PackageId)?.Name
                            ?? batch.Result.Items.FirstOrDefault(item => item.Job is not null)?.Job?.Package.Name ?? "Software installation", batch.Result.CreatedUtc,
                        batch.Result.Items.Count(item => (actor is null || actor.CanDevice(item.DeviceId)) && item.Job is not null),
                        batch.Result.Items.Count(item => (actor is null || actor.CanDevice(item.DeviceId)) && item.Job is null))).ToList()
            };
        }
        finally { _gate.Release(); }
    }

    public async Task<InstallJob> CreateAsync(InstallRequest request, AdminIdentity? actor = null)
    {
        if(actor is not null && (!actor.Can(AdminPermission.Install)||!actor.CanDevice(request.DeviceId)))throw new UnauthorizedAccessException("Installation permission for this device is required.");
        if (!Guid.TryParseExact(request.Id, "N", out _)) throw new ArgumentException("Invalid job ID.");
        await _gate.WaitAsync();
        try
        {
            var old = _data.Jobs.FirstOrDefault(x => x.Id == request.Id);
            if (old is not null)
            {
                if (old.DeviceId != request.DeviceId) throw new InvalidOperationException("The job ID belongs to another device.");
                return old;
            }
            var device = _data.Devices.FirstOrDefault(x => x.Id == request.DeviceId)
                ?? throw new ArgumentException("Pair the client service first.");
            if (!device.LastSeenUtc.HasValue || DateTimeOffset.UtcNow - device.LastSeenUtc.Value > TimeSpan.FromSeconds(20))
                throw new InvalidOperationException("The paired service is offline. Start Admin and the service, then refresh.");
            if (!device.Enabled || device.Removed) throw new InvalidOperationException("This device is disabled or removed.");
            var package = request.PackageId.Length == 0 ? _data.Package :
                _data.Packages.Append(_data.Package).FirstOrDefault(x => x is not null && x.Id == request.PackageId);
            if (package is null) throw new InvalidOperationException("Choose an approved package first.");
            if (request.PackageId.Length != 0 && request.PackageId != package.Id)
                throw new InvalidOperationException("The prepared installer changed. Select your installer again before installing.");
            if(actor is not null && !ApplicationPackagePolicy.IsGeneral(package))throw new InvalidOperationException("Reimport this legacy package as an ordinary approved installer.");
            if (!ApplicationPackagePolicy.IsApproved(package)) throw new InvalidDataException("The imported package profile is invalid.");
            if (ApplicationPackagePolicy.IsGeneral(package)
                && (!Version.TryParse(device.ServiceVersion, out var serviceVersion) || serviceVersion < new Version(0, 4, 0)))
                throw new InvalidOperationException("Update this Client's background installer to version 0.4.0 before sending EXE/MSI applications.");
            if (ApplicationPackagePolicy.NeedsUpdatedService(package)
                && (!Version.TryParse(device.ServiceVersion, out var automaticVersion) || automaticVersion < new Version(0, 5, 0)))
                throw new InvalidOperationException("Update this Client's background installer to version 0.5.0 to use automatic installer detection. Pairing is preserved.");
            if (_data.Jobs.Any(x => x.DeviceId == device.Id && !SevenZipPolicy.IsTerminal(x.State)))
                throw new InvalidOperationException("This client already has an installation job in progress.");
            var job = new InstallJob { Id = request.Id, DeviceId = device.Id, ComputerName = device.ComputerName, Package = package, AdminId = actor?.Id ?? "", AdminUsername = actor?.Username ?? "", AdminDisplayName = actor?.DisplayName ?? "" };
            await CommitAsync(_data with { Jobs = _data.Jobs.Append(job).ToList() });
            return job;
        }
        finally { _gate.Release(); }
    }

    public async Task<InstallJob?> NextAsync(string deviceId)
    {
        await _gate.WaitAsync();
        try
        {
            var job = _data.Jobs.FirstOrDefault(x => x.DeviceId == deviceId && !SevenZipPolicy.IsTerminal(x.State));
            if (job is null) return null;
            if (job.State == InstallState.Queued)
            {
                job = job with { State = InstallState.Downloading, UpdatedUtc = DateTimeOffset.UtcNow, Details = "Client service accepted the job." };
                await CommitAsync(_data with { Jobs = _data.Jobs.Select(x => x.Id == job.Id ? job : x).ToList() });
            }
            return job; // Repeat delivery uses the same ID; the client ledger prevents repeat execution.
        }
        finally { _gate.Release(); }
    }

    public async Task<InstallJob?> OwnJobAsync(string deviceId, string jobId)
    {
        await _gate.WaitAsync();
        try { return _data.Jobs.FirstOrDefault(x => x.Id == jobId && x.DeviceId == deviceId); }
        finally { _gate.Release(); }
    }

    public static bool CanTransition(InstallState old, InstallState next) => old == next ||
        !SevenZipPolicy.IsTerminal(old) && (next is InstallState.Failed or InstallState.NeedsReview ||
        old == InstallState.Downloading && next == InstallState.Verifying ||
        old == InstallState.Verifying && (next is InstallState.Installing or InstallState.AlreadyInstalled) ||
        old == InstallState.Installing && next == InstallState.Installed);

    public async Task<(InstallJob Job, bool Changed)> ReportAsync(string deviceId, InstallReport report)
    {
        await _gate.WaitAsync();
        try
        {
            var old = _data.Jobs.FirstOrDefault(x => x.Id == report.JobId && x.DeviceId == deviceId)
                ?? throw new ArgumentException("Installation job not found.");
            if (!CanTransition(old.State, report.State)) throw new InvalidOperationException("Invalid installation status transition.");
            if ((report.State is InstallState.Installed or InstallState.AlreadyInstalled)
                && !ApplicationPackagePolicy.VerifiedResult(old.Package, report.InstalledVersion, report.Evidence))
                throw new ArgumentException("Installed version verification is required.");
            if (report.State == InstallState.Installed && !ApplicationPackagePolicy.SuccessfulExit(old.Package, report.ExitCode))
                throw new ArgumentException("A successful installer exit code is required.");
            if (old.State == report.State) return (old, false);
            var details = report.Details ?? "";
            var updated = old with { State = report.State, UpdatedUtc = DateTimeOffset.UtcNow,
                Details = details[..Math.Min(details.Length, 2000)], ExitCode = report.ExitCode, InstalledVersion = report.InstalledVersion,
                Evidence = report.Evidence };
            await CommitAsync(_data with { Jobs = _data.Jobs.Select(x => x.Id == old.Id ? updated : x).ToList() });
            return (updated, true);
        }
        finally { _gate.Release(); }
    }

    public string PackagePath(ApprovedPackage package)
    {
        if (!ApplicationPackagePolicy.IsApproved(package)) throw new InvalidDataException("Invalid package profile.");
        return ApplicationPackagePolicy.IsGeneral(package)
            ? Path.Combine(_root, "packages", package.Id, package.FileName) : InstallerPath;
    }

    public async Task<ApprovedPackage> ImportApplicationAsync(Stream stream, string fileName, ApplicationImportOptions options)
    {
        ApplicationPackagePolicy.Validate(fileName, options);
        if (options.VerificationMode != ApplicationPackagePolicy.Exact && options.VerificationMode != ApplicationPackagePolicy.Auto)
            throw new ArgumentException("Automatic installation profiles must be identified from the uploaded installer.");
        await _gate.WaitAsync();
        string? temporary = null;
        try
        {
            if (_data.Jobs.Any(x => !SevenZipPolicy.IsTerminal(x.State)))
                throw new InvalidOperationException("Wait for active installations to finish before importing another package.");
            var id = "app-" + Guid.NewGuid().ToString("N");
            var folder = Path.Combine(_root, "packages", id);
            Directory.CreateDirectory(folder);
            foreach (var path in new[] { Path.Combine(_root, "packages"), folder })
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Package storage must not contain links.");
            temporary = Path.Combine(folder, "upload.tmp");
            long length = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536]; int count;
                while ((count = await stream.ReadAsync(buffer)) > 0)
                {
                    length += count;
                    if (length > ApplicationPackagePolicy.MaximumBytes) throw new InvalidDataException("Installer exceeds 2 GiB.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count));
                }
            }
            var digest = Convert.ToHexString(hash.GetHashAndReset());
            if (length == 0) throw new InvalidDataException("The installer is empty.");
            if (options.ExpectedSha256.Length > 0 && !string.Equals(digest, options.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installer does not match the supplied SHA-256.");
            var header = new byte[8];
            await using (var input = File.OpenRead(temporary))
            {
                var count = await input.ReadAsync(header);
                var isExe = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (isExe ? count < 2 || header[0] != 'M' || header[1] != 'Z'
                    : count != 8 || !header.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
                    throw new InvalidDataException("File header does not match the selected EXE/MSI type.");
            }
            if (options.VerificationMode == ApplicationPackagePolicy.Auto)
                options = InstallerCatalog.Resolve(InstallerCatalog.Inspect(temporary, fileName), options);
            ApplicationPackagePolicy.Validate(fileName, options);
            var package = new ApprovedPackage { Id = id, Profile = "application", Name = options.Name,
                FileName = fileName, Sha256 = digest, Length = length, SilentArguments = options.SilentArguments,
                InstalledDisplayName = options.InstalledDisplayName, InstalledDisplayVersion = options.InstalledDisplayVersion,
                VerificationMode = options.VerificationMode, InstalledProductCode = options.InstalledProductCode,
                InstalledPublisher = options.InstalledPublisher };
            File.Move(temporary, PackagePath(package));
            await CommitAsync(_data with { Package = package, Packages = _data.Packages.Where(x => x.Id != package.Id).Append(package).ToList() });
            return package;
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { _gate.Release(); }
        }
    }
}
