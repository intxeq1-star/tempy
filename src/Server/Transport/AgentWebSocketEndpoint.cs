using System.Net.WebSockets;

namespace LanManagement.Server.Transport;

public static class AgentWebSocketEndpoint
{
    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            context.Response.Headers["Upgrade"] = "websocket";
            await context.Response.WriteAsJsonAsync(new { error = "WebSocket upgrade required." });
            return;
        }

        // Agent connections are machine clients, not browser origins. Authentication happens in the
        // first REGISTER_DEVICE frame, and the endpoint intentionally exposes no REST management API.
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var runner = context.RequestServices.GetRequiredService<IAgentSessionRunner>();
        await runner.RunAsync(socket, remoteIp, context.RequestAborted);
    }
}
