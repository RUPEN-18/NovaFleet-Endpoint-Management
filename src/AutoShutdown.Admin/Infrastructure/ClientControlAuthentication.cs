using Microsoft.AspNetCore.Http;

namespace AutoShutdown.Admin.Infrastructure;

public static class ClientControlAuthentication
{
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next, InstallStore? devices)
    {
        var id = context.Request.Headers["X-Nova-Device-Id"].FirstOrDefault() ?? context.Request.Query["deviceId"].FirstOrDefault() ?? "";
        var credential = context.Request.Headers["X-AutoShutdown-Key"].FirstOrDefault() ?? context.Request.Query["key"].FirstOrDefault() ?? "";
        var computer = devices is null ? null : await devices.AuthenticateComputerAsync(id, credential);
        if (computer is null) { context.Response.StatusCode = 401; return; }
        var path = context.Request.Path;
        var allowed = path.StartsWithSegments("/hub/client") || context.Request.Method == "GET"
            && (path == "/api/settings" || path == "/health" || path.StartsWithSegments("/media"));
        if (!allowed) { context.Response.StatusCode = 403; return; }
        if (path.StartsWithSegments("/hub/client") && !string.Equals(context.Request.Query["client"].ToString(), computer, StringComparison.OrdinalIgnoreCase))
        { context.Response.StatusCode = 403; return; }
        context.Items["DeviceId"] = id;
        context.Items["ComputerName"] = computer;
        await next(context);
    }
}
