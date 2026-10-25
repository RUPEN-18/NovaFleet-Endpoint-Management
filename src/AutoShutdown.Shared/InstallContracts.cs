using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AutoShutdown.Shared;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InstallState { Queued, Downloading, Verifying, Installing, Installed, AlreadyInstalled, Failed, NeedsReview }

public sealed record ApprovedPackage
{
    public string Profile { get; init; } = "7zip";
    public string Id { get; init; } = SevenZipPolicy.PackageId;
    public string Name { get; init; } = "7-Zip 26.04 (x64 EXE)";
    public string FileName { get; init; } = SevenZipPolicy.FileName;
    public required string Sha256 { get; init; }
    public long Length { get; init; }
    public string SilentArguments { get; init; } = "";
    public string InstalledDisplayName { get; init; } = "";
    public string InstalledDisplayVersion { get; init; } = "";
    public string VerificationMode { get; init; } = "exact";
    public string InstalledProductCode { get; init; } = "";
    public string InstalledPublisher { get; init; } = "";
}

public sealed record InstallJob
{
    public string BatchId { get; init; } = "";
    public string RetryOfJobId { get; init; } = "";
    public string AdminId { get; init; } = "";
    public string AdminUsername { get; init; } = "";
    public string AdminDisplayName { get; init; } = "";
    public required string Id { get; init; }
    public required string DeviceId { get; init; }
    public required string ComputerName { get; init; }
    public required ApprovedPackage Package { get; init; }
    public InstallState State { get; init; } = InstallState.Queued;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Details { get; init; } = "Waiting for the deployment service.";
    public int? ExitCode { get; init; }
    public string InstalledVersion { get; init; } = "";
    public InstallationEvidence? Evidence { get; init; }
}

public sealed record InstallRequest(string Id, string DeviceId, string PackageId = "");
public sealed record InstallationEvidence(string Mode, string DisplayName, string Publisher = "", string ProductCode = "", bool ExecutableVerified = false);
public sealed record InstallReport(string JobId, InstallState State, string Details, int? ExitCode = null, string InstalledVersion = "", InstallationEvidence? Evidence = null);
public sealed record DeploymentDevice(string Id, string ComputerName, bool Online, DateTimeOffset? LastSeenUtc, string ServiceVersion = "0.2.0")
{
    public bool Enabled { get; init; } = true;
    public bool Removed { get; init; }
}
public sealed record DeploymentHeartbeat(string ServiceVersion);
public sealed record DeploymentOverview(List<DeploymentDevice> Devices, ApprovedPackage? Package, List<InstallJob> Jobs)
{
    public List<InstallBatchSummary> Batches { get; init; } = new();
    public int ActiveCount { get; init; }
    public int SuccessfulCount { get; init; }
    public int FailedCount { get; init; }
}

public sealed record DeviceConnection
{
    public required string ServerUrl { get; init; }
    public required string ServerCertificateSha256 { get; init; }
    public required string DeviceId { get; init; }
    public required string DeviceToken { get; init; }
    public required string ComputerName { get; init; }
    public string? ControlServerUrl { get; init; }
    public string? ControlApiKey { get; init; }
}

public static class SevenZipPolicy
{
    public const string PackageId = "7zip-exe-x64-26.04";
    public const string FileName = "7z2604-x64.exe";
    public const string Version = "26.04";
    public const long MaximumBytes = 32 * 1024 * 1024;
    private const string ReleaseApi = "https://api.github.com/repos/ip7z/7zip/releases/tags/26.04";

    public static bool IsHash(string? text) => text is not null && Regex.IsMatch(text, @"\A[0-9a-fA-F]{64}\z");
    public static bool IsTerminal(InstallState state) => state is InstallState.Installed or InstallState.AlreadyInstalled or InstallState.Failed or InstallState.NeedsReview;
    public static bool IsApproved(ApprovedPackage p) => p.Id == PackageId && p.FileName == FileName && IsHash(p.Sha256) && p.Length > 0 && p.Length <= MaximumBytes;

    public static ApprovedPackage ParseOfficialRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetProperty("tag_name").GetString() != "26.04")
            throw new InvalidDataException("The official release version does not match.");
        var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(x => x.GetProperty("name").GetString() == FileName);
        if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("digest", out var digest))
            throw new InvalidDataException("The official release API did not provide a SHA-256 digest. Installation is blocked.");
        var text = digest.GetString() ?? "";
        if (!text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The official asset has no usable SHA-256 digest.");
        var package = new ApprovedPackage { Sha256 = text[7..].ToUpperInvariant(), Length = asset.GetProperty("size").GetInt64() };
        if (!IsApproved(package)) throw new InvalidDataException("The official release metadata is invalid.");
        return package;
    }

    public static async Task<ApprovedPackage> GetOfficialAsync(CancellationToken token = default)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(35) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NovaDeploymentTest/0.2");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.GetAsync(ReleaseApi, token);
        response.EnsureSuccessStatusCode();
        return ParseOfficialRelease(await response.Content.ReadAsStringAsync(token));
    }
}

public static class DeploymentTransport
{
    public static HttpClientHandler CreateHandler(Uri server, string certificateHash)
    {
        if (server.Scheme != Uri.UriSchemeHttps || !SevenZipPolicy.IsHash(certificateHash))
            throw new InvalidDataException("A pinned HTTPS server connection is required.");
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            // Deployment connects directly to the explicitly paired PC, including on corporate networks.
            // Vendor metadata downloads keep their normal proxy configuration.
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert is not null && DateTime.UtcNow >= cert.NotBefore.ToUniversalTime()
                && DateTime.UtcNow <= cert.NotAfter.ToUniversalTime()
                && string.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256), certificateHash, StringComparison.OrdinalIgnoreCase)
        };
        return handler;
    }
    public static HttpClient Create(Uri server, string certificateHash) => new(CreateHandler(server, certificateHash))
        { BaseAddress = server, Timeout = TimeSpan.FromMinutes(15) };
}

// Public status contains no credentials. Only the service/Administrators may change it.
public static class DeploymentActivity
{
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoShutdown.Nova.DeploymentStatus");
    public static string FilePath => Path.Combine(DirectoryPath, "activity.json");
    public static bool IsInstalling()
    {
        if (!File.Exists(FilePath)) return false;
        try
        {
            if (new FileInfo(FilePath).Length > 16384) return true;
            using var json = JsonDocument.Parse(File.ReadAllText(FilePath));
            return json.RootElement.GetProperty("busy").GetBoolean();
        }
        catch { return true; } // Defer shutdown if service status cannot be read safely.
    }
}
