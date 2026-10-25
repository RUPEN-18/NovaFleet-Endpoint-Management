using AutoShutdown.Shared;
using Microsoft.AspNetCore.Http;

namespace AutoShutdown.Admin.Infrastructure;

public static class InstallAuthentication
{
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next, InstallStore store,
        string adminToken, int adminPort = DeploymentNetwork.AdminPort)
    {
        if (context.Request.Path.StartsWithSegments("/admin"))
        {
            // Administration requires both a loopback peer and the local-only listener.
            if (!DeploymentNetwork.CanUseAdminEndpoint(context.Connection.LocalIpAddress,
                context.Connection.LocalPort, context.Connection.RemoteIpAddress, adminPort))
            { context.Response.StatusCode = 403; return; }
            if (!InstallStore.SecretEquals(context.Request.Headers["X-Nova-Admin-Token"].ToString(), adminToken))
            { context.Response.StatusCode = 401; return; }
        }
        else
        {
            var id = await store.AuthenticateAsync(context.Request.Headers["X-Nova-Device-Id"].ToString(),
                context.Request.Headers["X-Nova-Device-Token"].ToString());
            if (id is null) { context.Response.StatusCode = 401; return; }
            context.Items["DeviceId"] = id;
        }
        await next(context);
    }
}
