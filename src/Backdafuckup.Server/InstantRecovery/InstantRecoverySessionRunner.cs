using System.Globalization;
using System.Net;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.InstantRecovery;
using Backdafuckup.Nfs;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.InstantRecovery;

/// <summary>
/// Runs an instant recovery session for as long as it lives. After a restart of the server it resumes: the restore points
/// are pinned again, the namespace journal replayed, and the export comes back with the same file handles, so the host's
/// datastore reconnects and the VMs carry on (their disk I/O waited meanwhile). A shutdown of the service leaves the
/// session running in the catalog for exactly that.
/// </summary>
public sealed class InstantRecoverySessionRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    NfsServerHost nfs,
    RestorePointAccess restorePoints,
    AsyncChunkCache cache,
    SessionEventWriter events,
    IOptions<InstantRecoveryOptions> options,
    TimeProvider time,
    ILogger<InstantRecoverySessionRunner> logger)
{
    /// <summary>How often a resume retries while the NFS server cannot listen.</summary>
    internal static TimeSpan ListenRetry { get; set; } = TimeSpan.FromSeconds(30);

    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var log = events.CreateLog(session.Id, null);
        InstantRecoveryEntity? recovery;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            recovery = await db.InstantRecoveries.AsNoTracking().Include(r => r.Vms).FirstOrDefaultAsync(r => r.SessionId == session.Id, cancellationToken).ConfigureAwait(false);
        }

        if (recovery is null)
        {
            return (SessionState.Failed, "No instant recovery belongs to this session.");
        }

        switch (recovery.State)
        {
            case InstantRecoveryState.Stopped or InstantRecoveryState.Migrated:
                return (SessionState.Succeeded, $"The instant recovery ended ({recovery.State}).");
            case InstantRecoveryState.Failed:
                return (SessionState.Failed, recovery.Message ?? "The instant recovery failed.");
            case InstantRecoveryState.Starting:
                return (SessionState.Failed, "Starting an instant recovery is not available in this version of the server.");
        }

        await using var served = await ServeAsync(recovery, log, cancellationToken).ConfigureAwait(false);
        log.Info($"Instant recovery resumed: datastore {recovery.DatastoreName} (export {recovery.ExportPath}) is served again for " +
                 $"{string.Join(", ", recovery.Vms.Select(v => v.Name))}.");
        await Task.Delay(Timeout.InfiniteTimeSpan, time, cancellationToken).ConfigureAwait(false);
        return (SessionState.Stopped, "Stopped.");
    }

    /// <summary>
    /// Pins the restore points, opens their disks, opens the file system on the overlay directory (replaying its journal) and
    /// adds the export. Disposing the result removes the export and releases everything.
    /// </summary>
    internal async Task<ServedRecovery> ServeAsync(InstantRecoveryEntity recovery, IOperationLog log, CancellationToken cancellationToken)
    {
        await WaitForNfsAsync(recovery, log, cancellationToken).ConfigureAwait(false);
        var served = new ServedRecovery(nfs, (ulong)recovery.ExportId);
        try
        {
            var bases = new Dictionary<string, IBaseImage>(StringComparer.Ordinal);
            foreach (var vm in recovery.Vms)
            {
                RestorePointEntity point;
                await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
                {
                    point = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == vm.RestorePointId, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"The restore point {vm.RestorePointId} of {vm.Name} is no longer in the catalog.");
                }

                var opened = await restorePoints.OpenRestorePointAsync(point, cancellationToken).ConfigureAwait(false);
                served.Keep(opened);
                foreach (var diskKey in (vm.DiskFiles ?? []).Keys)
                {
                    var index = await opened.Reader.ReadDiskIndexAsync(diskKey, cancellationToken).ConfigureAwait(false);
                    bases[BaseKey(vm.RestorePointId, diskKey)] = new RestorePointBaseImage(index, opened.Reader.Chunks, cache, Math.Max(0, options.Value.ReadAheadChunks));
                }
            }

            var fileSystem = InstantRecoveryFileSystem.Open(
                recovery.OverlayDirectory, (ulong)recovery.ExportId, nfs.HandleKey,
                key => bases.TryGetValue(key, out var image) ? image : throw new InvalidDataException($"The datastore of session {recovery.SessionId} names disk {key}, which none of its restore points has."),
                new InstantRecoveryFsOptions
                {
                    OverlayBudgetBytes = recovery.OverlayLimitBytes ?? long.MaxValue,
                    FreeSpaceReserveBytes = options.Value.FreeSpaceReserveBytes,
                },
                time);
            served.Serve(fileSystem, new NfsExport(recovery.ExportPath, (ulong)recovery.ExportId, fileSystem, Allowlist(recovery)));
            return served;
        }
        catch
        {
            await served.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The key of a restore point disk as the base of a file in the datastore.</summary>
    public static string BaseKey(Guid restorePointId, string diskKey) => $"{restorePointId:N}/{diskKey}";

    private static HashSet<IPAddress>? Allowlist(InstantRecoveryEntity recovery) =>
        recovery.AllowedClients is { Count: > 0 } clients ? [.. clients.Select(IPAddress.Parse)] : null;

    /// <summary>The VMs wait on their datastore until the NFS server listens again; until then the reason is logged, and tried again.</summary>
    private async Task WaitForNfsAsync(InstantRecoveryEntity recovery, IOperationLog log, CancellationToken cancellationToken)
    {
        while (true)
        {
            var status = await nfs.EnsureListeningAsync(cancellationToken).ConfigureAwait(false);
            if (status.Listening)
            {
                return;
            }

            log.Error($"The datastore {recovery.DatastoreName} cannot be served: {status.Error} Tried again in " +
                      $"{ListenRetry.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s; the VMs' disk I/O waits meanwhile.");
            logger.LogError("Instant recovery {Session} waits for the NFS server: {Error}", recovery.SessionId, status.Error);
            await Task.Delay(ListenRetry, time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>What a served instant recovery holds open: the export, its file system, the pinned restore points.</summary>
    internal sealed class ServedRecovery(NfsServerHost nfs, ulong exportId) : IAsyncDisposable
    {
        private readonly List<IAsyncDisposable> _kept = [];
        private InstantRecoveryFileSystem? _fileSystem;
        private bool _exported;

        public InstantRecoveryFileSystem? FileSystem => _fileSystem;

        public void Keep(IAsyncDisposable item) => _kept.Add(item);

        public void Serve(InstantRecoveryFileSystem fileSystem, NfsExport export)
        {
            _fileSystem = fileSystem;
            nfs.AddExport(export);
            _exported = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_exported)
            {
                nfs.RemoveExport(exportId);
                _exported = false;
            }

            _fileSystem?.Dispose();
            _fileSystem = null;
            foreach (var item in Enumerable.Reverse(_kept))
            {
                await item.DisposeAsync().ConfigureAwait(false);
            }

            _kept.Clear();
        }
    }
}
