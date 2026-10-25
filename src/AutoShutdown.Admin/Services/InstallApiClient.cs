using AutoShutdown.Admin.Infrastructure;
using AutoShutdown.Shared;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace AutoShutdown.Admin.Services;

public sealed class InstallApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    public InstallApiClient(DeploymentBootstrap bootstrap, string sessionToken)
    {
        _http = DeploymentTransport.Create(new Uri(bootstrap.Url), bootstrap.CertificateHash);
        _http.DefaultRequestHeaders.Add("X-Nova-Admin-Token", bootstrap.Secrets.AdminToken);
        _http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",sessionToken);
    }
    public async Task<DeploymentOverview> OverviewAsync() =>
        await _http.GetFromJsonAsync<DeploymentOverview>("admin/overview", _json)
        ?? throw new InvalidOperationException("No deployment overview returned.");
    public async Task<DeviceConnection> PairAsync()
    {
        using var response = await _http.PostAsJsonAsync("admin/pair", new InstallServer.PairRequest(Environment.MachineName), _json);
        return await ReadAsync<DeviceConnection>(response);
    }
    public async Task<DeploymentLanInfo> NetworkAsync() =>
        await _http.GetFromJsonAsync<DeploymentLanInfo>("admin/network", _json)
        ?? throw new InvalidOperationException("No LAN settings returned.");
    public async Task SaveNetworkAsync(string? address)
    {
        using var response = await _http.PostAsJsonAsync("admin/network", new DeploymentLanSettings(address), _json);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ApiError.Message(await response.Content.ReadAsStringAsync()));
    }
    public async Task<DeviceConnection> PairRemoteAsync(string computer)
    {
        using var response = await _http.PostAsJsonAsync("admin/pair/remote", new InstallServer.PairRequest(computer), _json);
        return await ReadAsync<DeviceConnection>(response);
    }
    public async Task<InstallJob> CreateAsync(string deviceId, string packageId = "")
    {
        using var response = await _http.PostAsJsonAsync("admin/jobs", new InstallRequest(Guid.NewGuid().ToString("N"), deviceId, packageId), _json);
        return await ReadAsync<InstallJob>(response);
    }
    public async Task<InstallBatchResult> CreateBatchAsync(InstallBatchRequest request)
    {
        using var response = await _http.PostAsJsonAsync("admin/batches", request, _json);
        return await ReadAsync<InstallBatchResult>(response);
    }
    public async Task<InstallBatchResult> RetryFailedAsync(InstallRetryRequest request)
    {
        using var response = await _http.PostAsJsonAsync("admin/retry", request, _json);
        return await ReadAsync<InstallBatchResult>(response);
    }
    public async Task<InstallBatchResult> BatchAsync(string id) =>
        await _http.GetFromJsonAsync<InstallBatchResult>("admin/batches/" + Uri.EscapeDataString(id), _json)
        ?? throw new InvalidOperationException("No batch result returned.");
    public async Task<ApprovedPackage> ImportApplicationAsync(string path, ApplicationImportOptions options)
    {
        ApplicationPackagePolicy.Validate(Path.GetFileName(path), options);
        await using var input = File.OpenRead(path);
        using var content = new MultipartFormDataContent();
        using var file = new StreamContent(input);
        content.Add(file, "file", Path.GetFileName(path));
        content.Add(new StringContent(options.Name), "name");
        content.Add(new StringContent(options.SilentArguments), "arguments");
        content.Add(new StringContent(options.InstalledDisplayName), "displayName");
        content.Add(new StringContent(options.InstalledDisplayVersion), "version");
        content.Add(new StringContent(options.ExpectedSha256), "sha256");
        content.Add(new StringContent(options.VerificationMode), "mode");
        using var response = await _http.PostAsync("admin/import/application", content);
        return await ReadAsync<ApprovedPackage>(response);
    }
    private async Task<T> ReadAsync<T>(HttpResponseMessage response) where T : class
    {
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ApiError.Message(text));
        return JsonSerializer.Deserialize<T>(text, _json) ?? throw new InvalidOperationException("The deployment response was empty.");
    }
    public async Task<List<ApprovedPackage>> PackagesAsync()=>await _http.GetFromJsonAsync<List<ApprovedPackage>>("admin/packages",_json)??new();
    public void Dispose() => _http.Dispose();
}
