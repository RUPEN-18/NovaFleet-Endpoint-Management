namespace AutoShutdown.Client.Services;

public sealed class ClientOptions
{
    public string ServerUrl { get; set; } = "http://127.0.0.1:5055";
    public string ApiKey { get; set; } = "";
    public string CertificateSha256 { get; set; } = "";
    public string DeviceId { get; set; } = "";
}
