using AutoShutdown.Shared;
using Microsoft.AspNetCore.Http;

namespace AutoShutdown.Admin.Infrastructure;

public sealed class NoConnectedClientException(string message) : InvalidOperationException(message) { }

public static class AdminActionEndpoint
{
    public static async Task<IResult> HandleAsync(Func<Task<DeliveryResult>> dispatch, Action<Exception> log)
    {
        try { return Results.Ok(await dispatch()); }
        catch (NoConnectedClientException ex) { return Results.Text(ex.Message, statusCode: 409); }
        catch (UnauthorizedAccessException ex) { return Results.Text(ex.Message, statusCode: 403); }
        catch (ArgumentException ex) { return Results.Text(ex.Message, statusCode: 400); }
        catch (Exception ex)
        {
            log(ex);
            return Results.Text("Command failed: " + ex.Message, statusCode: 500);
        }
    }
}
