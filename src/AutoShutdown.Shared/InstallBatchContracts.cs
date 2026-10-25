namespace AutoShutdown.Shared;

public sealed record InstallBatchRequest(string Id, string PackageId, List<string> DeviceIds);
public sealed record InstallRetryRequest(string Id, List<string> JobIds);
public sealed record InstallBatchItem(string DeviceId, string ComputerName, string Details, InstallJob? Job = null);
public sealed record InstallBatchResult(string Id, string PackageId, List<InstallBatchItem> Items, DateTimeOffset CreatedUtc);
public sealed record InstallBatchSummary(string Id, string PackageName, DateTimeOffset CreatedUtc, int Accepted, int Rejected);
