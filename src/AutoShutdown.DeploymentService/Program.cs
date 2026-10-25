using AutoShutdown.DeploymentService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Security.Principal;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Nova Deployment Service requires Windows.");
    return 1;
}

if (!WindowsServiceHelpers.IsWindowsService())
{
    Console.Error.WriteLine("Use scripts/Install-DeploymentService.ps1 from an administrator PowerShell window, then start the Windows service.");
    return 2;
}

using var identity = WindowsIdentity.GetCurrent();
if (identity.User?.Value != "S-1-5-18")
{
    Console.Error.WriteLine("This service must run under the LocalSystem account configured by the installer.");
    return 3;
}

try
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });
    builder.Services.AddWindowsService(options => options.ServiceName = ServiceConstants.Name);
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));
    builder.Services.AddSingleton<ServiceJournal>();
    builder.Services.AddSingleton<DeviceSession>();
    builder.Services.AddHostedService<DeploymentWorker>();
    builder.Services.AddHostedService<InstallWorker>();
    builder.Services.AddHostedService<DeviceHeartbeatWorker>();
    using var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
