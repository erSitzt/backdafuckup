using Backdafuckup.Tape.Devices;

namespace Backdafuckup.TapeRecover;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        var recovery = new TapeRecovery(new LocalTapeHost(), Console.Out, Console.In, Environment.GetEnvironmentVariable, !Console.IsInputRedirected);
        try
        {
            return await recovery.RunAsync(args, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Stopped.");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException or System.Security.Cryptography.CryptographicException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
