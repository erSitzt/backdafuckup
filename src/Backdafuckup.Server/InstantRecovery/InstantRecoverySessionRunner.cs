using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.InstantRecovery;
using Backdafuckup.Nfs;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.InstantRecovery;

/// <summary>
/// Runs an instant recovery session for as long as it lives:
/// <list type="bullet">
/// <item><b>Start:</b> checks the host, plans each VM's files, builds the namespace (descriptors, the restore point
/// disks as flat files), serves the export to the host's VMkernel addresses only, mounts it as a datastore, registers
/// the VMs and powers them on. A failure removes what the start made, from its ledger rows.</item>
/// <item><b>Watch:</b> notices VMs that were migrated or removed in vCenter by hand, and warns by age.</item>
/// <item><b>Migrate</b> (asked through <see cref="InstantRecoveryControl"/>): Storage vMotion of each VM, then the
/// datastore goes. <b>Stop:</b> each VM powered off and unregistered, then the datastore goes.</item>
/// </list>
/// After a restart of the server it resumes: the restore points are pinned again, the namespace journal replayed, and the
/// export comes back with the same file handles, so the host's datastore reconnects and the VMs carry on. A shutdown of the
/// service leaves the session running in the catalog for exactly that. A stop of the session in the console asks the
/// instant recovery to stop.
/// </summary>
public sealed class InstantRecoverySessionRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    NfsServerHost nfs,
    RestorePointAccess restorePoints,
    AsyncChunkCache cache,
    SessionEventWriter events,
    ConnectionManager connections,
    InstantRecoveryControl control,
    Telemetry.InstantRecoveryMetrics metrics,
    IHostApplicationLifetime lifetime,
    IOptions<InstantRecoveryOptions> options,
    TimeProvider time,
    ILogger<InstantRecoverySessionRunner> logger)
{
    private const string EmptyBasePrefix = "empty:";

    /// <summary>How often a resume retries while the NFS server cannot listen.</summary>
    internal static TimeSpan ListenRetry { get; set; } = TimeSpan.FromSeconds(30);

    private TimeSpan Interval => options.Value.WatchInterval > TimeSpan.Zero ? options.Value.WatchInterval : TimeSpan.FromSeconds(10);

    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var log = events.CreateLog(session.Id, null);
        try
        {
            var recovery = await LoadAsync(session.Id, cancellationToken).ConfigureAwait(false);
            if (recovery is null)
            {
                return (SessionState.Failed, "No instant recovery belongs to this session.");
            }

            var live = control.Track(recovery);
            switch (recovery.State)
            {
                case InstantRecoveryState.Stopped or InstantRecoveryState.Migrated:
                    return (SessionState.Succeeded, $"The instant recovery ended ({recovery.State}).");
                case InstantRecoveryState.Failed:
                    return (SessionState.Failed, recovery.Message ?? "The instant recovery failed.");
                case InstantRecoveryState.Starting when Directory.Exists(recovery.OverlayDirectory):
                    // The start had begun (the overlay directory is its first step) when the server stopped.
                    return await FailInterruptedStartAsync(recovery, log).ConfigureAwait(false);
                case InstantRecoveryState.Starting:
                    var (failed, started) = await StartAsync(session.Id, recovery, log, cancellationToken).ConfigureAwait(false);
                    if (started is null)
                    {
                        return failed;
                    }

                    await using (started.ConfigureAwait(false))
                    {
                        live.FileSystem = started.FileSystem;
                        return await LiveAsync(session.Id, started, log, cancellationToken).ConfigureAwait(false);
                    }
            }

            await using var served = await ServeAsync(recovery, log, wait: true, null, cancellationToken).ConfigureAwait(false);
            live.FileSystem = served.FileSystem;
            log.Info($"Instant recovery resumed: datastore {recovery.DatastoreName} (export {recovery.ExportPath}) is served again for " +
                     $"{string.Join(", ", recovery.Vms.Select(v => v.Name))}.");
            return await LiveAsync(session.Id, served, log, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            control.Forget(session.Id);
        }
    }

    /// <summary>
    /// Pins the restore points, opens their disks, opens the file system on the overlay directory (replaying its journal),
    /// lets <paramref name="setup"/> build the namespace, and adds the export. Disposing the result removes the export and
    /// releases everything.
    /// </summary>
    /// <param name="wait">Wait for the NFS server to listen (a resume: the VMs wait), else fail at once (a start).</param>
    internal async Task<ServedRecovery> ServeAsync(
        InstantRecoveryEntity recovery, IOperationLog log, bool wait, Action<InstantRecoveryFileSystem>? setup, CancellationToken cancellationToken)
    {
        if (wait)
        {
            await WaitForNfsAsync(recovery, log, cancellationToken).ConfigureAwait(false);
        }
        else if (await nfs.EnsureListeningAsync(cancellationToken).ConfigureAwait(false) is { Listening: false } status)
        {
            throw new InvalidOperationException($"The NFS server of this backup server does not listen: {status.Error}");
        }

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
                var backedUp = opened.Reader.Manifest.Disks.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
                foreach (var diskKey in (vm.DiskFiles ?? []).Keys.Where(backedUp.Contains))
                {
                    var index = await opened.Reader.ReadDiskIndexAsync(diskKey, cancellationToken).ConfigureAwait(false);
                    bases[BaseKey(vm.RestorePointId, diskKey)] = new RestorePointBaseImage(index, opened.Reader.Chunks, cache, Math.Max(0, options.Value.ReadAheadChunks));
                }
            }

            var fileSystem = InstantRecoveryFileSystem.Open(
                recovery.OverlayDirectory, (ulong)recovery.ExportId, nfs.HandleKey,
                key => bases.TryGetValue(key, out var image) ? image
                    : key.StartsWith(EmptyBasePrefix, StringComparison.Ordinal) ? new EmptyBaseImage(long.Parse(key.AsSpan(EmptyBasePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture))
                    : throw new InvalidDataException($"The datastore of session {recovery.SessionId} names disk {key}, which none of its restore points has."),
                new InstantRecoveryFsOptions
                {
                    OverlayBudgetBytes = recovery.OverlayLimitBytes ?? long.MaxValue,
                    FreeSpaceReserveBytes = options.Value.FreeSpaceReserveBytes,
                },
                time);
            served.Open(fileSystem);
            setup?.Invoke(fileSystem);
            served.Export(new NfsExport(recovery.ExportPath, (ulong)recovery.ExportId, fileSystem, Allowlist(recovery)));
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

    /// <summary>
    /// The address of this server that the host mounts from: configured, else the listen address if it is one address,
    /// else the one that routes to the host's VMkernel addresses.
    /// </summary>
    internal static string RemoteHostFor(InstantRecoveryOptions options, string hostName, IReadOnlyList<string> hostAddresses)
    {
        if (!string.IsNullOrWhiteSpace(options.AdvertiseAddress))
        {
            return options.AdvertiseAddress.Trim();
        }

        if (IPAddress.TryParse(options.ListenAddress, out var listen) && !listen.Equals(IPAddress.Any) && !listen.Equals(IPAddress.IPv6Any))
        {
            return listen.ToString();
        }

        foreach (var address in hostAddresses)
        {
            if (!IPAddress.TryParse(address, out var target))
            {
                continue;
            }

            try
            {
                // A connected UDP socket sends nothing; it only picks the route, and with it the local address.
                using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(target, options.NfsPort > 0 ? options.NfsPort : 2049);
                if (socket.LocalEndPoint is IPEndPoint { Address: var local } && !local.Equals(IPAddress.Any) && !IPAddress.IsLoopback(local))
                {
                    return local.ToString();
                }
            }
            catch (SocketException)
            {
            }
        }

        throw new InvalidOperationException(
            $"No address of this backup server routes to ESXi host {hostName}'s VMkernel addresses ({string.Join(", ", hostAddresses)}); set InstantRecovery:AdvertiseAddress.");
    }

    private static HashSet<IPAddress>? Allowlist(InstantRecoveryEntity recovery) =>
        recovery.AllowedClients is { Count: > 0 } clients ? [.. clients.Select(IPAddress.Parse)] : null;

    private static InstantRecoveryDatastore Datastore(InstantRecoveryEntity recovery) =>
        new(recovery.DatastoreName, recovery.RemoteHost ?? string.Empty, recovery.ExportPath);

    private async Task<((SessionState State, string Summary) Result, ServedRecovery? Served)> StartAsync(
        Guid sessionId, InstantRecoveryEntity recovery, IOperationLog log, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return (await FailAsync(sessionId, log,
                "Instant recovery is off on this backup server: set InstantRecovery:Enabled to true in its configuration, restart the service, and let the ESXi hosts' " +
                "VMkernel addresses reach it on TCP/UDP 111 (portmapper), TCP 2049 (NFS) and TCP/UDP 20048 (MOUNT).").ConfigureAwait(false), null);
        }

        ServedRecovery? served = null;
        var began = false;
        try
        {
            await using var handle = new PlatformHandle(connections, recovery.ConnectionId);
            var platform = await handle.GetAsync(cancellationToken).ConfigureAwait(false);
            var host = await platform.GetHostAsync(recovery.HostId, cancellationToken).ConfigureAwait(false);
            if (host.Problems.Count > 0)
            {
                throw new InvalidOperationException(string.Join(" ", host.Problems));
            }

            // Each VM's files, from its restore point's manifest and backed-up configuration.
            var layouts = new Dictionary<Guid, (InstantRecoveryVmLayout Layout, RestorePointManifest Manifest, VmMetadataBlob Metadata)>();
            foreach (var vm in recovery.Vms)
            {
                var (manifest, metadata) = await ReadPointAsync(vm, cancellationToken).ConfigureAwait(false);
                var layout = platform.PlanVm(manifest, metadata, recovery.KeepDiskIdentity);
                foreach (var warning in layout.Warnings)
                {
                    log.Warning($"{vm.Name}: {warning}");
                }

                if (layout.Problems.Count > 0)
                {
                    throw new InvalidOperationException($"{vm.Name} cannot run from its restore point: {string.Join(" ", layout.Problems)}");
                }

                layouts[vm.Id] = (layout, manifest, metadata);
            }

            CheckSwapSpace(recovery, host, layouts.Values.Select(l => l.Layout.MemoryBytes).Sum());
            var remoteHost = RemoteHostFor(settings, host.Name, host.Addresses);
            recovery = await UpdateAsync(sessionId, r =>
            {
                r.HostName = host.Name;
                r.AllowedClients = [.. host.Addresses];
                r.RemoteHost = remoteHost;
                foreach (var vm in r.Vms)
                {
                    vm.DiskFiles = layouts[vm.Id].Layout.Disks.ToDictionary(d => d.DiskKey, d => $"{vm.Directory}/{d.Descriptor}", StringComparer.Ordinal);
                }
            }, cancellationToken).ConfigureAwait(false);

            // From here on the start has made something: the overlay directory first.
            Directory.CreateDirectory(recovery.OverlayDirectory);
            began = true;
            served = await ServeAsync(recovery, log, wait: false, fileSystem =>
            {
                foreach (var vm in recovery.Vms)
                {
                    fileSystem.CreateDirectory(vm.Directory);
                    foreach (var disk in layouts[vm.Id].Layout.Disks)
                    {
                        fileSystem.CreateBaseFile($"{vm.Directory}/{disk.FlatFile}",
                            disk.Empty ? EmptyBasePrefix + disk.CapacityBytes.ToString(CultureInfo.InvariantCulture) : BaseKey(vm.RestorePointId, disk.DiskKey), disk.CapacityBytes);
                        fileSystem.CreateFile($"{vm.Directory}/{disk.Descriptor}", disk.DescriptorContent);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
            log.Info($"Serving {recovery.ExportPath} over NFS 3 from {remoteHost} to ESXi host {host.Name} ({string.Join(", ", host.Addresses)}) only: " +
                     $"{string.Join(", ", recovery.Vms.Select(v => $"{v.Name} ({layouts[v.Id].Layout.Disks.Count} disk(s))"))}.");

            var ledger = new DbArtifactLedger(dbFactory, recovery.ConnectionId, sessionId, null, time);
            string datastoreId;
            try
            {
                datastoreId = await platform.MountDatastoreAsync(host.Id, Datastore(recovery), ledger, log, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"{ex.Message} {DescribeClients(host.Addresses)}", ex);
            }

            recovery = await UpdateAsync(sessionId, r => r.DatastoreId = datastoreId, cancellationToken).ConfigureAwait(false);
            foreach (var vm in recovery.Vms)
            {
                var (layout, manifest, metadata) = layouts[vm.Id];
                var created = await platform.CreateVmAsync(new InstantRecoveryVmSpec
                {
                    Name = vm.Name,
                    HostId = host.Id,
                    DatastoreId = datastoreId,
                    DatastoreName = recovery.DatastoreName,
                    Directory = vm.Directory,
                    Manifest = manifest,
                    Metadata = metadata,
                    Disks = layout.Disks,
                    FolderId = recovery.FolderId,
                    ResourcePoolId = recovery.ResourcePoolId,
                    NetworkMap = vm.NetworkMap ?? [],
                    ConnectNics = recovery.ConnectNics,
                }, ledger, log, cancellationToken).ConfigureAwait(false);
                recovery = await UpdateAsync(sessionId, r =>
                {
                    var row = r.Vms.Single(v => v.Id == vm.Id);
                    row.VmId = created.Id;
                    row.InstanceUuid = created.InstanceUuid;
                }, cancellationToken).ConfigureAwait(false);
            }

            foreach (var vm in recovery.Vms)
            {
                try
                {
                    await platform.PowerOnAsync(vm.VmId!, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex.Message.Contains("MonitorLoop", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"ESXi host {host.Name} could not power on VM {vm.Name}: {ex.Message} ESXi could not prepare the VM's memory, usually because its swap file " +
                        $"({Bytes(layouts[vm.Id].Layout.MemoryBytes)}) does not fit: datastore {recovery.DatastoreName} offers only what the write limit or the overlay volume leaves free. " +
                        "Raise the write limit, free space on the overlay volume, or give the host a swap datastore.", ex);
                }

                log.Info($"Powered on VM {vm.Name} on ESXi host {host.Name}.");
            }

            await UpdateAsync(sessionId, r =>
            {
                r.State = InstantRecoveryState.Running;
                r.Message = null;
            }, cancellationToken).ConfigureAwait(false);
            log.Info($"Instant recovery running: {string.Join(", ", recovery.Vms.Select(v => v.Name))} {(recovery.Vms.Count == 1 ? "runs" : "run")} from datastore {recovery.DatastoreName} ({remoteHost}:{recovery.ExportPath}) " +
                     $"on ESXi host {host.Name}. Migrate {(recovery.Vms.Count == 1 ? "it" : "them")} to production storage, or stop the instant recovery, in the backup console.");
            return ((SessionState.Running, string.Empty), served);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && lifetime.ApplicationStopping.IsCancellationRequested)
        {
            // The service stops: the start is undone when the session resumes (it is still Starting).
            if (served is not null)
            {
                await served.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            var reason = ex is OperationCanceledException ? "The instant recovery was stopped while it was starting." : ex.Message;
            log.Error(reason);
            logger.LogWarning(ex, "Instant recovery {Session} failed to start", sessionId);
            if (began)
            {
                await UndoAsync(sessionId, recovery.ConnectionId, log).ConfigureAwait(false);
            }

            if (served is not null)
            {
                await served.DisposeAsync().ConfigureAwait(false);
            }

            if (began)
            {
                DeleteOverlay(recovery.OverlayDirectory, log);
            }

            return (await FailAsync(sessionId, log, reason).ConfigureAwait(false), null);
        }
    }

    /// <summary>
    /// Without a swap datastore of the host, ESXi puts each VM's swap file (as big as its memory) into the instant
    /// recovery's datastore and powers the VM on only if that much is free there: the write limit and the overlay volume
    /// must leave room for it, plus some for what the VMs write. Checked before anything is made.
    /// </summary>
    private void CheckSwapSpace(InstantRecoveryEntity recovery, InstantRecoveryHost host, long memoryBytes)
    {
        if (host.SwapDatastore is not null || memoryBytes <= 0)
        {
            return;
        }

        var needed = memoryBytes + (1L << 30);
        if (recovery.OverlayLimitBytes is { } limit && limit < needed)
        {
            throw new InvalidOperationException(
                $"The write limit of {Bytes(limit)} leaves no room for the VMs' swap files ({Bytes(memoryBytes)} of memory): ESXi host {host.Name} has no swap datastore of its own, " +
                $"so ESXi creates them in the instant recovery's datastore and powers the VMs on only if that much is free there. Raise the limit to at least {Bytes(needed)}, or leave it empty.");
        }

        long free;
        try
        {
            free = InstantRecoveryFileSystem.VolumeAvailableBytes(nfs.OverlayDirectory) - options.Value.FreeSpaceReserveBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }

        if (free < needed)
        {
            throw new InvalidOperationException(
                $"The overlay volume of this backup server ({nfs.OverlayDirectory}) has {Bytes(Math.Max(0, free))} free above the reserve, too little for the VMs' swap files " +
                $"({Bytes(memoryBytes)} of memory): ESXi host {host.Name} has no swap datastore of its own, so ESXi creates them in the instant recovery's datastore. Free space there.");
        }
    }

    /// <summary>The restore point's manifest and its VM configuration.</summary>
    private async Task<(RestorePointManifest Manifest, VmMetadataBlob Metadata)> ReadPointAsync(InstantRecoveryVmEntity vm, CancellationToken cancellationToken)
    {
        RestorePointEntity point;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            point = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == vm.RestorePointId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The restore point {vm.RestorePointId} of {vm.Name} is no longer in the catalog.");
        }

        await using var opened = await restorePoints.OpenRestorePointAsync(point, cancellationToken).ConfigureAwait(false);
        var manifest = opened.Reader.Manifest;
        var blob = manifest.Metadata ?? throw new InvalidDataException($"The restore point of {point.VmName} from {point.CreatedUtc:yyyy-MM-dd HH:mm} UTC holds no VM configuration.");
        await using var stream = await opened.Reader.OpenBlobAsync(blob, cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return (manifest, new VmMetadataBlob(blob.Format, memory.ToArray()));
    }

    /// <summary>What the NFS server saw of the host's addresses, for an error about a mount.</summary>
    private string DescribeClients(IReadOnlyList<string> hostAddresses) =>
        string.Join(" ", hostAddresses.Select(a => nfs.Clients.Describe(IPAddress.Parse(a), nfs.ExpectedPorts).TrimEnd('.') + "."));

    /// <summary>
    /// While the instant recovery lives: acts on its state (running, stopping, migrating) every interval or when woken. A
    /// stop of the session asks it to stop; if the stop is refused, it keeps running (until the service stops).
    /// </summary>
    private async Task<(SessionState State, string Summary)> LiveAsync(Guid sessionId, ServedRecovery served, IOperationLog log, CancellationToken cancellationToken)
    {
        var token = cancellationToken;
        var watch = new WatchState();
        await using var handle = new PlatformHandle(connections, (await LoadAsync(sessionId, token).ConfigureAwait(false))!.ConnectionId);
        while (true)
        {
            try
            {
                var recovery = await LoadAsync(sessionId, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The instant recovery is no longer in the catalog.");
                control.Track(recovery);
                var result = recovery.State switch
                {
                    InstantRecoveryState.Stopping => await StopAsync(recovery, served, handle, log, watch, token).ConfigureAwait(false),
                    InstantRecoveryState.Migrating => await MigrateAsync(recovery, served, handle, log, watch, token).ConfigureAwait(false),
                    InstantRecoveryState.Running => await WatchAsync(recovery, served, handle, log, watch, token).ConfigureAwait(false),
                    InstantRecoveryState.Failed => (SessionState.Failed, recovery.Message ?? "The instant recovery failed."),
                    _ => (SessionState.Succeeded, $"The instant recovery ended ({recovery.State})."),
                };
                if (result is { } done)
                {
                    return done;
                }

                await control.WaitAsync(sessionId, Interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested && !lifetime.ApplicationStopping.IsCancellationRequested && token == cancellationToken)
            {
                // The session was stopped in the console: the instant recovery stops. From here on only a stop of the
                // service ends the loop, so that a refused stop keeps the VMs' datastore served.
                token = lifetime.ApplicationStopping;
                log.Info(await control.RequestStopAsync(sessionId, token).ConfigureAwait(false)
                    ? "The session was stopped: the instant recovery stops (its VMs are powered off and removed from vCenter, then its datastore)."
                    : "The session was stopped while the instant recovery is migrating or stopping already; that goes on.");
            }
        }
    }

    /// <summary>Running: looks at the VMs. Null while it goes on; a result once every VM is migrated or gone.</summary>
    private async Task<(SessionState, string)?> WatchAsync(
        InstantRecoveryEntity recovery, ServedRecovery served, PlatformHandle handle, IOperationLog log, WatchState watch, CancellationToken cancellationToken)
    {
        if (!watch.AgeWarned && time.GetUtcNow() - recovery.CreatedUtc > options.Value.AgeWarning)
        {
            watch.AgeWarned = true;
            log.Warning($"The instant recovery runs for more than {options.Value.AgeWarning.TotalHours.ToString("0", CultureInfo.InvariantCulture)} hours: its VMs depend on this " +
                        $"backup server and grow its overlay ({Bytes(served.FileSystem?.OverlayBytes ?? 0)} now). Migrate them to production storage, or stop it.");
        }

        try
        {
            var platform = await handle.GetAsync(cancellationToken).ConfigureAwait(false);
            var settled = 0;
            foreach (var vm in recovery.Vms.Where(v => v.VmId is not null))
            {
                var state = await platform.GetVmStateAsync(vm.VmId!, recovery.DatastoreName, cancellationToken).ConfigureAwait(false);
                if (!state.Exists)
                {
                    settled++;
                    if (watch.Gone.Add(vm.Id))
                    {
                        log.Warning($"VM {vm.Name} ({vm.VmId}) was removed from vCenter outside the backup server.");
                    }
                }
                else if (state.FilesOnDatastore.Count == 0)
                {
                    settled++;
                    if (watch.Moved.Add(vm.Id))
                    {
                        log.Info($"VM {vm.Name} was migrated off datastore {recovery.DatastoreName} in vCenter: it now runs from {Datastores(state.FilesElsewhere)}.");
                    }
                }

                // A VM paused on a question (above all "no more space" once its writes passed the write limit): say so once.
                if (state.Question is { } question && watch.Asked.Add((vm.Id, question)))
                {
                    var limit = recovery.OverlayLimitBytes;
                    var full = limit is { } l && (served.FileSystem?.OverlayBytes ?? 0) >= l - (64L << 20);
                    log.Warning($"VM {vm.Name} is paused: it waits on a question in vCenter: \"{question.Trim()}\"" +
                                (full
                                    ? $" Its writes reached the write limit of {Bytes(limit!.Value)}, so it cannot go on with this instant recovery: stop it (what the VM wrote " +
                                      "is lost) and start again with a higher limit, or none."
                                    : " Answer it in vCenter, or stop the instant recovery."));
                }
            }

            if (watch.Unreachable is not null)
            {
                log.Info($"vCenter answers again ({watch.Unreachable}).");
                watch.Unreachable = null;
            }

            return settled > 0 && settled == recovery.Vms.Count(v => v.VmId is not null)
                ? await FinishAsync(recovery, served, handle, log, watch, migrated: watch.Moved.Count > 0, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (watch.Unreachable is null)
            {
                log.Warning($"Cannot look at the VMs in vCenter: {ex.Message} The VMs keep running; tried again every {Interval.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s.");
                watch.Unreachable = ex.Message;
            }

            await handle.ResetAsync().ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Stopping: every VM powered off and unregistered, then the datastore goes. A refusal keeps it running.</summary>
    private async Task<(SessionState, string)?> StopAsync(
        InstantRecoveryEntity recovery, ServedRecovery served, PlatformHandle handle, IOperationLog log, WatchState watch, CancellationToken cancellationToken)
    {
        if (!watch.StopLogged)
        {
            watch.StopLogged = true;
            log.Info($"Stopping the instant recovery: {string.Join(", ", recovery.Vms.Select(v => v.Name))} {(recovery.Vms.Count == 1 ? "is" : "are")} powered off and removed from vCenter " +
                     $"(no file is deleted), then datastore {recovery.DatastoreName} is unmounted.");
        }

        try
        {
            var platform = await handle.GetAsync(cancellationToken).ConfigureAwait(false);
            foreach (var vm in recovery.Vms.Where(v => v.VmId is not null))
            {
                await platform.RemoveVmAsync(vm.VmId!, recovery.DatastoreName, log, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (InstantRecoveryRefusedException ex)
        {
            log.Error($"{ex.Message} The instant recovery keeps running.");
            await UpdateAsync(recovery.SessionId, r =>
            {
                r.State = InstantRecoveryState.Running;
                r.Message = ex.Message;
            }, cancellationToken).ConfigureAwait(false);
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                // A stop of the session asked for it: the session runs on.
                await db.Sessions.Where(s => s.Id == recovery.SessionId && s.State == SessionState.Stopping)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, SessionState.Running), cancellationToken).ConfigureAwait(false);
            }

            watch.StopLogged = false;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Once(watch, log, $"Stopping is held up: {ex.Message} Tried again every {Interval.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s.");
            await handle.ResetAsync().ConfigureAwait(false);
            return null;
        }

        return await FinishAsync(recovery, served, handle, log, watch, migrated: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Migrating: Storage vMotion of each VM still on the datastore (one after the other), then the datastore goes.</summary>
    private async Task<(SessionState, string)?> MigrateAsync(
        InstantRecoveryEntity recovery, ServedRecovery served, PlatformHandle handle, IOperationLog log, WatchState watch, CancellationToken cancellationToken)
    {
        var current = (string?)null;
        try
        {
            var target = recovery.MigrationTarget ?? throw new InvalidOperationException("No migration target was given.");
            var platform = await handle.GetAsync(cancellationToken).ConfigureAwait(false);
            if (recovery.MigrateTaskId is { } pending)
            {
                // After a restart: the Storage vMotion that was running.
                if (await WaitTaskAsync(recovery.SessionId, platform, pending, "a VM", log, cancellationToken).ConfigureAwait(false) is { } error)
                {
                    throw new InvalidOperationException(error);
                }

                await UpdateAsync(recovery.SessionId, r => r.MigrateTaskId = null, cancellationToken).ConfigureAwait(false);
            }

            foreach (var vm in recovery.Vms.Where(v => v.VmId is not null))
            {
                current = vm.Name;
                var state = await platform.GetVmStateAsync(vm.VmId!, recovery.DatastoreName, cancellationToken).ConfigureAwait(false);
                if (!state.Exists || state.FilesOnDatastore.Count == 0)
                {
                    continue;
                }

                var task = await platform.StartMigrationAsync(vm.VmId!, target, log, cancellationToken).ConfigureAwait(false);
                await UpdateAsync(recovery.SessionId, r => r.MigrateTaskId = task, cancellationToken).ConfigureAwait(false);
                if (await WaitTaskAsync(recovery.SessionId, platform, task, vm.Name, log, cancellationToken).ConfigureAwait(false) is { } error)
                {
                    throw new InvalidOperationException(error);
                }

                await UpdateAsync(recovery.SessionId, r => r.MigrateTaskId = null, cancellationToken).ConfigureAwait(false);
                state = await platform.GetVmStateAsync(vm.VmId!, recovery.DatastoreName, cancellationToken).ConfigureAwait(false);
                if (state.FilesOnDatastore.Count > 0)
                {
                    throw new InvalidOperationException($"files of it are still on datastore {recovery.DatastoreName}: {string.Join(", ", state.FilesOnDatastore)}");
                }

                log.Info($"VM {vm.Name} now runs from {Datastores(state.FilesElsewhere)}.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = $"Migrating {(current is null ? "the VMs" : $"VM {current}")} failed: {ex.Message}. " +
                          $"It keeps running from datastore {recovery.DatastoreName}; migrate it again, or stop the instant recovery.";
            log.Error(message);
            metrics.RecordMigration(succeeded: false);
            await UpdateAsync(recovery.SessionId, r =>
            {
                r.State = InstantRecoveryState.Running;
                r.MigrationTarget = null;
                r.MigrateTaskId = null;
                r.Message = message;
            }, CancellationToken.None).ConfigureAwait(false);
            await handle.ResetAsync().ConfigureAwait(false);
            return null;
        }

        return await FinishAsync(recovery, served, handle, log, watch, migrated: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits for a platform task; logs its progress by quarters. Null on success, else its error.</summary>
    private async Task<string?> WaitTaskAsync(Guid sessionId, IInstantRecoveryPlatform platform, string taskId, string vmName, IOperationLog log, CancellationToken cancellationToken)
    {
        var reported = 0;
        var poll = Interval < TimeSpan.FromSeconds(2) ? Interval : TimeSpan.FromSeconds(2);
        while (true)
        {
            var task = await platform.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (control.Find(sessionId) is { } live && task.Progress is { } percent)
            {
                live.MigrationProgress = Math.Clamp(percent / 100.0, 0, 1);
            }

            if (task.Done)
            {
                return task.State == "success" ? null : task.Error ?? "the task failed without a message";
            }

            if (task.Progress is { } progress && progress / 25 > reported && progress < 100)
            {
                reported = progress / 25;
                log.Info($"Migrating {vmName}: {progress} %.");
            }

            await Task.Delay(poll, time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Every VM is off the datastore (migrated, removed or gone): it is unmounted, the export removed, the overlay
    /// deleted, the restore points released, and the ledger rows closed (a migrated VM is the administrator's now).
    /// </summary>
    private async Task<(SessionState, string)?> FinishAsync(
        InstantRecoveryEntity recovery, ServedRecovery served, PlatformHandle handle, IOperationLog log, WatchState watch, bool migrated, CancellationToken cancellationToken)
    {
        if (recovery.DatastoreId is { } datastoreId)
        {
            try
            {
                var platform = await handle.GetAsync(cancellationToken).ConfigureAwait(false);
                await platform.UnmountDatastoreAsync(datastoreId, Datastore(recovery), log, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Once(watch, log, $"Datastore {recovery.DatastoreName} is not unmounted yet: {ex.Message} Tried again every {Interval.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s.");
                await handle.ResetAsync().ConfigureAwait(false);
                return null;
            }
        }

        var overlay = served.FileSystem?.PeakOverlayBytes ?? 0;
        if (control.Find(recovery.SessionId) is { } live)
        {
            live.FileSystem = null;
        }

        if (migrated)
        {
            metrics.RecordMigration(succeeded: true);
        }

        await served.DisposeAsync().ConfigureAwait(false);
        DeleteOverlay(recovery.OverlayDirectory, log);
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false))
        {
            await db.Artifacts.Where(a => a.SessionId == recovery.SessionId && a.State != ArtifactState.Done)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, ArtifactState.Done).SetProperty(a => a.UpdatedUtc, time.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
        }

        var state = migrated ? InstantRecoveryState.Migrated : InstantRecoveryState.Stopped;
        await UpdateAsync(recovery.SessionId, r =>
        {
            r.State = state;
            r.MigrationTarget = null;
            r.Message = null;
        }, CancellationToken.None).ConfigureAwait(false);
        var summary = migrated
            ? $"Migrated: {string.Join(", ", recovery.Vms.Select(v => v.Name))} {(recovery.Vms.Count == 1 ? "runs" : "run")} from production storage; datastore {recovery.DatastoreName} is gone, the overlay deleted (the VMs wrote {Bytes(overlay)} into it at most)."
            : $"Stopped: {string.Join(", ", recovery.Vms.Select(v => v.Name))} removed, datastore {recovery.DatastoreName} unmounted, the overlay deleted (the VMs wrote {Bytes(overlay)} into it at most).";
        log.Info(summary);
        return (SessionState.Succeeded, summary);
    }

    /// <summary>The server stopped while the start was under way: what it made is removed, and the session fails.</summary>
    private async Task<(SessionState, string)> FailInterruptedStartAsync(InstantRecoveryEntity recovery, IOperationLog log)
    {
        const string Reason = "The backup server stopped while this instant recovery was starting; what the start had made is removed. Start it again.";
        log.Warning(Reason);
        ServedRecovery? served = null;
        try
        {
            // Served again for the cleanup, so that the host still reaches the VMs' files while they are removed.
            served = await ServeAsync(recovery, log, wait: false, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("Instant recovery {Session}: the interrupted start's datastore is not served for its cleanup ({Error}).", recovery.SessionId, ex.Message);
        }

        await UndoAsync(recovery.SessionId, recovery.ConnectionId, log).ConfigureAwait(false);
        if (served is not null)
        {
            await served.DisposeAsync().ConfigureAwait(false);
        }

        DeleteOverlay(recovery.OverlayDirectory, log);
        return await FailAsync(recovery.SessionId, log, Reason).ConfigureAwait(false);
    }

    /// <summary>Removes what a start made, from its ledger rows, newest first (the VMs, then the datastore). What fails is left to the periodic cleanup.</summary>
    private async Task UndoAsync(Guid sessionId, Guid connectionId, IOperationLog log)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        List<ProviderArtifactEntity> artifacts;
        await using (var db = await dbFactory.CreateDbContextAsync(timeout.Token).ConfigureAwait(false))
        {
            artifacts = await db.Artifacts.Where(a => a.SessionId == sessionId && a.State != ArtifactState.Done).OrderByDescending(a => a.CreatedUtc)
                .ToListAsync(timeout.Token).ConfigureAwait(false);
            if (artifacts.Count == 0)
            {
                return;
            }

            try
            {
                await using var hypervisor = await connections.OpenAsync(connectionId, timeout.Token).ConfigureAwait(false);
                foreach (var artifact in artifacts)
                {
                    try
                    {
                        await hypervisor.CleanupArtifactAsync(
                            new ProviderArtifact(artifact.Id, artifact.Kind, artifact.NativeId, artifact.NativeRef, artifact.SessionId, artifact.RestorePointId, artifact.State, artifact.Data),
                            log, timeout.Token).ConfigureAwait(false);
                        artifact.State = ArtifactState.Done;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || timeout.IsCancellationRequested)
                    {
                        log.Warning($"Removing {artifact.Kind} {artifact.NativeId} failed ({ex.Message}); it is removed automatically later.");
                        artifact.State = ArtifactState.CleanupPending;
                        artifact.LastError = ex.Message;
                    }

                    artifact.UpdatedUtc = time.GetUtcNow();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Warning($"vCenter cannot be reached to remove what the start made ({ex.Message}); it is removed automatically later.");
            }

            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<(SessionState, string)> FailAsync(Guid sessionId, IOperationLog log, string reason)
    {
        await UpdateAsync(sessionId, r =>
        {
            r.State = InstantRecoveryState.Failed;
            r.Message = reason;
        }, CancellationToken.None).ConfigureAwait(false);
        return (SessionState.Failed, reason);
    }

    private void DeleteOverlay(string directory, IOperationLog log)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"The overlay directory {directory} could not be deleted ({ex.Message}); delete it by hand.");
            logger.LogWarning(ex, "Overlay {Directory} not deleted", directory);
        }
    }

    private async Task<InstantRecoveryEntity?> LoadAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.InstantRecoveries.AsNoTracking().Include(r => r.Vms.OrderBy(v => v.Directory)).FirstOrDefaultAsync(r => r.SessionId == sessionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InstantRecoveryEntity> UpdateAsync(Guid sessionId, Action<InstantRecoveryEntity> change, CancellationToken cancellationToken)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var recovery = await db.InstantRecoveries.Include(r => r.Vms).SingleAsync(r => r.SessionId == sessionId, cancellationToken).ConfigureAwait(false);
            change(recovery);
            recovery.UpdatedUtc = time.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return (await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Logs a held-up step once per distinct message, not every interval.</summary>
    private static void Once(WatchState watch, IOperationLog log, string message)
    {
        if (watch.LastProblem != message)
        {
            watch.LastProblem = message;
            log.Warning(message);
        }
    }

    /// <summary>"datastore ds-01" or "datastores ds-01, ds-02", from datastore paths ("[ds-01] vm/vm.vmx").</summary>
    private static string Datastores(IEnumerable<string> files)
    {
        var names = files.Select(f => f.StartsWith('[') && f.IndexOf(']', StringComparison.Ordinal) is > 0 and var end ? f[1..end] : f).Distinct(StringComparer.Ordinal).ToList();
        return names.Count == 1 ? $"datastore {names[0]}" : $"datastores {string.Join(", ", names)}";
    }

    private static string Bytes(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GiB"
        : (bytes / (double)(1L << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MiB";

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

    /// <summary>What the watch has said already (each thing once) and what it has seen.</summary>
    private sealed class WatchState
    {
        public HashSet<Guid> Gone { get; } = [];

        public HashSet<Guid> Moved { get; } = [];

        /// <summary>The questions a VM waited on that were logged already.</summary>
        public HashSet<(Guid Vm, string Question)> Asked { get; } = [];

        public bool AgeWarned { get; set; }

        public bool StopLogged { get; set; }

        public string? Unreachable { get; set; }

        public string? LastProblem { get; set; }
    }

    /// <summary>The connection to the platform, opened when needed and again after an error.</summary>
    private sealed class PlatformHandle(ConnectionManager connections, Guid connectionId) : IAsyncDisposable
    {
        private IHypervisorSession? _session;

        public async Task<IInstantRecoveryPlatform> GetAsync(CancellationToken cancellationToken)
        {
            _session ??= await connections.OpenAsync(connectionId, cancellationToken).ConfigureAwait(false);
            return _session as IInstantRecoveryPlatform
                ?? throw new NotSupportedException($"The connection of this instant recovery is {_session.Platform.ProductName}; instant recovery runs VMs on vSphere only.");
        }

        public async ValueTask ResetAsync()
        {
            if (_session is not null)
            {
                await _session.DisposeAsync().ConfigureAwait(false);
                _session = null;
            }
        }

        public ValueTask DisposeAsync() => ResetAsync();
    }

    /// <summary>What a served instant recovery holds open: the export, its file system, the pinned restore points.</summary>
    internal sealed class ServedRecovery(NfsServerHost nfs, ulong exportId) : IAsyncDisposable
    {
        private readonly List<IAsyncDisposable> _kept = [];
        private InstantRecoveryFileSystem? _fileSystem;
        private bool _exported;

        public InstantRecoveryFileSystem? FileSystem => _fileSystem;

        public void Keep(IAsyncDisposable item) => _kept.Add(item);

        public void Open(InstantRecoveryFileSystem fileSystem) => _fileSystem = fileSystem;

        public void Export(NfsExport export)
        {
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
