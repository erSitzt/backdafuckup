using System.Diagnostics.Metrics;
using Backdafuckup.InstantRecovery;
using Backdafuckup.Nfs;
using Backdafuckup.Server.InstantRecovery;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Telemetry;

/// <summary>
/// Metrics of instant recovery and its NFS server. The gauges read the live state the session runners keep (no database
/// query per scrape); what the hosts read and write is counted by the instant recovery library itself
/// (<see cref="InstantRecoveryTelemetry"/>).
/// </summary>
public sealed class InstantRecoveryMetrics
{
    private readonly Counter<long> _requests;
    private readonly Histogram<double> _requestSeconds;
    private readonly Counter<long> _migrations;

    public InstantRecoveryMetrics(
        IMeterFactory meterFactory, NfsServerHost nfs, InstantRecoveryControl control, AsyncChunkCache cache, IOptions<InstantRecoveryOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(nfs);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        var meter = meterFactory.Create(BackupMetrics.MeterName);

        _requests = meter.CreateCounter<long>("bdfu_nfs_requests_total", description: "NFS 3 calls the server answered, by procedure and status (ok, or the NFS error).");
        _requestSeconds = meter.CreateHistogram("bdfu_nfs_request_seconds", "s", "Time to answer an NFS 3 call, by procedure.", tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10] });
        _migrations = meter.CreateCounter<long>("bdfu_instant_recovery_migrations_total", description: "Migrations of instant recoveries to production storage, by result (succeeded, failed).");
        nfs.Called += Observe;

        meter.CreateObservableGauge("bdfu_nfs_connections", () => nfs.Connections, description: "Open TCP connections of the NFS server for instant recovery.");
        meter.CreateObservableGauge("bdfu_instant_recovery_sessions", () =>
            control.Live.GroupBy(l => l.State).Select(g => new Measurement<int>(g.Count(), new KeyValuePair<string, object?>("state", g.Key))),
            description: "Instant recoveries this server runs, by state (Starting, Running, Migrating, Stopping).");
        meter.CreateObservableGauge("bdfu_instant_recovery_age_seconds", () =>
            control.Live.Select(l => new Measurement<double>((time.GetUtcNow() - l.CreatedUtc).TotalSeconds, Tags(l))),
            "s", "How long each instant recovery runs; it is meant to be migrated or stopped soon.");
        meter.CreateObservableGauge("bdfu_instant_recovery_overlay_bytes", () =>
            control.Live.Where(l => l.FileSystem is not null).Select(l => new Measurement<long>(l.FileSystem!.OverlayBytes, Tags(l))),
            "By", "What the VMs of each instant recovery wrote into its overlay.");
        meter.CreateObservableGauge("bdfu_instant_recovery_overlay_limit_bytes", () =>
            control.Live.Where(l => l.OverlayLimitBytes is > 0).Select(l => new Measurement<long>(l.OverlayLimitBytes!.Value, Tags(l))),
            "By", "The write limit of an instant recovery, where one was set.");
        meter.CreateObservableGauge("bdfu_instant_recovery_last_io_timestamp_seconds", () =>
            control.Live.Where(l => l.FileSystem is not null).Select(l => new Measurement<double>(l.FileSystem!.LastIoUtc.ToUnixTimeMilliseconds() / 1000.0, Tags(l))),
            "s", "When the host last read or wrote a file of the instant recovery's datastore (or when it was served).");
        meter.CreateObservableGauge("bdfu_instant_recovery_migration_progress_ratio", () =>
            control.Live.Where(l => l.MigrationProgress is not null).Select(l => new Measurement<double>(l.MigrationProgress!.Value, Tags(l))),
            description: "Progress of a running migration to production storage (0–1).");
        meter.CreateObservableGauge("bdfu_instant_recovery_overlay_free_bytes", () => OverlayFree(nfs, options.Value),
            "By", "Free space for overlays on the overlay directory's volume, above the reserve (InstantRecovery:FreeSpaceReserveBytes); writes beyond it fail.");
        meter.CreateObservableCounter("bdfu_instant_recovery_cache_hits_total", () => cache.Hits, description: "Chunk reads of instant recoveries served from the memory cache.");
        meter.CreateObservableCounter("bdfu_instant_recovery_cache_loads_total", () => cache.Misses, description: "Chunks of instant recoveries loaded from the repositories.");
        meter.CreateObservableGauge("bdfu_instant_recovery_cache_bytes", () => cache.Bytes, "By", "Chunks of restore points the instant recovery cache holds (InstantRecovery:CacheBytes).");
    }

    public void RecordMigration(bool succeeded) => _migrations.Add(1, new KeyValuePair<string, object?>("result", succeeded ? "succeeded" : "failed"));

    private void Observe(NfsCallRecord record)
    {
        var procedure = new KeyValuePair<string, object?>("proc", record.Procedure);
        _requests.Add(1, procedure, new KeyValuePair<string, object?>("status", record.Status == NfsStatus.Ok ? "ok" : record.Status.ToString()));
        _requestSeconds.Record(record.Duration.TotalSeconds, procedure);
    }

    private static IEnumerable<Measurement<long>> OverlayFree(NfsServerHost nfs, InstantRecoveryOptions options)
    {
        if (!options.Enabled)
        {
            yield break;
        }

        long free;
        try
        {
            free = InstantRecoveryFileSystem.VolumeAvailableBytes(nfs.OverlayDirectory) - options.FreeSpaceReserveBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            yield break;
        }

        yield return new Measurement<long>(Math.Max(0, free));
    }

    private static KeyValuePair<string, object?>[] Tags(LiveInstantRecovery live) =>
    [
        new("session", live.SessionId.ToString()),
        new("vms", live.Vms),
        new("host", live.Host),
    ];
}
