using AutoShutdown.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace AutoShutdown.Admin.Infrastructure;

public sealed class InstallServer : IAsyncDisposable
{
    private WebApplication? _app;
    public DeploymentBootstrap Bootstrap { get; } = new();
    private readonly InstallStore _store;
    public InstallStore Store => _store;
    private readonly StateStore _logs = new();
    public AdminSecurityStore Security { get; }
    public InstallServer()
    {
        ProtectedAdminFiles.ProtectTree(Bootstrap.Root);
        _store = new InstallStore(Bootstrap.Root);
        Security = new AdminSecurityStore(Bootstrap.Root);
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = ApplicationPackagePolicy.MaximumBytes + 1024 * 1024;
            options.Listen(IPAddress.Loopback, 5056, endpoint => endpoint.UseHttps(Bootstrap.Certificate));
            if (Bootstrap.LanAddress is not null)
                options.Listen(DeploymentNetwork.ParseLanAddress(Bootstrap.LanAddress), DeploymentNetwork.DevicePort,
                    endpoint => endpoint.UseHttps(Bootstrap.Certificate));
        });
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = ApplicationPackagePolicy.MaximumBytes);
        var app = builder.Build();
        _app = app;
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            if (context.Request.Path.StartsWithSegments("/auth"))
            {
                if(!AdminSessionEndpoints.CheckBootstrap(context,Bootstrap.Secrets.AdminToken))
                {context.Response.StatusCode=403;return;}
                await next(context);return;
            }
            await InstallAuthentication.InvokeAsync(context, async ctx =>
            {
                if(ctx.Request.Path.StartsWithSegments("/admin") && !await AdminSessionEndpoints.Authorize(ctx,Security,AdminSessionEndpoints.InstallPermission(ctx.Request.Path,ctx.Request.Method)))return;
                await next(ctx);
                if(ctx.Request.Path.StartsWithSegments("/admin") && ctx.Request.Method!="GET")
                    await Security.AuditAsync(AdminSessionEndpoints.Actor(ctx),ctx.Request.Path.ToString(),ctx.Response.StatusCode.ToString());
            },_store,Bootstrap.Secrets.AdminToken);
        });
        AdminSessionEndpoints.MapAuthentication(app,Security);
        InstallBatchEndpoints.Map(app, _store, Security);
        app.MapGet("/admin/overview", async (HttpContext ctx) => {
            var actor=AdminSessionEndpoints.Actor(ctx);var all=await _store.OverviewAsync(actor);var counts=await _store.CountsAsync(actor);
            return all with {ActiveCount=counts.Active,SuccessfulCount=counts.Success,FailedCount=counts.Failed,Devices=all.Devices.Where(x=>actor.CanDevice(x.Id)).ToList(),Jobs=all.Jobs.Where(x=>actor.CanDevice(x.DeviceId)).ToList(),
                Package=all.Package is not null && ApplicationPackagePolicy.IsGeneral(all.Package)?all.Package:null};
        });
        app.MapGet("/admin/packages",()=>_store.PackagesAsync());
        app.MapGet("/admin/network", () => new DeploymentLanInfo(Bootstrap.ConfiguredLanAddress,
            Bootstrap.LanUrl, Bootstrap.CertificateHash));
        app.MapPost("/admin/network", async Task<IResult> (DeploymentLanSettings settings) =>
        {
            try
            {
                if ((await _store.OverviewAsync()).Jobs.Any(job => !SevenZipPolicy.IsTerminal(job.State)))
                    return Results.BadRequest("Finish the current installation jobs before changing the LAN connection.");
                await Bootstrap.SaveLanSettingsAsync(settings.Address);
                return Results.Ok(new { message = "LAN settings saved. Close and reopen Admin to apply them." });
            }
            catch (Exception ex) { return Results.BadRequest(ex.Message); }
        });
        app.MapPost("/admin/pair/remote", async Task<IResult> (PairRequest request, HttpContext ctx) =>
        {
            try
            {
                DeploymentNetwork.ValidateComputerName(request.ComputerName);
                if (string.Equals(request.ComputerName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest("Use PAIR THIS PC for the local service. Enter the second PC's exact computer name here.");
                if (Bootstrap.LanUrl is null) return Results.BadRequest("Save an assigned Admin LAN IPv4 address, then reopen Admin first.");
                var profile = await _store.PairAsync(request.ComputerName, Bootstrap.LanUrl, Bootstrap.CertificateHash);
                await Security.AuditAsync(AdminSessionEndpoints.Actor(ctx),"DEVICE_REGISTERED","Profile exported",deviceId:profile.DeviceId,computer:profile.ComputerName);
                await _logs.AppendLogAsync(new LogEntry { Event = "DEPLOYMENT_REMOTE_PROFILE_CREATED", Target = request.ComputerName,
                    Details = "A computer-bound LAN pairing profile was created; the service is not connected yet." });
                return Results.Ok(profile);
            }
            catch (Exception ex) { return Results.BadRequest(ex.Message); }
        });
        app.MapPost("/admin/pair", async Task<IResult> (PairRequest request, HttpContext ctx) =>
        {
            try
            {
                var profile = await _store.PairAsync(request.ComputerName, Bootstrap.Url, Bootstrap.CertificateHash);
                await Security.AuditAsync(AdminSessionEndpoints.Actor(ctx),"DEVICE_REGISTERED","Local pairing",deviceId:profile.DeviceId,computer:profile.ComputerName);
                await _logs.AppendLogAsync(new LogEntry { Event = "DEPLOYMENT_SERVICE_PAIRED", Target = request.ComputerName, Details = "A separate service credential was created." });
                return Results.Ok(profile);
            }
            catch (Exception ex) { return Results.BadRequest(ex.Message); }
        });
        app.MapPost("/admin/jobs", async Task<IResult> (InstallRequest request, HttpContext ctx) =>
        {
            try
            {
                var actor=AdminSessionEndpoints.Actor(ctx);
                if(!actor.CanDevice(request.DeviceId))return Results.Json(new {error="This device is not assigned to your account."},statusCode:403);
                var job = await _store.CreateAsync(request,actor);
                await Security.AuditAsync(actor,"INSTALL_QUEUED","Queued",deviceId:job.DeviceId,computer:job.ComputerName,software:job.Package.Name,version:job.Package.InstalledDisplayVersion);
                await _logs.AppendLogAsync(new LogEntry { Event = "DEPLOYMENT_QUEUED", Target = job.ComputerName, Details = $"{job.Package.Name}; job {job.Id}" });
                return Results.Ok(job);
            }
            catch (Exception ex) { return Results.BadRequest(ex.Message); }
        });
        app.MapPost("/admin/import/application", async Task<IResult> (HttpRequest request, HttpContext ctx) =>
        {
            try
            {
                if (!request.HasFormContentType) return Results.BadRequest("Select an EXE/MSI installer.");
                var form = await request.ReadFormAsync();
                var file = form.Files.GetFile("file");
                if (file is null || file.Length <= 0 || file.Length > ApplicationPackagePolicy.MaximumBytes)
                    return Results.BadRequest("Installer must be between 1 byte and 2 GiB.");
                var options = new ApplicationImportOptions(form["name"].ToString(), form["arguments"].ToString(),
                    form["displayName"].ToString(), form["version"].ToString(), form["sha256"].ToString(),
                    form["mode"].ToString() is "auto" ? ApplicationPackagePolicy.Auto : ApplicationPackagePolicy.Exact);
                await using var stream = file.OpenReadStream();
                var package = await _store.ImportApplicationAsync(stream, file.FileName, options);
                await Security.AuditAsync(AdminSessionEndpoints.Actor(ctx),"PACKAGE_APPROVED","Approved",package.Sha256,software:package.Name,version:package.InstalledDisplayVersion);
                await _logs.AppendLogAsync(new LogEntry { Event = "APPLICATION_IMPORTED", Target = package.Name,
                    Details = $"Admin-selected installer SHA-256: {package.Sha256}; verification: {package.VerificationMode}; expected version: {package.InstalledDisplayVersion}." });
                return Results.Ok(package);
            }
            catch (Exception ex) { return Results.BadRequest(ex.Message); }
        });
        app.MapPost("/device/heartbeat", async (HttpContext context) =>
        {
            var heartbeat = context.Request.HasJsonContentType()
                ? await context.Request.ReadFromJsonAsync<DeploymentHeartbeat>() : null;
            await _store.TouchAsync((string)context.Items["DeviceId"]!, heartbeat?.ServiceVersion ?? "0.2.0");
            return Results.Ok();
        });
        app.MapGet("/device/next", async (HttpContext context) =>
        {
            var job = await _store.NextAsync((string)context.Items["DeviceId"]!);
            return job is null ? (IResult)Results.NoContent() : Results.Ok(job);
        });
        app.MapGet("/device/package/{jobId}", async (string jobId, HttpContext context) =>
        {
            var job = await _store.OwnJobAsync((string)context.Items["DeviceId"]!, jobId);
            return job is null || SevenZipPolicy.IsTerminal(job.State) ? (IResult)Results.NotFound()
                : Results.File(_store.PackagePath(job.Package), "application/octet-stream", fileDownloadName: job.Package.FileName);
        });
        app.MapPost("/device/report", async Task<IResult> (InstallReport report, HttpContext context) =>
        {
            try
            {
                var result = await _store.ReportAsync((string)context.Items["DeviceId"]!, report);
                if (result.Changed)
                {
                    var actor = result.Job.AdminId.Length==0?null:new AdminIdentity(result.Job.AdminId,result.Job.AdminUsername,result.Job.AdminDisplayName,"Historical",AdminPermission.None,new());
                    await Security.AuditAsync(actor,"INSTALL_"+result.Job.State.ToString().ToUpperInvariant(),result.Job.State.ToString(),result.Job.Details,result.Job.DeviceId,result.Job.ComputerName,result.Job.Package.Name,result.Job.InstalledVersion);
                }
                if (result.Changed)
                    await _logs.AppendLogAsync(new LogEntry { Source = result.Job.ComputerName, Event = "DEPLOYMENT_" + result.Job.State.ToString().ToUpperInvariant(),
                        Target = result.Job.Id, Details = result.Job.Details });
                return Results.Ok(result.Job);
            }
            catch (Exception ex) { return Results.BadRequest(ex.Message); }
        });
        await app.StartAsync();
    }

    public sealed record PairRequest(string ComputerName);
    public async ValueTask DisposeAsync()
    {
        if (_app is not null) { using var stopTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3)); await _app.StopAsync(stopTimeout.Token); await _app.DisposeAsync(); }
        Bootstrap.Dispose();
    }
}
