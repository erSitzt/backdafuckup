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

        MoverOptions options;
        try
        {
            options = MoverOptions.FromEnvironment(Environment.GetEnvironmentVariable);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

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

        return await MoverRunner.RunAsync(options, handler: null, Console.Out, stop.Token).ConfigureAwait(false);
    }
}
