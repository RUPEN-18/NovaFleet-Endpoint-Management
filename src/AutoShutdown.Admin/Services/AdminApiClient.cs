using System.Net.Http;
using AutoShutdown.Admin.Infrastructure;
using AutoShutdown.Shared;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace AutoShutdown.Admin.Services;

public sealed class AdminApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public string BaseUrl { get; }

    public AdminApiClient(ServerOptions options, DeploymentBootstrap bootstrap, string sessionToken)
    {
        BaseUrl = options.ClientBaseUrl.TrimEnd('/');
        _http = DeploymentTransport.Create(new Uri(BaseUrl + "/"),bootstrap.CertificateHash);
        _http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",sessionToken);
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public Task<GlobalSettings?> GetSettingsAsync() => _http.GetFromJsonAsync<GlobalSettings>("api/settings", _json);

    public async Task<GlobalSettings> SaveSettingsAsync(GlobalSettings settings)
    {
        using var request=new HttpRequestMessage(HttpMethod.Put,"api/settings"){Content=JsonContent.Create(settings,options:_json)};
        request.Headers.Add("X-LT-Confirm-Automation","true");
        var response=await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GlobalSettings>(_json))!;
    }

    public async Task<List<ClientSnapshot>> GetClientsAsync() =>
        await _http.GetFromJsonAsync<List<ClientSnapshot>>("api/clients", _json) ?? new();

    public async Task<List<GroupDefinition>> GetGroupsAsync() =>
        await _http.GetFromJsonAsync<List<GroupDefinition>>("api/groups", _json) ?? new();

    public async Task<List<GroupDefinition>> SaveGroupsAsync(List<GroupDefinition> groups)
    {
        var response = await _http.PutAsJsonAsync("api/groups", groups, _json);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<GroupDefinition>>(_json) ?? new();
    }

    public async Task<List<ScheduleDefinition>> GetSchedulesAsync() =>
        await _http.GetFromJsonAsync<List<ScheduleDefinition>>("api/schedules", _json) ?? new();

    public async Task<List<ScheduleDefinition>> SaveSchedulesAsync(List<ScheduleDefinition> schedules)
    {
        var response = await _http.PutAsJsonAsync("api/schedules", schedules, _json);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ScheduleDefinition>>(_json) ?? new();
    }

    public async Task<List<LogEntry>> GetLogsAsync(int take = 1000) =>
        await _http.GetFromJsonAsync<List<LogEntry>>($"api/logs?take={take}", _json) ?? new();

    public async Task<List<DeploymentTestJob>> GetDeploymentTestsAsync() =>
        await _http.GetFromJsonAsync<List<DeploymentTestJob>>("api/deployment-tests", _json) ?? new();

    public async Task<DeploymentTestJob> SendDeploymentTestAsync(DeploymentTestRequest request)
    {
        using var response = await _http.PostAsJsonAsync("api/deployment-tests", request, _json);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ApiError.Message(body));
        return JsonSerializer.Deserialize<DeploymentTestJob>(body, _json)
            ?? throw new InvalidOperationException("The server returned no test job.");
    }

    public async Task<DeliveryResult> SendActionAsync(AdminActionRequest request)
    {
        var response = await _http.PostAsJsonAsync("api/actions", request, _json);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body);
        return JsonSerializer.Deserialize<DeliveryResult>(body, _json) ?? new DeliveryResult();
    }

    public async Task<MediaAsset> UploadImageAsync(string path)
    {
        await using var input = File.OpenRead(path);
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(input);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(path));
        content.Add(fileContent, "file", Path.GetFileName(path));
        var response = await _http.PostAsync("api/media", content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ApiError.Message(body));
        return JsonSerializer.Deserialize<MediaAsset>(body, _json)!;
    }


    public async Task<byte[]?> GetMediaBytesAsync(string mediaId)
    {
        var response = await _http.GetAsync($"media/{Uri.EscapeDataString(mediaId)}");
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    public async Task<List<AdminIdentity>> AccountsAsync()=>await _http.GetFromJsonAsync<List<AdminIdentity>>("api/accounts",_json)??new();
    public async Task<List<AuditEntry>> AuditAsync()=>await _http.GetFromJsonAsync<List<AuditEntry>>("api/audit",_json)??new();
    public async Task<List<ManagedDevice>> DevicesAsync()=>await _http.GetFromJsonAsync<List<ManagedDevice>>("api/devices",_json)??new();
    public Task<AdminIdentity> AccountAsync(AccountChange change)=>MutateAsync<AdminIdentity>(HttpMethod.Put,"api/accounts",change);
    private async Task<T> MutateAsync<T>(HttpMethod method,string path,object value)
    {
        using var request=new HttpRequestMessage(method,path){Content=JsonContent.Create(value,options:_json)};
        using var response=await _http.SendAsync(request);var body=await response.Content.ReadAsStringAsync();
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException(ApiError.Message(body));
        return JsonSerializer.Deserialize<T>(body,_json)!;
    }
    public sealed record ManagedDevice(string Id,string ComputerName,string DisplayName,bool Enabled,DateTimeOffset? LastSeenUtc,string ServiceVersion);
    public void Dispose() => _http.Dispose();
}
