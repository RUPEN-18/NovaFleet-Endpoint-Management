using AutoShutdown.Admin.Infrastructure;
using AutoShutdown.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); checks++;
}
async Task Reject(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
    { Check(true, name); return; }
    throw new Exception("FAIL: " + name);
}
string Id() => Guid.NewGuid().ToString("N");
var root = Path.Combine(Path.GetTempPath(), "LTStep4Checks-" + Id());
Directory.CreateDirectory(root);
try
{
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var bytes = new byte[] { (byte)'M', (byte)'Z', 1, 2, 3 }; // Test data; never executed or installed.
    var package = new ApprovedPackage
    {
        Id = "app-" + Id(), Profile = "application", Name = "Test application", FileName = "test-app.exe",
        Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), SilentArguments = "/quiet",
        InstalledDisplayName = "Test application", InstalledDisplayVersion = "1.2.3", VerificationMode = ApplicationPackagePolicy.Exact
    };
    var a = Id(); var b = Id(); var offline = Id(); var disabled = Id(); var outdated = Id();
    var serviceToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var controlToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    EnrolledDevice Device(string id, string name, DateTimeOffset? seen, bool enabled = true, string version = "0.5.0") =>
        new(id, name, InstallStore.HashToken(serviceToken), seen, InstallStore.HashToken(controlToken), version, name, enabled);
    var seed = new InstallDatabase
    {
        Devices = new() { Device(a, "CLIENT-01", DateTimeOffset.UtcNow), Device(b, "CLIENT-02", DateTimeOffset.UtcNow),
            Device(offline, "OFFLINE-PC", DateTimeOffset.UtcNow.AddMinutes(-2)), Device(disabled, "DISABLED-PC", DateTimeOffset.UtcNow, false),
            Device(outdated, "OLD-SERVICE", DateTimeOffset.UtcNow, version: "0.3.0") },
        Package = package, Packages = new() { package }
    };
    var path = Path.Combine(root, "install-jobs.json");
    var legacySeed = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(seed, json))!.AsObject();
    legacySeed.Remove("batches");
    await File.WriteAllTextAsync(path, legacySeed.ToJsonString());
    var installerFolder = Path.Combine(root, "packages", package.Id); Directory.CreateDirectory(installerFolder);
    await File.WriteAllBytesAsync(Path.Combine(installerFolder, package.FileName), bytes);
    var store = new InstallStore(root);
    var super = new AdminIdentity(Id(), "root", "Root Admin", "SuperAdmin", AdminPermission.All, new());
    var limited = new AdminIdentity(Id(), "limited", "Limited Admin", "Admin", AdminPermission.Install, new() { b });
    var viewer = limited with { Permissions = AdminPermission.None };
    Check((await store.OverviewAsync()).Jobs.Count == 0, "legacy store opens without creating installations");
    Check(File.Exists(path + ".pre-step4.bak"), "pre-Step-4 history is backed up before batch writes");
    await Reject(() => store.CreateBatchAsync(new(Id(), package.Id, new() { a, b }), limited), "mixed unassigned selection is rejected before any jobs are created");
    Check((await store.OverviewAsync()).Jobs.Count == 0, "scope denial cannot partially queue a batch");
    await Reject(() => store.CreateBatchAsync(new(Id(), package.Id, new() { b }), viewer), "view-only account cannot queue installations");
    await Reject(() => store.CreateBatchAsync(new(Id(), package.Id, new()), super), "empty PC selection is rejected");
    await Reject(() => store.CreateBatchAsync(new(Id(), package.Id, Enumerable.Range(0, 101).Select(_ => Id()).ToList()), super), "batch size limit is enforced on the server");
    await Reject(() => store.CreateBatchAsync(new("bad-id", package.Id, new() { a }), super), "malformed request identifiers are rejected");
    var request = new InstallBatchRequest(Id(), package.Id, new() { a, b, a, offline, disabled, outdated });
    var batch = await store.CreateBatchAsync(request, super);
    Check(batch.Created && batch.Result.Items.Count == 5, "a repeated PC selection creates only one result per PC");
    Check(batch.Result.Items.Count(item => item.Job is not null) == 2, "eligible PCs queue while offline, disabled and old services receive individual rejections");
    Check(batch.Result.Items.Single(item => item.DeviceId == offline).Job is null, "offline PC is Not queued rather than Failed");
    var jobA = batch.Result.Items.Single(item => item.DeviceId == a).Job!;
    var jobB = batch.Result.Items.Single(item => item.DeviceId == b).Job!;
    Check(jobA.Id != jobB.Id && jobA.BatchId == request.Id && jobB.BatchId == request.Id, "PCs have separate job IDs within the same batch");
    Check(jobB.AdminId == super.Id, "batch jobs retain the initiating administrator identity");
    var repeated = await store.CreateBatchAsync(request with { DeviceIds = request.DeviceIds.AsEnumerable().Reverse().ToList() }, super);
    Check(!repeated.Created && repeated.Result.Items.Single(item => item.DeviceId == b).Job!.Id == jobB.Id,
        "repeated request returns the original jobs without duplicate installation");
    await Reject(() => store.CreateBatchAsync(request with { PackageId = "app-" + Id() }, super), "request identifier cannot be reused for a different package");
    await Reject(() => store.CreateBatchAsync(request, super with { Id = Id() }), "another administrator cannot reuse an existing request identifier");
    var occupied = await store.CreateBatchAsync(new(Id(), package.Id, new() { a, b }), super);
    Check(occupied.Result.Items.All(item => item.Job is null), "busy PCs are rejected without creating parallel jobs");
    Check((await store.NextAsync(a))!.Id == jobA.Id && (await store.NextAsync(a))!.Id == jobA.Id, "repeat service polling delivers the same job ID");
    await Reject(() => store.ReportAsync(a, new(jobA.Id, InstallState.Installed, "premature", 0, "1.2.3")), "queued delivery cannot jump directly to Installed");
    await store.ReportAsync(a, new(jobA.Id, InstallState.Verifying, "verified bytes"));
    await store.ReportAsync(a, new(jobA.Id, InstallState.Installing, "installer running"));
    await Reject(() => store.ReportAsync(a, new(jobA.Id, InstallState.Installed, "wrong version", 0, "9.9")), "success is blocked without the expected installed version");
    await Reject(() => store.ReportAsync(a, new(jobA.Id, InstallState.Installed, "failed exit", 1603, "1.2.3")), "a failed installer exit cannot be reported as success");
    await store.ReportAsync(a, new(jobA.Id, InstallState.Installed, "installation verified", 0, "1.2.3"));
    await store.NextAsync(b);
    await store.ReportAsync(b, new(jobB.Id, InstallState.Failed, "simulated transfer failure"));
    var updated = await store.GetBatchAsync(request.Id, super);
    Check(updated.Items.Single(item => item.DeviceId == a).Job!.State == InstallState.Installed
        && updated.Items.Single(item => item.DeviceId == b).Job!.State == InstallState.Failed, "batch results show each PC's current service report");
    var scoped = await store.GetBatchAsync(request.Id, limited);
    Check(scoped.Items.Count == 1 && scoped.Items[0].DeviceId == b, "batch results expose only assigned PCs");
    await Reject(() => store.GetBatchAsync(request.Id, limited with { DeviceIds = new() }), "unassigned account cannot read batch results");
    var retryRequest = new InstallRetryRequest(Id(), new() { jobA.Id, jobB.Id });
    var retry = await store.RetryFailedAsync(retryRequest, super);
    Check(retry.Result.Items.Single(item => item.DeviceId == a).Job is null, "retry excludes successfully installed PCs");
    var retryB = retry.Result.Items.Single(item => item.DeviceId == b).Job!;
    Check(retryB.Id != jobB.Id && retryB.RetryOfJobId == jobB.Id, "retry creates a new attempt linked to its original failed job");
    Check((await store.OwnJobAsync(b, jobB.Id))!.State == InstallState.Failed, "retry preserves the original failure record");
    var repeatedRetry = await store.RetryFailedAsync(retryRequest, super);
    Check(!repeatedRetry.Created && repeatedRetry.Result.Items.Single(item => item.DeviceId == b).Job!.Id == retryB.Id, "repeating the retry request cannot create another attempt");
    var duplicateRetry = await store.RetryFailedAsync(new(Id(), new() { jobB.Id }), super);
    Check(duplicateRetry.Result.Items.All(item => item.Job is null), "an old failed attempt with a newer retry is not retried again");
    await Reject(() => store.RetryFailedAsync(new(Id(), new() { jobA.Id, jobB.Id }), limited), "retry checks every selected job against device permissions");
    await Reject(() => store.RetryFailedAsync(new(Id(), new() { jobB.Id }), viewer), "view-only account cannot retry failed jobs");
    var activeRetry = await store.RetryFailedAsync(new(Id(), new() { retryB.Id }), super);
    Check(activeRetry.Result.Items.All(item => item.Job is null), "active attempts cannot be retried");
    await store.NextAsync(b);
    await store.ReportAsync(b, new(retryB.Id, InstallState.NeedsReview, "simulated interrupted installer"));
    var reviewRetry = await store.RetryFailedAsync(new(Id(), new() { retryB.Id }), super);
    Check(reviewRetry.Result.Items.All(item => item.Job is null), "NeedsReview is excluded from automatic retry");
    // Make a later independently submitted attempt fail, then retry that latest confirmed failure.
    await store.TouchAsync(b, "0.5.0");
    var later = await store.CreateBatchAsync(new(Id(), package.Id, new() { b }), limited);
    var laterB = later.Result.Items[0].Job!;
    await store.NextAsync(b); await store.ReportAsync(b, new(laterB.Id, InstallState.Failed, "new confirmed failure"));
    var latestRetry = await store.RetryFailedAsync(new(Id(), new() { laterB.Id }), limited);
    Check(latestRetry.Result.Items[0].Job?.RetryOfJobId == laterB.Id, "the latest confirmed failure can be retried by an assigned administrator");
    var concurrentPC = await store.PairAsync("CONCURRENT-PC", "https://127.0.0.1:5056/", new string('A', 64));
    await store.TouchAsync(concurrentPC.DeviceId, "0.5.0");
    var concurrentRequest = new InstallBatchRequest(Id(), package.Id, new() { concurrentPC.DeviceId });
    var concurrentResults = await Task.WhenAll(store.CreateBatchAsync(concurrentRequest, super), store.CreateBatchAsync(concurrentRequest, super));
    Check(concurrentResults.Count(result => result.Created) == 1 && (await store.OverviewAsync()).Jobs.Count(job => job.DeviceId == concurrentPC.DeviceId) == 1,
        "simultaneous duplicate requests create only one installation attempt");
    var missingPC = await store.PairAsync("MISSING-INSTALLER-PC", "https://127.0.0.1:5056/", new string('A', 64));
    await store.TouchAsync(missingPC.DeviceId, "0.5.0");
    var installerPath = Path.Combine(installerFolder, package.FileName);
    File.Move(installerPath, installerPath + ".test-backup");
    try
    {
        var missing = await store.CreateBatchAsync(new(Id(), package.Id, new() { missingPC.DeviceId }), super);
        Check(missing.Result.Items[0].Job is null, "missing installer bytes are rejected before an attempt is queued");
    }
    finally { File.Move(installerPath + ".test-backup", installerPath); }
    var storeAfterRestart = new InstallStore(root);
    Check((await storeAfterRestart.GetBatchAsync(request.Id, super)).Items.Count == 5, "batch membership survives reopening the store");
    Check(!(await storeAfterRestart.CreateBatchAsync(request, super)).Created, "idempotency survives server restart");
    Check(await storeAfterRestart.AuthenticateAsync(b, serviceToken) == b && await storeAfterRestart.AuthenticateComputerAsync(b, controlToken) == "CLIENT-02",
        "existing service and Client pairing credentials survive batch migration");
    await Reject(() => storeAfterRestart.RetryFailedAsync(retryRequest with { JobIds = new() { jobB.Id } }, super), "retry identifier cannot be reused for different jobs");
    var legacyJson = JsonSerializer.Serialize(jobA, json);
    using (var document = JsonDocument.Parse(legacyJson))
        Check(document.RootElement.GetProperty("id").GetString() == jobA.Id && document.RootElement.GetProperty("state").GetString() == "Queued",
            "service job ID and existing state JSON stay compatible");
    var legacyRoot = Path.Combine(root, "legacy-failure"); Directory.CreateDirectory(legacyRoot);
    var legacyJob = new InstallJob { Id = Id(), DeviceId = a, ComputerName = "CLIENT-01", Package = package, State = InstallState.Failed };
    var legacyDatabase = seed with { Devices = new() { Device(a, "CLIENT-01", DateTimeOffset.UtcNow) }, Jobs = new() { legacyJob } };
    var legacyNode = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(legacyDatabase, json))!.AsObject();
    legacyNode.Remove("batches");
    legacyNode["jobs"]![0]!.AsObject().Remove("batchId"); legacyNode["jobs"]![0]!.AsObject().Remove("retryOfJobId");
    await File.WriteAllTextAsync(Path.Combine(legacyRoot, "install-jobs.json"), legacyNode.ToJsonString());
    var legacyPackageFolder = Path.Combine(legacyRoot, "packages", package.Id); Directory.CreateDirectory(legacyPackageFolder);
    await File.WriteAllBytesAsync(Path.Combine(legacyPackageFolder, package.FileName), bytes);
    var legacyStore = new InstallStore(legacyRoot);
    var legacyRetry = await legacyStore.RetryFailedAsync(new(Id(), new() { legacyJob.Id }), super);
    Check(legacyRetry.Result.Items[0].Job?.RetryOfJobId == legacyJob.Id && (await legacyStore.OwnJobAsync(a, legacyJob.Id))!.BatchId == "",
        "pre-Step-4 failed history can be retried without rewriting its original record");
    var supersededRoot = Path.Combine(root, "superseded-failure"); Directory.CreateDirectory(supersededRoot);
    var newerPackage = package with { Id = "app-" + Id(), Name = "Newer approved software" };
    var newerJob = legacyJob with { Id = Id(), Package = newerPackage, State = InstallState.Installed, InstalledVersion = "1.2.3", CreatedUtc = legacyJob.CreatedUtc.AddSeconds(1) };
    await File.WriteAllTextAsync(Path.Combine(supersededRoot, "install-jobs.json"), JsonSerializer.Serialize(legacyDatabase with { Jobs = new() { legacyJob, newerJob } }, json));
    var supersededStore = new InstallStore(supersededRoot);
    var supersededRetry = await supersededStore.RetryFailedAsync(new(Id(), new() { legacyJob.Id }), super);
    Check(supersededRetry.Result.Items[0].Job is null, "a later successful attempt on the PC excludes an older failed retry even if its package identifier changed");
    // The actual batch endpoints use real session authorization against isolated test data.
    var security = new AdminSecurityStore(Path.Combine(root, "auth"));
    var rootSession = await security.SetupAsync(new("batchroot", "Batch Admin", "test batch password 2026"));
    var ordinary = await security.AccountAsync(rootSession.Identity, new("", "batchuser", "Batch User", "Admin", AdminPermission.Install, new() { b }, true, "test ordinary password 2026"));
    var ordinarySession = await security.LoginAsync(new(ordinary.Username, "test ordinary password 2026"));
    var noInstall = await security.AccountAsync(rootSession.Identity, new("", "batchviewer", "Batch Viewer", "Admin", AdminPermission.None, new() { b }, true, "test viewer password 2026"));
    var viewerSession = await security.LoginAsync(new(noInstall.Username, "test viewer password 2026"));
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = root });
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
    await using var web = builder.Build();
    web.Use(async (HttpContext context, RequestDelegate next) =>
    {
        if (!await AdminSessionEndpoints.Authorize(context, security, AdminSessionEndpoints.InstallPermission(context.Request.Path, context.Request.Method))) return;
        await next(context);
    });
    InstallBatchEndpoints.Map(web, storeAfterRestart, security);
    await web.StartAsync();
    try
    {
        var address = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
        using (var denied = await http.GetAsync("admin/batches/" + request.Id)) Check(denied.StatusCode == HttpStatusCode.Unauthorized, "batch endpoint rejects missing login session");
        http.DefaultRequestHeaders.Authorization = new("Bearer", ordinarySession.Token);
        using (var allowed = await http.GetAsync("admin/batches/" + request.Id))
        {
            allowed.EnsureSuccessStatusCode(); var body = await allowed.Content.ReadFromJsonAsync<InstallBatchResult>(json);
            Check(body?.Items.Count == 1 && body.Items[0].DeviceId == b, "real batch GET returns JSON scoped to assigned PCs");
        }
        using (var denied = await http.PostAsJsonAsync("admin/batches", new InstallBatchRequest(Id(), package.Id, new() { a, b }), json))
            Check(denied.StatusCode == HttpStatusCode.Forbidden, "real batch POST blocks unassigned PCs");
        using (var allowed = await http.PostAsJsonAsync("admin/batches", new InstallBatchRequest(Id(), package.Id, new() { b }), json))
        {
            allowed.EnsureSuccessStatusCode(); var body = await allowed.Content.ReadFromJsonAsync<InstallBatchResult>(json);
            Check(body?.Items.Count == 1 && body.Items[0].Job is null, "real batch POST returns per-PC busy rejection as JSON");
        }
        using (var denied = await http.PostAsJsonAsync("admin/retry", new InstallRetryRequest(Id(), new() { jobA.Id }), json))
            Check(denied.StatusCode == HttpStatusCode.Forbidden, "real retry endpoint blocks unassigned jobs");
        http.DefaultRequestHeaders.Authorization = new("Bearer", rootSession.Token);
        await storeAfterRestart.TouchAsync(missingPC.DeviceId, "0.5.0");
        var apiRequest = new InstallBatchRequest(Id(), package.Id, new() { missingPC.DeviceId });
        InstallBatchResult apiQueued;
        using (var accepted = await http.PostAsJsonAsync("admin/batches", apiRequest, json))
        {
            accepted.EnsureSuccessStatusCode(); apiQueued = (await accepted.Content.ReadFromJsonAsync<InstallBatchResult>(json))!;
            Check(apiQueued.Items[0].Job?.State == InstallState.Queued, "real batch POST writes an accepted PC's queued job to JSON");
        }
        using (var repeatedResponse = await http.PostAsJsonAsync("admin/batches", apiRequest, json))
        {
            repeatedResponse.EnsureSuccessStatusCode(); var body = await repeatedResponse.Content.ReadFromJsonAsync<InstallBatchResult>(json);
            Check(body?.Items[0].Job?.Id == apiQueued.Items[0].Job!.Id, "real batch POST preserves job identity on repeated submission");
        }
        var apiFailedJob = apiQueued.Items[0].Job!;
        await storeAfterRestart.NextAsync(missingPC.DeviceId);
        await storeAfterRestart.ReportAsync(missingPC.DeviceId, new(apiFailedJob.Id, InstallState.Failed, "API test failure"));
        using (var accepted = await http.PostAsJsonAsync("admin/retry", new InstallRetryRequest(Id(), new() { apiFailedJob.Id }), json))
        {
            accepted.EnsureSuccessStatusCode(); var body = await accepted.Content.ReadFromJsonAsync<InstallBatchResult>(json);
            Check(body?.Items[0].Job?.RetryOfJobId == apiFailedJob.Id, "real retry POST returns a new attempt linked to the failed job");
        }
        http.DefaultRequestHeaders.Authorization = new("Bearer", viewerSession.Token);
        using (var denied = await http.PostAsJsonAsync("admin/batches", new InstallBatchRequest(Id(), package.Id, new() { b }), json))
            Check(denied.StatusCode == HttpStatusCode.Forbidden, "real batch endpoint enforces installation permission");
        using (var denied = await http.PostAsJsonAsync("ADMIN/RETRY/", new InstallRetryRequest(Id(), new() { jobB.Id }), json))
            Check(denied.StatusCode == HttpStatusCode.Forbidden, "case and trailing slash cannot bypass retry permission");
        Check(AdminSessionEndpoints.InstallPermission("/ADMIN/BATCHES/", "POST") == AdminPermission.Install
            && AdminSessionEndpoints.InstallPermission("/admin/batches/" + request.Id, "DELETE") == AdminPermission.Manage,
            "batch permissions normalize paths and unknown mutations fail closed");
        var audit = await security.AuditAsync(rootSession.Identity);
        Check(audit.Any(entry => entry.AdminId == ordinary.Id && entry.Action == "INSTALL_NOT_QUEUED"), "batch outcome audit identifies the initiating administrator");
    }
    finally { await web.StopAsync(); }
    Console.WriteLine($"All {checks} Step 4 installation batch checks passed. No real software was installed.");
}
finally { try { Directory.Delete(root, true); } catch { } }
