using System.Diagnostics.Metrics;

namespace Backdafuckup.InstantRecovery;

/// <summary>
/// Counters of what the hosts read and write on instant recovery datastores. They belong to the backup server's meter
/// (same name), so its Prometheus endpoint serves them: <c>bdfu_instant_recovery_read_bytes_total{source}</c> (overlay:
/// what the VMs wrote; backup: chunks of the restore points; zero: regions the backup holds no data for) and
/// <c>bdfu_instant_recovery_write_bytes_total</c>.
/// </summary>
public static class InstantRecoveryTelemetry
{
    /// <summary>The backup server's meter name.</summary>
    public const string MeterName = "Backdafuckup";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Reads = Meter.CreateCounter<long>(
        "bdfu_instant_recovery_read_bytes_total", "By", "Bytes hosts read from instant recovery datastores, by source: overlay (written by the VMs), backup (restore points), zero.");

    private static readonly Counter<long> Writes = Meter.CreateCounter<long>(
        "bdfu_instant_recovery_write_bytes_total", "By", "Bytes hosts wrote to instant recovery datastores (into the overlay).");

    internal static void Read(string source, long bytes)
    {
        if (bytes > 0)
        {
            Reads.Add(bytes, new KeyValuePair<string, object?>("source", source));
        }
    }

    internal static void Written(long bytes)
    {
        if (bytes > 0)
        {
            Writes.Add(bytes);
        }
    }
}
