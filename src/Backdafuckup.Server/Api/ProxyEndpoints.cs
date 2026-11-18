using System.Net.WebSockets;
using Backdafuckup.Contracts;
using Backdafuckup.Server.Proxies;

namespace Backdafuckup.Server.Api;

/// <summary>Backup proxies: the console's management, and the agent's enrollment, control connection and tunnels.</summary>
public static class ProxyEndpoints
{
    public static void MapProxyEndpoints(this IEndpointRouteBuilder app)
    {
        var proxies = app.MapGroup("/api/proxies");
        proxies.MapGet("/", (ProxyAgents agents, CancellationToken ct) => Run(async () => Results.Ok(await agents.ListAsync(ct))));
        proxies.MapPost("/", (CreateProxyDto request, ProxyAgents agents, CancellationToken ct) => Run(async () => Results.Ok(await agents.CreateAsync(request.Name, ct))))
            .WithDescription("Creates a proxy and its one-time install token (24 hours); the answer's command enrolls the agent on the proxy machine.");
        proxies.MapPost("/{id:guid}/renew", (Guid id, ProxyAgents agents, CancellationToken ct) => Run(async () => Results.Ok(await agents.RenewInstallAsync(id, ct))))
            .WithDescription("A new install token; the proxy's current key stops working until it enrolls again.");
        proxies.MapDelete("/{id:guid}", (Guid id, ProxyAgents agents, CancellationToken ct) => Run(async () =>
        {
            await agents.DeleteAsync(id, ct);
            return Results.NoContent();
        }));

        // The agent's side: like the other agents' interfaces these ignore the user sign-in; the agent proves itself.
        var accept = new WebSocketAcceptContext { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(60) };
        app.MapPost("/" + Proxy.ProxyAgentProtocol.EnrollPath, (EnrollProxyRequestDto request, ProxyAgents agents, HttpContext http, CancellationToken ct) =>
            Run(async () => Results.Ok(await agents.EnrollAsync(request, Remote(http), ct)))).AllowAnonymous().ExcludeFromDescription();

        app.MapGet("/api/agent/proxies/{id:guid}/control", async (Guid id, ProxyAgents agents, IHostApplicationLifetime lifetime, HttpContext http, CancellationToken ct) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                return Results.Problem("The control connection of a proxy is a WebSocket.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (await agents.AuthenticateAsync(id, http.Request.Headers.Authorization, ct) is not { } proxy)
            {
                return Results.Problem($"There is no proxy {id}, or its key does not match (enrolled again elsewhere? renew its install token).", statusCode: StatusCodes.Status401Unauthorized);
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync(accept);
            await Agents.AgentSocketShutdown.RunAsync(socket, lifetime, ct, token => agents.RunControlAsync(proxy, socket, Remote(http), token));
            return Results.Empty;
        }).AllowAnonymous().ExcludeFromDescription();

        app.MapGet("/api/agent/proxies/{id:guid}/tunnel/{stream:guid}", async (Guid id, Guid stream, ProxyAgents agents, HttpContext http, CancellationToken ct) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                return Results.Problem("A proxy's tunnel is a WebSocket.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (!agents.TryClaimTunnel(id, stream, http.Request.Headers.Authorization, out var pending))
            {
                return Results.Problem($"No tunnel {stream} is expected from proxy {id}, or its token does not match.", statusCode: StatusCodes.Status401Unauthorized);
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync(accept);
            var tunnel = new TunnelStream(WebSocketStream.Create(socket, WebSocketMessageType.Binary, ownsWebSocket: false));
            if (!pending.Socket.TrySetResult(tunnel))
            {
                await tunnel.DisposeAsync();
                return Results.Empty;
            }

            try
            {
                await tunnel.Closed.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // The request ended (server stopping, or the proxy closed it).
            }

            return Results.Empty;
        }).AllowAnonymous().ExcludeFromDescription();
    }

    private static string Remote(HttpContext http)
    {
        var address = http.Connection.RemoteIpAddress is { IsIPv4MappedToIPv6: true } mapped ? mapped.MapToIPv4() : http.Connection.RemoteIpAddress;
        return $"{address}:{http.Connection.RemotePort}";
    }

    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ProxyRequestException ex)
        {
            return Results.Problem(ex.Message, statusCode: ex.StatusCode);
        }
    }
}
