using System.Net.WebSockets;

namespace Backdafuckup.Server.Clusters;

/// <summary>
/// <c>/api/agent/clusters/{id}/*</c>: the WebSockets of a cluster's agent (<see cref="ClusterAgents"/>). Like the guest
/// agents' interface it ignores the user sign-in; the agent authenticates with the cluster's connect token.
/// </summary>
public static class ClusterAgentEndpoints
{
    private static readonly WebSocketAcceptContext Accept = new() { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(60) };

    public static void MapClusterAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var clusters = app.MapGroup("/api/agent/clusters").AllowAnonymous().ExcludeFromDescription();

        clusters.MapGet("/{id:guid}/control", async (Guid id, ClusterAgents agents, IHostApplicationLifetime lifetime, HttpContext http, CancellationToken ct) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                return Results.Problem("The control connection of a cluster agent is a WebSocket.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (await agents.AuthenticateAsync(id, http.Request.Headers.Authorization, ct) is not { } connection)
            {
                return NotKnown(id);
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync(Accept);
            await Agents.AgentSocketShutdown.RunAsync(socket, lifetime, ct, token => agents.RunControlAsync(connection, socket, $"{http.Connection.RemoteIpAddress}:{http.Connection.RemotePort}", token));
            return Results.Empty;
        });

        clusters.MapGet("/{id:guid}/tunnel/{stream:guid}", async (Guid id, Guid stream, ClusterAgents agents, IHostApplicationLifetime lifetime, HttpContext http, CancellationToken ct) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                return Results.Problem("A stream of a cluster agent is a WebSocket.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (await agents.AuthenticateAsync(id, http.Request.Headers.Authorization, ct) is null)
            {
                return NotKnown(id);
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync(Accept);
            await Agents.AgentSocketShutdown.RunAsync(socket, lifetime, ct, token => agents.RunTunnelAsync(id, stream, socket, token));
            return Results.Empty;
        });
    }

    private static IResult NotKnown(Guid id) =>
        Results.Problem($"The cluster {id} is not reached through an agent, or the connect token does not match (renewed? apply the current setup manifest).",
            statusCode: StatusCodes.Status401Unauthorized);
}
