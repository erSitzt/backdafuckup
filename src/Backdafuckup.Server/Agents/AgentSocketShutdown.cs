using System.Net.WebSockets;

namespace Backdafuckup.Server.Agents;

/// <summary>
/// Ends an agent's WebSocket when the server stops: first a close frame "going away" (the agent reconnects to the next
/// start), and if the agent has not let go a few seconds later, the connection is cancelled. Without this the host waits its
/// whole shutdown timeout (30 s) for these connections, and Windows reports the service as terminated unexpectedly.
/// </summary>
internal static class AgentSocketShutdown
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    /// <summary>Runs <paramref name="run"/> with a token that is also cancelled shortly after the server begins to stop.</summary>
    public static async Task RunAsync(WebSocket socket, IHostApplicationLifetime lifetime, CancellationToken requestAborted, Func<CancellationToken, Task> run)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        using var registration = lifetime.ApplicationStopping.Register(() => _ = GoingAwayAsync(socket, linked));
        await run(linked.Token).ConfigureAwait(false);
    }

    private static async Task GoingAwayAsync(WebSocket socket, CancellationTokenSource linked)
    {
        try
        {
            using var timeout = new CancellationTokenSource(Grace);
            await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "the backup server is shutting down", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Closed, aborted or busy already: the cancellation below ends it.
        }

        try
        {
            linked.CancelAfter(Grace);
        }
        catch (ObjectDisposedException)
        {
            // The connection ended meanwhile.
        }
    }
}
