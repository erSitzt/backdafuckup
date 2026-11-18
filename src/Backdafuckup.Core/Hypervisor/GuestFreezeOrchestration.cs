using System.Globalization;
using Backdafuckup.Core.Operations;

namespace Backdafuckup.Core.Hypervisor;

/// <summary>
/// Guest processing around a platform snapshot, the same for every hypervisor: freeze the guest right before the snapshot,
/// thaw it right after (also when the snapshot failed), and report what the guest said.
/// </summary>
public static class GuestFreezeOrchestration
{
    /// <summary>Freezes the guest right before the snapshot; a guest that cannot be frozen gives a crash-consistent result instead.</summary>
    public static async Task<(IGuestFreeze? Freeze, GuestFreezeResult? Result)> FreezeAsync(IGuestQuiescer? quiescer, IOperationLog log, CancellationToken cancellationToken)
    {
        if (quiescer is null)
        {
            return (null, null);
        }

        try
        {
            return (await quiescer.FreezeAsync(log, cancellationToken).ConfigureAwait(false), null);
        }
        catch (GuestQuiesceException ex)
        {
            return (null, GuestFreezeResult.NotConsistent(ex.Message));
        }
    }

    /// <summary>Thaws the guest right after the snapshot (also when it failed) and returns what the guest reported.</summary>
    public static async Task<GuestFreezeResult?> ThawAsync(IGuestFreeze? freeze, GuestFreezeResult? result, bool snapshotTaken)
    {
        if (freeze is null)
        {
            return result;
        }

        await using (freeze.ConfigureAwait(false))
        {
            try
            {
                return await freeze.ThawAsync(snapshotTaken, CancellationToken.None).ConfigureAwait(false);
            }
            catch (GuestQuiesceException ex)
            {
                return GuestFreezeResult.NotConsistent(ex.Message);
            }
        }
    }

    /// <summary>The session log lines for the guest's result: application-consistent, or crash-consistent with the reason.</summary>
    public static void Report(IOperationLog log, GuestFreezeResult result, TimeSpan snapshotTime)
    {
        if (result.Sql is { } sql)
        {
            var text = $"Guest processing (SQL Server snapshot): {sql.Summary} VM snapshot took {Seconds(snapshotTime)}.";
            if (sql.AllDatabasesConsistent)
            {
                log.Info(text);
            }
            else
            {
                log.Warning(text);
            }

            foreach (var warning in sql.Warnings ?? [])
            {
                log.Warning($"Guest processing (SQL Server snapshot): {warning}");
            }

            return;
        }

        var writers = result.Writers.Count == 0 ? string.Empty
            : " Writers: " + string.Join(", ", result.Writers.Select(w => w.Failure is null ? $"{w.Name} ({w.State})" : $"{w.Name} ({w.State}: {w.Failure})")) + ".";
        if (result.ApplicationConsistent)
        {
            log.Info(
                $"Guest processing: application-consistent snapshot. Writes were held for {Seconds(result.HeldTime)} (VM snapshot {Seconds(snapshotTime)}, " +
                $"freeze {Seconds(result.FreezeTime)}).{writers}");
        }
        else
        {
            log.Warning($"Guest processing: the snapshot is only crash-consistent: {result.Reason} VM snapshot took {Seconds(snapshotTime)}.{writers}");
        }
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0 s", CultureInfo.InvariantCulture);
}
