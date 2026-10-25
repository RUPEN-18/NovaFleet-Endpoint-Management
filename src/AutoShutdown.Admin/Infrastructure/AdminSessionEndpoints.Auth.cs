using AutoShutdown.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Net;
namespace AutoShutdown.Admin.Infrastructure;
internal static partial class AdminSessionEndpoints
{
    internal static bool CheckBootstrap(HttpContext ctx,string secret,int port=5056) => ctx.Request.IsHttps && ctx.Connection.LocalPort==port && ctx.Connection.LocalIpAddress is not null && IPAddress.IsLoopback(ctx.Connection.LocalIpAddress) && ctx.Connection.RemoteIpAddress is not null && IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress) && InstallStore.SecretEquals(ctx.Request.Headers["X-Nova-Admin-Token"].ToString(),secret);
    internal static string Token(HttpContext ctx)=>ctx.Request.Headers.Authorization.ToString() is var h && h.StartsWith("Bearer ",StringComparison.Ordinal)?h[7..]:"";
    internal static AdminIdentity Actor(HttpContext ctx)=>(AdminIdentity)ctx.Items["AdminIdentity"]!;
    internal static async Task<bool> Authorize(HttpContext ctx,AdminSecurityStore security,AdminPermission permission)
    {
        var actor=await security.ResolveAsync(Token(ctx));
        if(actor is null){ctx.Response.StatusCode=401;await ctx.Response.WriteAsync("Your session expired. Sign in again.");return false;}
        if(!actor.Can(permission)){await security.AuditAsync(actor,"ACCESS_DENIED","Denied",ctx.Request.Path.ToString());ctx.Response.StatusCode=403;await ctx.Response.WriteAsync("Your account does not have permission for this operation.");return false;}
        ctx.Items["AdminIdentity"]=actor;return true;
    }
    internal static AdminPermission InstallPermission(PathString path,string method)=>path.Value?.TrimEnd('/').ToLowerInvariant() switch
    {
        "/admin/overview" or "/admin/packages"=>AdminPermission.None,
        "/admin/jobs" or "/admin/batches" or "/admin/retry"=>AdminPermission.Install,
        _ when method.Equals("GET",StringComparison.OrdinalIgnoreCase) && (path.Value?.TrimEnd('/').ToLowerInvariant().StartsWith("/admin/batches/",StringComparison.Ordinal) == true)=>AdminPermission.None,
        _=>AdminPermission.Manage
    };
    internal static AdminPermission ControlPermission(PathString path,string method)=>path.Value?.TrimEnd('/').ToLowerInvariant() switch
    {
        "/api/clients" or "/api/devices" or "/api/settings" or "/api/automation" or "/api/groups" or "/api/schedules" or "/health" when method.Equals("GET",StringComparison.OrdinalIgnoreCase)=>AdminPermission.None,
        "/api/accounts" or "/api/devices" or "/api/settings" or "/api/groups" or "/api/schedules" or "/api/logs" or "/api/deployment-tests"=>AdminPermission.Manage,
        "/api/automation"=>AdminPermission.Automation,
        "/api/audit"=>AdminPermission.ReadLogs,
        "/api/actions" or "/api/media"=>AdminPermission.None, // Each handler checks its specific operation permission.
        _ when path.StartsWithSegments("/media") && method.Equals("GET",StringComparison.OrdinalIgnoreCase)=>AdminPermission.None,
        _=>AdminPermission.Manage
    };
    internal static void MapAuthentication(WebApplication app,AdminSecurityStore security)
    {
        app.MapGet("/auth/status",()=>Results.Ok(new {needsSetup=security.NeedsSetup}));
        app.MapPost("/auth/setup",async Task<IResult>(SetupRequest request)=>{
            try{return Results.Ok(await security.SetupAsync(request));}
            catch(Exception e)when(e is ArgumentException or InvalidOperationException){return Results.BadRequest(e.Message);}
        });
        app.MapPost("/auth/login",async Task<IResult>(LoginRequest request)=>{
            try{return Results.Ok(await security.LoginAsync(request));}catch(UnauthorizedAccessException e){return Results.Json(new {error=e.Message},statusCode:401);}
        });
        app.MapPost("/auth/recovery",async Task<IResult>(RecoveryRequest request)=>{
            try{await security.RecoverAsync(request);return Results.Ok();}catch(Exception e)when(e is ArgumentException or UnauthorizedAccessException){return Results.BadRequest(e.Message);}
        });
        Func<HttpContext,Task<IResult>> logout=async ctx=>{await security.LogoutAsync(Token(ctx));return Results.Ok();};
        app.MapPost("/auth/logout",(Delegate)logout);
    }
}
