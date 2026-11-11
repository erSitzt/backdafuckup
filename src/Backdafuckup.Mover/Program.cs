using System.Reflection;
using System.Runtime.InteropServices;

namespace Backdafuckup.Mover;

/// <summary>The mover of backdafuckup: runs in a pod of the cluster, next to one volume (see <see cref="MoverOptions"/> for its settings).</summary>
internal static class Program
{
    public static async Task<int> Main()
    {
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
