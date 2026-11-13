using System.Reflection;
using System.Runtime.InteropServices;

namespace Backdafuckup.Mover;

/// <summary>The mover of backdafuckup: runs in a pod of the cluster, next to one volume (see <see cref="MoverOptions"/> for its settings).</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // What the server execs in a node agent's pod: one request on standard input, its answer (JSON) on standard output.
        if (args is ["node-request", ..])
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            return await NodeRequest.RunAsync(
                Console.In, Console.Out, Environment.GetEnvironmentVariable("BDFU_NODE_SOCKET") is { Length: > 0 } socket ? socket : NodeAgentOptions.DefaultSocket, timeout.Token)
                .ConfigureAwait(false);
        }

        var version = typeof(MoverRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        Console.WriteLine($"backdafuckup mover {version}");

        // Kubernetes stops a pod with SIGTERM: end the transfer instead of being killed in the middle of a write.
        using var stop = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        try
        {
            // The cluster agent: the same image, as a Deployment that keeps the tunnel to the server open.
            if (string.Equals(Environment.GetEnvironmentVariable("BDFU_MOVER_MODE"), "agent", StringComparison.OrdinalIgnoreCase))
            {
                var agent = ClusterAgentOptions.FromEnvironment(Environment.GetEnvironmentVariable);
                await ClusterAgent.Create(agent, Console.Out).RunAsync(stop.Token).ConfigureAwait(false);
                return 0;
            }

            // The node agent: the same image, as a DaemonSet that reads pod volumes from the kubelet's directory.
            if (string.Equals(Environment.GetEnvironmentVariable("BDFU_MOVER_MODE"), "node", StringComparison.OrdinalIgnoreCase))
            {
                await new NodeAgent(NodeAgentOptions.FromEnvironment(Environment.GetEnvironmentVariable), Console.Out).RunAsync(stop.Token).ConfigureAwait(false);
                return 0;
            }

            var options = MoverOptions.FromEnvironment(Environment.GetEnvironmentVariable);
            return await MoverRunner.RunAsync(options, handler: null, Console.Out, stop.Token).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }
}
