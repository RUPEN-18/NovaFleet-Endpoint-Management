namespace AutoShutdown.Admin.Infrastructure;

public sealed class ServerOptions
{
    public string Url { get; set; } = "http://127.0.0.1:5055";
    public string ClientBaseUrl { get; set; } = "http://127.0.0.1:5055";
    public string ApiKey { get; set; } = "";
}
