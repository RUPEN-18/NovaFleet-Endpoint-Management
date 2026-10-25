using AutoShutdown.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Net;
using Microsoft.Extensions.Logging;

namespace AutoShutdown.Admin.Infrastructure;

public sealed class AdminServer : IAsyncDisposable
{
    private WebApplication? _app;
    public ServerOptions Options { get; private set; } = new();
    public DateTimeOffset StartedAt { get; private set; }

    public async Task StartAsync(DeploymentBootstrap? deployment = null, InstallStore? devices = null, AdminSecurityStore? security = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = Array.Empty<string>()
        });

        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        Options = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();
        if(deployment is null || devices is null || security is null)throw new InvalidOperationException("Admin TLS and identity stores are required.");
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback,5055);
            options.Listen(IPAddress.Loopback,5059,endpoint=>endpoint.UseHttps(deployment.Certificate));
            if(deployment.LanAddress is not null)options.Listen(DeploymentNetwork.ParseLanAddress(deployment.LanAddress),5058,endpoint=>endpoint.UseHttps(deployment.Certificate));
        });
        Options.ClientBaseUrl="https://127.0.0.1:5059/";
        builder.Services.AddSingleton(security);
        builder.Services.AddSingleton(devices);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 12 * 1024 * 1024);
        builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton<StateStore>();
        builder.Services.AddSingleton<ClientRegistry>();
        builder.Services.AddSingleton<ActionDispatcher>();
        builder.Services.AddSingleton(sp => new DeploymentTestStore(sp.GetRequiredService<StateStore>().RootPath));
        builder.Services.AddSingleton<DeploymentTestDispatcher>();
        builder.Services.AddHostedService<ScheduleWorker>();

        var app = builder.Build();
        _app = app;
        StartedAt = DateTimeOffset.Now;

        app.Use(async (ctx, next) =>
        {
            if (ctx.Connection.LocalPort == 5058)
            {
                await ClientControlAuthentication.InvokeAsync(ctx, _ => next(), devices);
                return;
            }
            if(ctx.Connection.RemoteIpAddress is null || !IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress)) {ctx.Response.StatusCode=403;return;}
            if(ctx.Connection.LocalPort==5059)
            {
                if(!await AdminSessionEndpoints.Authorize(ctx,security,AdminSessionEndpoints.ControlPermission(ctx.Request.Path,ctx.Request.Method)))return;
                await next();
                if(ctx.Request.Method!="GET")await security.AuditAsync(AdminSessionEndpoints.Actor(ctx),ctx.Request.Path.ToString(),ctx.Response.StatusCode.ToString());
                return;
            }
            // Client-only localhost compatibility: an API key never grants administrative access.
            var allowed=ctx.Request.Path.StartsWithSegments("/hub/client") || ctx.Request.Method=="GET" &&
                (ctx.Request.Path=="/api/settings" || ctx.Request.Path=="/health" || ctx.Request.Path.StartsWithSegments("/media"));
            var supplied=ctx.Request.Headers["X-AutoShutdown-Key"].FirstOrDefault()??ctx.Request.Query["key"].FirstOrDefault()??"";
            if(!allowed || string.IsNullOrWhiteSpace(supplied) || string.IsNullOrWhiteSpace(Options.ApiKey) || !SecureEquals(supplied,Options.ApiKey)){ctx.Response.StatusCode=403;return;}
            ctx.Items["DeviceId"]="";ctx.Items["ComputerName"]=ctx.Request.Query["client"].ToString();
            await next();
        });

        app.MapGet("/health", (ClientRegistry registry) => Results.Ok(new ServerHealth
        {
            Online = true,
            StartedAt = StartedAt,
            ConnectedClients = registry.ConnectedCount
        }));

        AdminSessionEndpoints.MapManagement(app,security,devices);
        // Existing GUI clients continue receiving the global settings contract.
        app.MapGet("/api/settings", (StateStore store) => store.GetSettingsAsync());
        app.MapPut("/api/settings", async (GlobalSettings settings, HttpContext ctx, StateStore store, IHubContext<ClientHub> hub) =>
        {
            if(!AdminSessionEndpoints.Actor(ctx).IsSuper)return Results.Json(new{error="Super Admin permission required."},statusCode:403);
            AdminSecurityStore.ValidatePolicy(settings);
            if(settings.MonitoringEnabled && ctx.Request.Headers["X-LT-Confirm-Automation"]!="true")return Results.BadRequest("Confirm enabling automatic shutdown on the Admin PC.");
            var previous=await store.GetSettingsAsync();
            await store.SetSettingsAsync(settings);
            await security.AuditAsync(AdminSessionEndpoints.Actor(ctx),"ADMIN_PC_POLICY","Applied",previous:System.Text.Json.JsonSerializer.Serialize(previous),current:System.Text.Json.JsonSerializer.Serialize(settings));
            await store.AppendLogAsync(new LogEntry { Source = "ADMIN", Event = "SETTINGS_UPDATED", Target = "ALL CLIENTS", Details = "Global policy updated" });
            await hub.Clients.All.SendAsync("SettingsChanged", settings);
            return Results.Ok(settings);
        });

        app.MapGet("/api/clients", async (HttpContext ctx, ClientRegistry registry, StateStore store) =>
        {
            var groups = await store.GetGroupsAsync();
            var known = await store.GetKnownClientsAsync();
            var actor=AdminSessionEndpoints.Actor(ctx);var enrolled=await devices.DevicesAsync();
            return Results.Ok(registry.GetSnapshots(groups,known).Where(x=>actor.IsSuper || enrolled.Any(d=>d.Id==x.DeviceId&&actor.CanDevice(d.Id))));
        });

        app.MapGet("/api/groups", async (HttpContext ctx, StateStore store) => {
            var groups=await store.GetGroupsAsync(); var actor=AdminSessionEndpoints.Actor(ctx);
            if(actor.IsSuper)return groups;
            var names=(await devices.DevicesAsync()).Where(device=>actor.CanDevice(device.Id)).Select(device=>device.ComputerName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return groups.Select(group=>group with{Members=group.Members.Where(names.Contains).ToList()}).Where(group=>group.Members.Count>0).ToList();
        });
        app.MapPut("/api/groups", async (List<GroupDefinition> groups, StateStore store) =>
        {
            await store.SetGroupsAsync(groups);
            await store.AppendLogAsync(new LogEntry { Source = "ADMIN", Event = "GROUPS_UPDATED", Target = "GROUPS", Details = $"{groups.Count} group(s) saved" });
            return Results.Ok(await store.GetGroupsAsync());
        });

        app.MapGet("/api/schedules", async (HttpContext ctx, StateStore store) => {
            var schedules=await store.GetSchedulesAsync();var actor=AdminSessionEndpoints.Actor(ctx);
            return actor.IsSuper?schedules:schedules.Where(schedule=>schedule.AdminId==actor.Id).ToList();
        });
        app.MapPut("/api/schedules", async (List<ScheduleDefinition> schedules, HttpContext ctx, StateStore store) =>
        {
            schedules=schedules.Select(x=>x with{AdminId=AdminSessionEndpoints.Actor(ctx).Id}).ToList();
            await store.SetSchedulesAsync(schedules);
            await store.AppendLogAsync(new LogEntry { Source = "ADMIN", Event = "SCHEDULES_UPDATED", Target = "SCHEDULES", Details = $"{schedules.Count} schedule(s) saved" });
            return Results.Ok(await store.GetSchedulesAsync());
        });

        app.MapGet("/api/logs", async (int? take, StateStore store) => Results.Ok(await store.GetLogsAsync(take ?? 1000)));

        app.MapGet("/api/deployment-tests", async (DeploymentTestStore store) => Results.Ok(await store.GetAsync()));
        app.MapPost("/api/deployment-tests", async (DeploymentTestRequest request, DeploymentTestDispatcher dispatcher) =>
        {
            try { return Results.Ok(await dispatcher.SendAsync(request)); }
            catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
            catch (InvalidOperationException ex) { return Results.Conflict(ex.Message); }
        });

        app.MapPost("/api/actions", (AdminActionRequest request, HttpContext ctx, ActionDispatcher dispatcher, ILogger<ActionDispatcher> logger) =>
            AdminActionEndpoint.HandleAsync(() => dispatcher.DispatchAsync(request, "ADMIN", AdminSessionEndpoints.Actor(ctx)), ex => logger.LogError(ex, "Command dispatch failed.")));

        app.MapPost("/api/media", async (HttpRequest request, HttpContext ctx, StateStore store) =>
        {
            var actor=AdminSessionEndpoints.Actor(ctx);
            if(!actor.Can(AdminPermission.Control)&&!actor.Can(AdminPermission.Automation))return Results.Json(new{error="Media permission is required."},statusCode:403);
            if (!request.HasFormContentType) return Results.BadRequest("multipart/form-data is required.");
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Results.BadRequest("Image file is required.");
            if (file.Length > 10 * 1024 * 1024) return Results.BadRequest("Image exceeds the 10 MB limit.");
            await using var stream = file.OpenReadStream();
            var asset = await store.SaveMediaAsync(stream, file.FileName, file.ContentType);
            await store.AppendLogAsync(new LogEntry { Source = "ADMIN", Event = "MEDIA_UPLOADED", Target = asset.Id, Details = $"{file.FileName} ({file.Length} bytes)" });
            return Results.Ok(asset);
        });

        app.MapGet("/media/{id}", async (string id, StateStore store) =>
        {
            var media = await store.GetMediaAsync(id);
            return media is null ? Results.NotFound() : Results.File(media.Value.Path, media.Value.Asset.ContentType, enableRangeProcessing: true);
        });

        app.MapHub<ClientHub>("/hub/client");

        await app.StartAsync();
    }

    private static bool SecureEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync(TimeSpan.FromSeconds(3));
        await _app.DisposeAsync();
    }
}
