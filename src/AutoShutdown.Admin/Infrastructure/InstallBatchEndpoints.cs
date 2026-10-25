using AutoShutdown.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AutoShutdown.Admin.Infrastructure;

internal static class InstallBatchEndpoints
{
    internal static void Map(WebApplication app, InstallStore store, AdminSecurityStore security)
    {
        app.MapPost("/admin/batches", async Task<IResult> (InstallBatchRequest request, HttpContext context) =>
        {
            try
            {
                var actor = AdminSessionEndpoints.Actor(context);
                var operation = await store.CreateBatchAsync(request, actor);
                if (operation.Created) await AuditBatchAsync(operation.Result, actor, security, "INSTALL_QUEUED");
                return Results.Ok(operation.Result);
            }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.BadRequest(ex.Message); }
        });
        app.MapPost("/admin/retry", async Task<IResult> (InstallRetryRequest request, HttpContext context) =>
        {
            try
            {
                var actor = AdminSessionEndpoints.Actor(context);
                var operation = await store.RetryFailedAsync(request, actor);
                if (operation.Created) await AuditBatchAsync(operation.Result, actor, security, "INSTALL_RETRY_QUEUED");
                return Results.Ok(operation.Result);
            }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.BadRequest(ex.Message); }
        });
        app.MapGet("/admin/batches/{id}", async Task<IResult> (string id, HttpContext context) =>
        {
            try { return Results.Ok(await store.GetBatchAsync(id, AdminSessionEndpoints.Actor(context))); }
            catch (ArgumentException ex) { return Results.NotFound(new { error = ex.Message }); }
        });
    }

    private static async Task AuditBatchAsync(InstallBatchResult result, AdminIdentity actor, AdminSecurityStore security, string action)
    {
        foreach (var item in result.Items)
            await security.AuditAsync(actor, item.Job is null ? "INSTALL_NOT_QUEUED" : action, item.Job is null ? "Rejected" : "Queued",
                $"Batch {result.Id}; {item.Details}", deviceId: item.DeviceId, computer: item.ComputerName,
                software: item.Job?.Package.Name ?? "", version: item.Job?.Package.InstalledDisplayVersion ?? "");
    }
}
