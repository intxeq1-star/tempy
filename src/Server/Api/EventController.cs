using LanManagement.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LanManagement.Server.Api;

[Route("api/events")]
[Authorize(Roles = "Viewer,Operator,Administrator")]
public sealed class EventController(IDashboardEventBus eventBus) : ControllerBase
{
    [HttpGet]
    public async Task Stream(CancellationToken cancellationToken)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        await using var subscription = eventBus.Subscribe();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var read = subscription.Reader.ReadAsync(readCancellation.Token).AsTask();
                var keepAlive = Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
                var completed = await Task.WhenAny(read, keepAlive);
                if (completed == read)
                {
                    var payload = await read;
                    await Response.WriteAsync($"event: dashboard\ndata: {payload}\n\n", cancellationToken);
                }
                else
                {
                    readCancellation.Cancel();
                    try { await read; } catch (OperationCanceledException) { }
                    await Response.WriteAsync("event: keepalive\ndata: {}\n\n", cancellationToken);
                }
                await Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Browser disconnected.
        }
    }
}
