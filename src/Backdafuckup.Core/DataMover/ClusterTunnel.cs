using System.Net.WebSockets;

namespace Backdafuckup.Core.DataMover;

/// <summary>
/// The tunnel of a cluster's agent (the mover image in agent mode, a Deployment in <c>backdafuckup-system</c>) for
/// clusters the server cannot reach: the agent dials out and keeps a control WebSocket open; for every TCP connection
/// the server's client opens to the API server, the server asks for one over it, and the agent opens a WebSocket of its
/// own for that connection and joins it to a TCP connection to the API server. TLS runs from the server to the API
/// server through it, so the agent sees no request; the server authenticates with the ServiceAccount token the agent
/// sends. Both WebSockets authenticate with the cluster's connect token (<c>Authorization: Bearer</c>).
/// </summary>
public static class ClusterTunnel
{
    /// <summary>The name the API server's certificate carries inside the cluster (the Service <c>kubernetes</c> in <c>default</c>).</summary>
    public const string ApiServerName = "kubernetes.default.svc";

    public static string ControlPath(Guid connectionId) => $"api/agent/clusters/{connectionId:D}/control";

    public static string TunnelPath(Guid connectionId, Guid stream) => $"api/agent/clusters/{connectionId:D}/tunnel/{stream:D}";

    /// <summary>Joins a TCP connection and a WebSocket until either side ends; then both are closed.</summary>
    public static async Task PumpAsync(Stream stream, WebSocket socket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(socket);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var up = Task.Run(
            async () =>
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, endOfMessage: true, stop.Token).ConfigureAwait(false);
                }

                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, stop.Token).ConfigureAwait(false);
            },
            CancellationToken.None);
        var down = Task.Run(
            async () =>
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var result = await socket.ReceiveAsync(buffer.AsMemory(), stop.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, result.Count), stop.Token).ConfigureAwait(false);
                }
            },
            CancellationToken.None);

        // One direction ends (or fails): the connection is over in both.
        await Task.WhenAny(up, down).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(up, down).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException)
        {
            // Closed from the other side.
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException)
                {
                    socket.Abort();
                }
            }
        }
    }
}

/// <summary>A message of the control WebSocket (JSON text frames, camelCase).</summary>
public sealed record ClusterAgentMessage
{
    /// <summary>Agent → server: <c>hello</c> (first), <c>token</c> (a renewed ServiceAccount token), <c>failed</c> (a stream that could not be opened). Server → agent: <c>open</c>.</summary>
    public required string Type { get; init; }

    public Guid? Stream { get; init; }

    public string? Error { get; init; }

    public string? Version { get; init; }

    /// <summary>Where the agent reaches the API server (for messages).</summary>
    public string? ApiHost { get; init; }

    public int? ApiPort { get; init; }

    /// <summary>The name the API server's certificate carries (<see cref="ClusterTunnel.ApiServerName"/>).</summary>
    public string? TlsName { get; init; }

    /// <summary>The cluster CA (the ServiceAccount's <c>ca.crt</c>).</summary>
    public string? CaPem { get; init; }

    /// <summary>The agent's ServiceAccount token, with the roles of the setup manifest.</summary>
    public string? Token { get; init; }

    public string? Pod { get; init; }

    public string? Node { get; init; }
}

public static class ClusterAgentMessageTypes
{
    public const string Hello = "hello";
    public const string Token = "token";
    public const string Failed = "failed";
    public const string Open = "open";
}
