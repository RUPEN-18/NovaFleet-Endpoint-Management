using AutoShutdown.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Net;
namespace AutoShutdown.Admin.Infrastructure;
internal static partial class AdminSessionEndpoints
{
    internal static void MapManagement(WebApplication app,AdminSecurityStore security,InstallStore devices)
    {
        Func<HttpContext,Task<IResult>> accounts=async ctx=>Results.Ok(await security.AccountsAsync(Actor(ctx)));
        app.MapGet("/api/accounts",(Delegate)accounts);
        app.MapPut("/api/accounts",async Task<IResult>(AccountChange change,HttpContext ctx)=>{
            try{
                if(!Actor(ctx).IsSuper)return Results.Json(new{error="Super Admin permission required."},statusCode:403);
                var known=await devices.DevicesAsync();
                if(change.DeviceIds is null || change.DeviceIds.Any(id=>!known.Any(device=>device.Id==id)))
                    return Results.BadRequest("Select registered client PCs for this account.");
                return Results.Ok(await security.AccountAsync(Actor(ctx),change));}
            catch(Exception e)when(e is ArgumentException or InvalidOperationException or UnauthorizedAccessException){return Results.BadRequest(e.Message);}
        });
        Func<HttpContext,Task<IResult>> audit=async ctx=>Results.Ok(await security.AuditAsync(Actor(ctx)));
        app.MapGet("/api/audit",(Delegate)audit);
        Func<HttpContext,Task<IResult>> registeredDevices=async ctx=>Results.Ok((await devices.DevicesAsync()).Where(x=>Actor(ctx).CanDevice(x.Id))
            .Select(x=>new {x.Id,x.ComputerName,x.DisplayName,x.Enabled,x.LastSeenUtc,x.ServiceVersion}));
        app.MapGet("/api/devices",(Delegate)registeredDevices);
    }
}
