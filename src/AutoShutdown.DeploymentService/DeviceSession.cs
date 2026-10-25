using AutoShutdown.Shared;
using Microsoft.Extensions.Hosting;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace AutoShutdown.DeploymentService;

public sealed class DeviceSession : IDisposable
{
    public HttpClient? Client { get; private set; }
    public DeviceConnection? Profile { get; private set; }
    public volatile bool Connected;
    private string? _configurationHash;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public void Reload()
    {
        var path = Path.Combine(ServiceConstants.DataPath, "connection.json");
        if (!File.Exists(path)) return;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 8192) throw new InvalidDataException("Service connection configuration is too large.");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (hash == _configurationHash) return;
        var profile = JsonSerializer.Deserialize<DeviceConnection>(bytes, _json)
            ?? throw new InvalidDataException("Pair the service in Admin.");
        DeploymentNetwork.ValidateConnection(profile, Environment.MachineName);
        var client = DeploymentTransport.Create(new Uri(profile.ServerUrl), profile.ServerCertificateSha256);
        client.DefaultRequestHeaders.Add("X-Nova-Device-Id", profile.DeviceId);
        client.DefaultRequestHeaders.Add("X-Nova-Device-Token", profile.DeviceToken);
        var previous = Client;
        Client = client; Profile = profile; _configurationHash = hash; Connected = false;
        previous?.Dispose();
    }
    public void Dispose() => Client?.Dispose();
}

public sealed class DeviceHeartbeatWorker(DeviceSession session) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var http = session.Client;
                if (http is not null)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(4));
                    using var response = await http.PostAsJsonAsync("device/heartbeat", new DeploymentHeartbeat(ServiceConstants.Version), deadline.Token);
                    session.Connected = response.IsSuccessStatusCode;
                }
            }
            catch { session.Connected = false; }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
