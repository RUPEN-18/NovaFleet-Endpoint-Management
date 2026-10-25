namespace AutoShutdown.DeploymentService;

public static class ServiceConstants
{
    public const string Name = "NovaDeploymentService";
    public const string Version = "0.5.0";
    public static string DataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "AutoShutdown.Nova.DeploymentService");
}
