using System.Text.Json;

namespace AutoShutdown.Shared;

public static class ClientDeploymentStatus
{
    public static string ReadSummary()
    {
        var healthPath = Path.Combine(DeploymentActivity.DirectoryPath, "service-status.json");
        if (!File.Exists(healthPath)) return "INSTALLATIONS: Client setup has not supplied a service status yet.";
        try
        {
            if (new FileInfo(healthPath).Length > 16384) return "INSTALLATIONS: service status cannot be read.";
            using var health = JsonDocument.Parse(File.ReadAllText(healthPath));
            var current = health.RootElement;
            var alive = current.GetProperty("state").GetString() == "Running"
                && DateTimeOffset.UtcNow - current.GetProperty("heartbeatUtc").GetDateTimeOffset() < TimeSpan.FromSeconds(25);
            var connected = alive && current.GetProperty("deploymentEnabled").GetBoolean();
            var summary = !alive ? "INSTALLATIONS: service stopped or heartbeat unavailable."
                : connected ? "INSTALLATIONS: connected to Admin." : "INSTALLATIONS: waiting for Admin connection.";
            if (File.Exists(DeploymentActivity.FilePath) && new FileInfo(DeploymentActivity.FilePath).Length <= 16384)
            {
                using var activity = JsonDocument.Parse(File.ReadAllText(DeploymentActivity.FilePath));
                if (activity.RootElement.TryGetProperty("application", out var name) && !string.IsNullOrWhiteSpace(name.GetString()))
                    summary += $"\n{name.GetString()}: {activity.RootElement.GetProperty("stage").GetString()}. {activity.RootElement.GetProperty("details").GetString()}";
            }
            return summary;
        }
        catch (IOException) { return "INSTALLATIONS: refreshing service status..."; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException)
        { return "INSTALLATIONS: service status unavailable."; }
    }
}
