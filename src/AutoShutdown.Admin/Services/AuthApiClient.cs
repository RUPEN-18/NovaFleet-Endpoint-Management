using AutoShutdown.Admin.Infrastructure;
using AutoShutdown.Shared;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
namespace AutoShutdown.Admin.Services;
public sealed class AuthApiClient:IDisposable
{
    private readonly HttpClient _http;
    public AuthApiClient(DeploymentBootstrap bootstrap)
    {_http=DeploymentTransport.Create(new Uri(bootstrap.Url),bootstrap.CertificateHash);_http.DefaultRequestHeaders.Add("X-Nova-Admin-Token",bootstrap.Secrets.AdminToken);}
    public async Task<bool> NeedsSetupAsync()
    {using var doc=JsonDocument.Parse(await _http.GetStringAsync("auth/status"));return doc.RootElement.GetProperty("needsSetup").GetBoolean();}
    private async Task<LoginSession> SessionAsync(string path,object request)
    {using var response=await _http.PostAsJsonAsync(path,request);var body=await response.Content.ReadAsStringAsync();if(!response.IsSuccessStatusCode)throw new InvalidOperationException(ApiError.Message(body));return JsonSerializer.Deserialize<LoginSession>(body,new JsonSerializerOptions(JsonSerializerDefaults.Web))??throw new InvalidOperationException("No session returned.");}
    public Task<LoginSession> LoginAsync(LoginRequest request)=>SessionAsync("auth/login",request);
    public Task<LoginSession> SetupAsync(SetupRequest request)=>SessionAsync("auth/setup",request);
    public async Task RecoverAsync(RecoveryRequest request)
    {using var response=await _http.PostAsJsonAsync("auth/recovery",request);if(!response.IsSuccessStatusCode)throw new InvalidOperationException(ApiError.Message(await response.Content.ReadAsStringAsync()));}
    public async Task LogoutAsync(string token)
    {using var request=new HttpRequestMessage(HttpMethod.Post,"auth/logout");request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);using var response=await _http.SendAsync(request);response.EnsureSuccessStatusCode();}
    public void Dispose()=>_http.Dispose();
}
