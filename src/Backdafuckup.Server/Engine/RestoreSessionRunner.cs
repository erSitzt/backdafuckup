using System.Text.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.DataMover;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Engine;

/// <summary>Restores a whole VM from a restore point to the original or an alternate location.</summary>
public sealed partial class RestoreSessionRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    ConnectionManager connections,
    SessionEventWriter events,
    SessionTracker tracker,
    Telemetry.BackupMetrics metrics,
    TimeProvider time,
    Backdafuckup.FileLevel.ChunkCache cache,
    RestorePointAccess access)
{
    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<RestoreRequestDto>(session.Parameters ?? "null")
            ?? throw new InvalidOperationException("The restore session has no parameters.");
        return request.KubernetesCluster is { } cluster
            ? await RunClusterAsync(session, request, cluster, cancellationToken).ConfigureAwait(false)
            : await RestorePointAsync(session, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Restores one restore point (a VM or a namespace) as a task of the session.</summary>
    private async Task<(SessionState State, string Summary)> RestorePointAsync(SessionEntity session, RestoreRequestDto request, CancellationToken cancellationToken)
    {
        RestorePointEntity point;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            point = await db.RestorePoints.Include(r => r.Disks).AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == request.RestorePointId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The restore point no longer exists.");
        }

        return await RestoreAsync(session, request, point, token => access.OpenRestorePointAsync(point, token), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Restores a VM or namespace from a restore point that <paramref name="open"/> opens: from its repository, or from
    /// the cache a tape copy was read into.
    /// </summary>
    internal async Task<(SessionState State, string Summary)> RestoreAsync(
        SessionEntity session, RestoreRequestDto request, RestorePointEntity point, Func<CancellationToken, Task<OpenedRestorePoint>> open, CancellationToken cancellationToken)
    {
        var targetConnection = request.TargetConnectionId ?? point.ConnectionId
            ?? throw new InvalidOperationException("No target pool selected.");
        var task = await tracker.AddTaskAsync(session.Id, point.VmNativeId, point.VmName, cancellationToken).ConfigureAwait(false);
        var log = new WarningTrackingLog(events.CreateLog(session.Id, task.Id, point.VmName));
        await tracker.UpdateTaskAsync(task.Id, t => { t.State = SessionState.Running; t.StartedUtc = time.GetUtcNow(); }, CancellationToken.None).ConfigureAwait(false);

        try
        {
            await using var opened = await open(cancellationToken).ConfigureAwait(false);
            var reader = opened.Reader;
            var manifest = reader.Manifest;
            await CheckKindAsync(manifest, point, targetConnection, cancellationToken).ConfigureAwait(false);
            var resume = request.ResumeWithMemory;
            if (resume)
            {
                await CheckResumeAsync(manifest, point, targetConnection, cancellationToken).ConfigureAwait(false);
            }

            // A resume creates the VM from the snapshot with memory's own metadata: suspended, with the RAM image.
            var metadataBlob = resume ? manifest.MemoryMetadata! : manifest.Metadata ?? throw new InvalidDataException("The restore point has no VM metadata.");
            byte[] metadata;
            await using (var blob = await reader.OpenBlobAsync(metadataBlob, cancellationToken).ConfigureAwait(false))
            {
                using var memory = new MemoryStream();
                await blob.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                metadata = memory.ToArray();
            }

            var originalLocation = request.Mode == RestoreModeDto.OriginalLocation;
            // A namespace's volumes go to the StorageClasses the restore maps them to; only a class chosen for a disk is fixed here.
            var storage = manifest.Kubernetes is not null
                ? manifest.Disks.Select(d => d.Key).Concat(manifest.FileTrees.Select(t => t.Key)).Distinct(StringComparer.Ordinal)
                    .ToDictionary(k => k, k => request.DiskStorage.GetValueOrDefault(k) ?? string.Empty)
                : DiskStorage(manifest, request.DiskStorage, originalLocation: request.Mode == RestoreModeDto.OriginalLocation);
            if (resume)
            {
                // The RAM image goes where it is chosen, else next to the first disk (the host that resumes must reach it).
                storage[manifest.Memory!.Key] = request.DiskStorage.GetValueOrDefault(manifest.Memory.Key)
                    ?? manifest.Disks.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => storage[d.Key]).FirstOrDefault(s => s.Length > 0) ?? string.Empty;
            }

            var plan = new VmRestorePlan
            {
                Mode = request.Mode == RestoreModeDto.OriginalLocation ? RestoreMode.OriginalLocation : RestoreMode.AlternateLocation,
                Manifest = manifest,
                Metadata = new VmMetadataBlob(metadataBlob.Format, metadata),
                VmName = string.IsNullOrWhiteSpace(request.VmName) ? manifest.Vm.Name : request.VmName,
                TargetHostId = request.TargetHostId,
                DiskStorage = storage,
                Resume = resume,
                NetworkMap = request.NetworkMap,
                RegenerateMacs = request.Mode == RestoreModeDto.AlternateLocation && request.RegenerateMacs,
                PowerOn = request.PowerOn,
                ReplaceExisting = request.ReplaceExisting,
                DeleteReplaced = request.DeleteReplaced,
                OriginalVmIds = originalLocation && manifest.Kubernetes is null ? await OriginalVmIdsAsync(targetConnection, point.VmNativeId, cancellationToken).ConfigureAwait(false) : [],
                SessionId = session.Id,
                Kubernetes = manifest.Kubernetes is { } space
                    ? new KubernetesRestoreOptions
                    {
                        TargetNamespace = string.IsNullOrWhiteSpace(request.Kubernetes?.TargetNamespace) ? space.Namespace : request.Kubernetes.TargetNamespace.Trim(),
                        ReplaceExisting = request.Kubernetes?.ReplaceExisting == true,
                        OnlyClaims = request.Kubernetes?.OnlyClaims,
                        ReplaceClaims = request.Kubernetes?.ReplaceClaims == true,
                        Objects = request.Kubernetes?.Objects,
                        ExactObjects = request.Kubernetes?.ExactObjects == true,
                        ClaimNames = request.Kubernetes?.ClaimNames ?? [],
                        StorageClassMap = request.Kubernetes?.StorageClassMap ?? [],
                        OtherCluster = await IsOtherClusterAsync(point, targetConnection, cancellationToken).ConfigureAwait(false),
                    }
                    : null,
            };

            if (plan.Kubernetes is null)
            {
                log.Info($"Restoring {manifest.Vm.Name} from {manifest.CreatedUtc.ToLocalTime():g} to {(plan.Mode == RestoreMode.OriginalLocation ? "the original location" : "an alternate location")} as '{plan.VmName}'" +
                         (resume ? ", with its memory: it resumes where it was at the backup." : "."));
            }

            await using var hypervisor = await connections.OpenAsync(targetConnection, cancellationToken).ConfigureAwait(false);
            var ledger = new DbArtifactLedger(dbFactory, targetConnection, session.Id, null, time);
            await using var restore = await hypervisor.BeginRestoreAsync(plan, log, ledger, cancellationToken).ConfigureAwait(false);

            var validation = await restore.ValidateAsync(cancellationToken).ConfigureAwait(false);
            foreach (var warning in validation.Warnings)
            {
                log.Warning(warning);
            }

            if (!validation.Ok)
            {
                foreach (var error in validation.Errors)
                {
                    log.Error(error);
                }

                await FinishAsync(task.Id, SessionState.Failed, string.Join(" ", validation.Errors), 0).ConfigureAwait(false);
                return (SessionState.Failed, validation.Errors[0]);
            }

            var warnings = validation.Warnings.Count > 0;
            if (manifest.DomainController == true)
            {
                warnings |= DomainControllerNotes(manifest, plan, log);
            }

            // A namespace restore of selected objects restores only their volumes.
            var disks = manifest.Disks.Where(d => restore.RestoresDisk(d.Key)).ToList();

            // Volumes backed up file by file go into new volumes as files (see TreeVolumeCapacity).
            var trees = manifest.FileTrees.Where(t => restore.RestoresDisk(t.Key))
                .Select(t => (Tree: t, Capacity: TreeVolumeCapacity(manifest.Kubernetes?.Volumes.FirstOrDefault(v => v.TreeKey == t.Key)?.SizeBytes ?? 0, t.Stats.Bytes)))
                .ToList();
            var noun = plan.Kubernetes is null ? "Disk" : "Volume";
            var total = Math.Max(1, disks.Sum(d => d.CapacityBytes) + trees.Sum(t => t.Capacity) + (resume ? manifest.Memory!.CapacityBytes : 0));
            long done = 0;
            long transferred = 0;
            var transferStarted = time.GetUtcNow();
            var rate = new RateMeter();

            // Processed: disk bytes restored; Transferred (StoredBytes): bytes sent to the host. The current rate is the
            // network rate, as for backups.
            TransferMetricsDto Metrics(long bytes, long sent) => new()
            {
                TotalBytes = total,
                ProcessedBytes = bytes,
                ReadBytes = bytes,
                StoredBytes = sent,
                Seconds = Math.Round((time.GetUtcNow() - transferStarted).TotalSeconds, 3),
                CurrentBytesPerSecond = rate.Current,
            };
            // The metadata names each disk by its id at the backup: the VM's own disks, or for a resume the disks of the
            // snapshot with memory. Each of them maps to the restored disk.
            string MetadataId(string key, string sourceDiskId) => resume ? manifest.MemoryDiskIds![key] : sourceDiskId;
            var mapping = new Dictionary<string, string>();

            async Task<string> RestoreImageAsync(RestoreDiskTarget target, string label)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var writer = await restore.CreateDiskAsync(target, cancellationToken).ConfigureAwait(false);
                var start = done;
                var sentBefore = transferred;
                var lastUpdate = DateTimeOffset.MinValue;
                var progress = new Progress<long>(bytes =>
                {
                    log.Info($"{label}: {bytes * 100.0 / target.CapacityBytes:0}% restored", $"disk-{target.DiskKey}");
                    if (time.GetUtcNow() - lastUpdate > TimeSpan.FromSeconds(2))
                    {
                        lastUpdate = time.GetUtcNow();
                        var sent = sentBefore + writer.TransferredBytes;
                        rate.Sample(lastUpdate, sent);
                        var metrics = Metrics(start + bytes, sent);
                        _ = PushProgressAsync(task.Id, session.Id, Math.Round((start + bytes) * 100.0 / total, 1), start + bytes, metrics);
                    }
                });
                if (writer is IFileLevelDiskWriter files)
                {
                    // A volume into a StorageClass without snapshots: its files, read from the image's file system.
                    await FileRestore.VolumeFileSender.SendAsync(reader, cache, target.DiskKey, files, log, progress, cancellationToken).ConfigureAwait(false);
                }
                else if (manifest.FileTrees.Any(t => t.Key == target.DiskKey))
                {
                    throw new InvalidOperationException($"{label} was backed up file by file: it goes back as files only.");
                }
                else
                {
                    var image = await RestorePointDiskImage.OpenAsync(reader, target.DiskKey, cancellationToken).ConfigureAwait(false);
                    await writer.WriteAsync(image, progress, cancellationToken).ConfigureAwait(false);
                }

                done += target.CapacityBytes;
                transferred += writer.TransferredBytes;
                log.Info($"{label}: restored ({BackupFormat(target.CapacityBytes)}, {BackupFormat(writer.TransferredBytes)} transferred).", $"disk-{target.DiskKey}");
                return writer.DiskId;
            }

            foreach (var disk in disks)
            {
                mapping[MetadataId(disk.Key, disk.SourceDiskId)] = await RestoreImageAsync(
                    new RestoreDiskTarget(disk.Key, disk.SourceDiskId, disk.Name, disk.CapacityBytes, plan.DiskStorage[disk.Key]), $"{noun} {disk.Name}").ConfigureAwait(false);
            }

            foreach (var (tree, capacity) in trees)
            {
                var source = $"{manifest.Kubernetes!.Namespace}/{tree.Key}";
                mapping[source] = await RestoreImageAsync(new RestoreDiskTarget(tree.Key, source, tree.Name, capacity, plan.DiskStorage.GetValueOrDefault(tree.Key) ?? string.Empty), $"{noun} {tree.Name}")
                    .ConfigureAwait(false);
            }

            foreach (var disk in manifest.ExcludedDisks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                mapping[MetadataId(disk.Key, disk.SourceDiskId)] = await restore.CreateEmptyDiskAsync(
                    new RestoreDiskTarget(disk.Key, disk.SourceDiskId, disk.Name, disk.CapacityBytes, plan.DiskStorage[disk.Key]), cancellationToken).ConfigureAwait(false);
                log.Info($"Disk {disk.Name}: created empty ({BackupFormat(disk.CapacityBytes)}); it was left out of the backup.", $"disk-{disk.Key}");
            }

            if (resume && manifest.Memory is { } ram)
            {
                mapping[ram.SourceDiskId] = await RestoreImageAsync(
                    new RestoreDiskTarget(ram.Key, ram.SourceDiskId, $"{plan.VmName} memory image", ram.CapacityBytes, plan.DiskStorage[ram.Key], MemoryImage: true),
                    "Memory image").ConfigureAwait(false);
            }

            var created = await restore.CreateVmAsync(mapping, cancellationToken).ConfigureAwait(false);
            if (manifest.DomainController == true)
            {
                warnings |= CheckGenerationId(manifest, created, plan.VmName, log);
            }

            var restored = await restore.CompleteAsync(cancellationToken).ConfigureAwait(false);
            warnings |= restored.Warnings is { Count: > 0 };
            if (restored.ReplacedVmId is { } replaced && replaced != restored.VmId)
            {
                warnings |= await FollowRestoredVmAsync(session.Id, point.VmName, targetConnection, replaced, restored, log).ConfigureAwait(false);
            }

            var final = Metrics(done, transferred) with { CurrentBytesPerSecond = 0 };
            await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false))
            {
                var pool = await db.Connections.Where(c => c.Id == targetConnection).Select(c => c.DisplayName).FirstOrDefaultAsync(CancellationToken.None).ConfigureAwait(false);
                metrics.RecordRestoredBytes(pool ?? targetConnection.ToString(), transferred);
            }

            log.Info(
                $"Restore finished: {(plan.Kubernetes is null ? $"VM '{restored.Name}' ({restored.VmId})" : $"namespace {restored.Name}")}, {VmBackupTask.Format(done)} restored in {MetricsText.Duration(final.Seconds)} " +
                $"({MetricsText.Rate(final.ProcessingRate)}), {VmBackupTask.Format(transferred)} transferred ({MetricsText.Rate(transferred / Math.Max(0.001, final.Seconds))}).");
            // Whatever was warned about on the way (a file that could not be put in place, a volume made ReadWriteOnce …).
            warnings |= log.Warned;
            await FinishAsync(task.Id, warnings ? SessionState.Warning : SessionState.Succeeded, null, done, final).ConfigureAwait(false);
            return (warnings ? SessionState.Warning : SessionState.Succeeded, plan.Kubernetes is null ? $"Restored as '{restored.Name}'." : $"Restored into namespace {restored.Name}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.Warning("Stopped by user; everything restored so far was removed.");
            await FinishAsync(task.Id, SessionState.Stopped, "Stopped", 0).ConfigureAwait(false);
            return (SessionState.Stopped, "Stopped by user.");
        }
        catch (Exception ex)
        {
            log.Error($"Restore failed: {ex.Message}");
            await FinishAsync(task.Id, SessionState.Failed, ex.Message, 0).ConfigureAwait(false);
            return (SessionState.Failed, ex.Message);
        }
    }

    /// <summary>
    /// A restore point goes back to the kind of platform it was backed up from: its VM metadata (XAPI export, vSphere
    /// configuration, Kubernetes objects) only means something there.
    /// </summary>
    private async Task CheckKindAsync(RestorePointManifest manifest, RestorePointEntity point, Guid targetConnection, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var target = await db.Connections.AsNoTracking().Where(c => c.Id == targetConnection).Select(c => new { c.Kind, c.DisplayName })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The target connection {targetConnection} no longer exists.");
        if (target.Kind != manifest.Source.Provider)
        {
            throw new InvalidOperationException(
                $"The restore point of {point.VmName} from {manifest.CreatedUtc.ToLocalTime():g} was backed up from {ConnectionKinds.Platform(manifest.Source.Provider)}; " +
                $"it cannot be restored to '{target.DisplayName}', a {ConnectionKinds.Platform(target.Kind)} {ConnectionKinds.Noun(target.Kind)}. Choose a {ConnectionKinds.Platform(manifest.Source.Provider)} target.");
        }
    }

    /// <summary>
    /// A resume needs the RAM image, the snapshot with memory's metadata and its disk ids, the pool the VM was backed up in
    /// (its memory needs the same kind of CPUs), and no domain controller: XAPI keeps the snapshot's VM generation id, so
    /// Windows would continue the directory of the backup without noticing a restore (USN rollback).
    /// </summary>
    private async Task CheckResumeAsync(RestorePointManifest manifest, RestorePointEntity point, Guid targetConnection, CancellationToken cancellationToken)
    {
        // Only a pool can resume a snapshot with memory (VM.checkpoint).
        if (manifest.Source.Provider != HypervisorKind.XenServer)
        {
            throw new InvalidOperationException($"The restore point of {point.VmName} has no snapshot with memory to resume ({ConnectionKinds.Platform(manifest.Source.Provider)}): restore it without memory.");
        }

        var at = $"{point.VmName} from {manifest.CreatedUtc.ToLocalTime():g}";
        if (manifest.Memory is null || manifest.MemoryMetadata is null)
        {
            throw new InvalidOperationException($"The restore point of {at} has no memory image: it can only be booted from its disks.");
        }

        var missing = manifest.Disks.Select(d => d.Key).Concat(manifest.ExcludedDisks.Select(d => d.Key))
            .Where(k => manifest.MemoryDiskIds?.ContainsKey(k) != true).ToList();
        if (manifest.MemoryDiskIds is null || missing.Count > 0)
        {
            throw new InvalidOperationException(manifest.MemoryDiskIds is null
                ? $"The restore point of {at} was backed up with memory by a version that could not resume yet: it can only be booted from its disks. Back the VM up again to resume it."
                : $"The restore point of {at} does not name the disk(s) {string.Join(", ", missing)} in its snapshot with memory, so it cannot resume. Restore it without memory.");
        }

        // A restore point with memory has no guest details, so its agent and the VM's other restore points tell whether it is a DC.
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await DomainControllerEvidence.FindAsync(db, point, cancellationToken).ConfigureAwait(false) is { } evidence)
        {
            throw new InvalidOperationException($"{point.VmName} is a domain controller ({evidence}): resumed, it would continue its directory from the backup without Windows " +
                "noticing a restore (USN rollback). Restore it without memory: Windows then restores Active Directory non-authoritatively at its first start.");
        }

        if (point.ConnectionId != targetConnection)
        {
            var names = await db.Connections.Where(c => c.Id == targetConnection || c.Id == point.ConnectionId)
                .ToDictionaryAsync(c => c.Id, c => c.DisplayName, cancellationToken).ConfigureAwait(false);
            var source = point.ConnectionId is { } id ? $"'{names.GetValueOrDefault(id, id.ToString())}'" : "(unknown)";
            throw new InvalidOperationException($"{point.VmName} resumes only in the pool it was backed up in ({source}), not in '{names.GetValueOrDefault(targetConnection, targetConnection.ToString())}': " +
                "its memory needs the same kind of CPUs. Restore it to that pool, or without memory.");
        }
    }

    /// <summary>
    /// The ids the backed-up VM had at the original location, oldest first: its own, then each VM that replaced it at an
    /// earlier restore there. The restore treats the last one that still exists as the original.
    /// </summary>
    private async Task<IReadOnlyList<string>> OriginalVmIdsAsync(Guid connectionId, string vmId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<string> { vmId };
        while (ids.Count < 100)
        {
            var current = ids[^1];
            var next = await db.VmReplacements.Where(r => r.ConnectionId == connectionId && r.ReplacedVmId == current)
                .OrderByDescending(r => r.CreatedUtc).Select(r => r.VmId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (next is null || ids.Contains(next))
            {
                break;
            }

            ids.Add(next);
        }

        return ids;
    }

    /// <summary>
    /// A restore to the original location that replaced a VM got a new VM id (the original still held the old one). The
    /// catalog records the replacement, for restores of older restore points to the original location, and the replaced
    /// VM's jobs back up the restored one: otherwise their next run backs up the renamed original (lab check H13). The
    /// restored VM starts a new chain, so its first backup is a full one.
    /// </summary>
    /// <returns>True when the catalog could not be changed (a warning was logged).</returns>
    private async Task<bool> FollowRestoredVmAsync(Guid sessionId, string vmName, Guid connectionId, string replacedVmId, RestoredVm restored, IOperationLog log)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            db.VmReplacements.Add(new VmReplacementEntity
            {
                ConnectionId = connectionId, ReplacedVmId = replacedVmId, VmId = restored.VmId, CreatedUtc = time.GetUtcNow(), SessionId = sessionId,
            });
            var members = await db.JobObjects.Where(o => o.NativeId == replacedVmId)
                .Join(db.Jobs.Where(j => j.ConnectionId == connectionId), o => o.JobId, j => j.Id, (o, j) => new { Member = o, Job = j.Name })
                .ToListAsync(CancellationToken.None).ConfigureAwait(false);
            var jobIds = members.Select(m => m.Member.JobId).ToList();
            var already = await db.JobObjects.Where(o => jobIds.Contains(o.JobId) && o.NativeId == restored.VmId).Select(o => o.JobId)
                .ToListAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var member in members)
            {
                if (already.Contains(member.Member.JobId))
                {
                    db.JobObjects.Remove(member.Member);
                }
                else
                {
                    member.Member.NativeId = restored.VmId;
                    member.Member.CachedName = restored.Name;
                }
            }

            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var job in members.Select(m => m.Job).Order(StringComparer.OrdinalIgnoreCase))
            {
                log.Info($"Job '{job}' now backs up the restored VM '{restored.Name}' ({restored.VmId}) instead of the replaced one ({replacedVmId}); its next run is a full backup.");
            }

            return false;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            log.Warning($"The catalog could not record that '{restored.Name}' ({restored.VmId}) replaced {vmName} ({replacedVmId}): {ex.Message}. Its jobs still name the replaced VM: replace it with the restored one in each job by hand. A later restore of an older restore point to the original location would replace the renamed VM, not this one.");
            return true;
        }
    }

    private async Task PushProgressAsync(Guid taskId, Guid sessionId, double progress, long bytes, TransferMetricsDto metrics)
    {
        try
        {
            await tracker.UpdateTaskAsync(taskId, t => { t.Progress = progress; t.TransferredBytes = bytes; t.Metrics = metrics; }).ConfigureAwait(false);
            await tracker.RollUpAsync(sessionId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            // Progress is informational; the final state is written when the restore ends.
        }
    }

    private Task FinishAsync(Guid taskId, SessionState state, string? error, long bytes, TransferMetricsDto? metrics = null) =>
        tracker.UpdateTaskAsync(taskId, t =>
        {
            t.State = state;
            t.Error = error;
            t.EndedUtc = time.GetUtcNow();
            if (t.Metrics is { } running)
            {
                t.Metrics = running with { CurrentBytesPerSecond = 0 };
            }

            if (state is SessionState.Succeeded or SessionState.Warning)
            {
                t.Progress = 100;
                t.TransferredBytes = bytes;
                t.Metrics = metrics ?? t.Metrics;
            }
        }, CancellationToken.None);

    private static string BackupFormat(long bytes) => VmBackupTask.Format(bytes);

    /// <summary>
    /// The size of the new volume a file tree goes into: the PVC's, unless its files need more (an NFS PV's capacity often
    /// says little about what is on it) — then the files plus an eighth and 256 MiB for the file system's own needs, in whole GiB.
    /// </summary>
    internal static long TreeVolumeCapacity(long requested, long fileBytes)
    {
        const long GiB = 1L << 30;
        var needed = fileBytes + (fileBytes / 8) + (256L << 20);
        return requested >= needed ? requested : (needed + GiB - 1) / GiB * GiB;
    }

    /// <summary>
    /// A domain controller's VM comes back with a new VM generation id, so Windows restores AD non-authoritatively at its
    /// first start. A copy beside the original (another location) would meet it on the network with the same name and
    /// addresses: a warning if it is powered on.
    /// </summary>
    private static bool DomainControllerNotes(RestorePointManifest manifest, VmRestorePlan plan, IOperationLog log)
    {
        var domain = string.IsNullOrEmpty(manifest.AdDomain) ? "its domain" : $"domain {manifest.AdDomain}";
        log.Info($"{manifest.Vm.Name} was a domain controller of {domain}. At its first start Windows restores Active Directory non-authoritatively: " +
            "it takes a new invocation id, drops its RID pool and replicates the newer changes from the other domain controllers. Single objects come back without a VM restore under Restore → Active Directory objects.");
        if (plan.Mode != RestoreMode.OriginalLocation && plan.PowerOn)
        {
            log.Warning($"'{plan.VmName}' is a copy of the domain controller {manifest.Vm.Name} and is powered on: if the original still runs, both have the same computer name, " +
                "addresses and computer account in the network. Connect the copy only to an isolated network.");
            return true;
        }

        return false;
    }

    /// <summary>Without a new VM generation id Windows cannot tell that the DC was restored (risk of a USN rollback).</summary>
    /// <param name="name">The restored VM's name (<paramref name="created"/> still has the backed-up one).</param>
    private static bool CheckGenerationId(RestorePointManifest manifest, RestoredVm created, string name, IOperationLog log)
    {
        var before = manifest.Vm.GenerationId;
        if (!VmGenerationIds.IsSet(created.GenerationId))
        {
            var cause = VmGenerationIds.IsSet(before) ? string.Empty
                : $" The backed-up VM {manifest.Vm.Name} had none either (created from a template without one), so the hypervisor gives none to its copies.";
            log.Warning($"The restored VM '{name}' ({created.VmId}) has no VM generation id, so Windows cannot tell that this domain controller was restored " +
                $"(risk of a USN rollback).{cause} Start it without network and follow \"Restoring a whole domain controller\" in the admin guide before connecting it.");
            return true;
        }

        if (before is not null && string.Equals(before, created.GenerationId, StringComparison.Ordinal))
        {
            log.Warning($"The restored VM '{name}' ({created.VmId}) kept the VM generation id of the backup ({before}), so Windows cannot tell that this domain controller was restored " +
                "(risk of a USN rollback). Start it without network and follow \"Restoring a whole domain controller\" in the admin guide before connecting it.");
            return true;
        }

        log.Info($"The restored VM '{name}' has a new VM generation id ({created.GenerationId}{(before is null ? string.Empty : $", backup: {before}")}).");
        return false;
    }

    /// <summary>
    /// Target storage per disk: the chosen one, else the original storage. Disks that were left out of the backup are
    /// created empty: on the storage chosen for them, else on their original storage (restore to the original location),
    /// else next to the first restored disk (another location: where the user put the VM).
    /// </summary>
    /// <summary>Whether the target connection is another cluster than the restore point's (by the cluster's platform id: one cluster may be added twice).</summary>
    private async Task<bool> IsOtherClusterAsync(RestorePointEntity point, Guid targetConnection, CancellationToken cancellationToken)
    {
        if (targetConnection == point.ConnectionId)
        {
            return false;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var platform = await db.Connections.AsNoTracking().Where(c => c.Id == targetConnection).Select(c => c.PlatformId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return platform is null || !string.Equals(platform, point.PlatformId, StringComparison.Ordinal);
    }

    internal static Dictionary<string, string> DiskStorage(RestorePointManifest manifest, IReadOnlyDictionary<string, string> chosen, bool originalLocation)
    {
        var storage = manifest.Disks.ToDictionary(d => d.Key, d => chosen.GetValueOrDefault(d.Key) ?? d.StorageId ?? string.Empty);
        var first = manifest.Disks.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => storage[d.Key]).FirstOrDefault(s => s.Length > 0);
        foreach (var disk in manifest.ExcludedDisks)
        {
            storage[disk.Key] = chosen.GetValueOrDefault(disk.Key) ?? (originalLocation ? disk.StorageId : null) ?? first ?? string.Empty;
        }

        return storage;
    }
}

/// <param name="ConvertToPacks">Switch a format 1 repository to pack files before the garbage collection.</param>
/// <param name="PackTargetSize">Size of the packs, in bytes.</param>
public sealed record MaintenanceRequest(bool ConvertToPacks, long PackTargetSize);

/// <summary>
/// Garbage collection of a repository (after retention or on demand); in a repository with pack files also the
/// packing of loose chunks left from format 1, in runs of limited length.
/// </summary>
public sealed class MaintenanceSessionRunner(
    RepositoryManager repositories,
    SessionEventWriter events,
    SessionQueue queue,
    IDbContextFactory<AppDbContext> dbFactory,
    Microsoft.Extensions.Options.IOptions<EngineOptions> options,
    Telemetry.BackupMetrics metrics)
{
    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var log = new WarningTrackingLog(events.CreateLog(session.Id, null));
        var repositoryId = session.RepositoryId ?? throw new InvalidOperationException("No repository.");
        var repository = await repositories.GetAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        var request = session.Parameters is { Length: > 0 } json ? JsonSerializer.Deserialize<MaintenanceRequest>(json) : null;
        var local = repository as Backdafuckup.Storage.LocalDirectoryRepository;
        var parts = new List<string>();

        if (request is { ConvertToPacks: true } && local is { FormatVersion: 1 })
        {
            log.Info(
                $"Converting '{local.Root}' to pack files of {VmBackupTask.Format(request.PackTargetSize)}. New chunks go into packs from now on; " +
                "the existing ones are packed below. Versions of backdafuckup older than this one can no longer open the repository.");
            await local.UpgradeToPacksAsync(request.PackTargetSize, cancellationToken).ConfigureAwait(false);
            parts.Add("converted to pack files");
        }

        log.Info("Removing data that is no longer referenced by any restore point.");
        var progress = new Progress<string>(message => log.Info(message, "gc"));
        var result = await repository.CollectGarbageAsync(progress, cancellationToken).ConfigureAwait(false);
        var gc = $"{result.DeletedChunks} unreferenced chunk(s) removed, {VmBackupTask.Format(result.FreedBytes)} freed";
        if (result.PacksDeleted > 0 || result.BytesRepacked > 0)
        {
            gc += $" ({result.PacksDeleted} pack(s) deleted, {VmBackupTask.Format(result.BytesRepacked)} of chunks still in use repacked)";
        }

        log.Info(gc + ".", "gc");
        parts.Add(gc);
        var repositoryName = await RepositoryNameAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        metrics.RecordPacks(repositoryName, result.BytesRepacked, 0);

        if (local is { FormatVersion: 2, LooseChunksMayExist: true })
        {
            var max = options.Value.PackLooseMaxDuration;
            log.Info($"Packing the loose chunks in '{local.Root}' that restore points and SQL backups use, newest first (at most {max.TotalHours:0.#} h in this run).");
            var packing = await local.PackLooseChunksAsync(max, new Progress<string>(message => log.Info(message, "pack")), cancellationToken).ConfigureAwait(false);
            metrics.RecordPacks(repositoryName, 0, packing.Packed);
            var text = $"{packing.Packed} loose chunk(s) packed";
            if (packing.RemovedDuplicates > 0)
            {
                text += $", {packing.RemovedDuplicates} loose copies of packed chunks removed";
            }

            if (packing.KeptDamaged > 0)
            {
                log.Warning($"{packing.KeptDamaged} loose chunk file(s) in '{local.Root}' could not be read and stay where they are. Verify the restore points to see which are affected.");
            }

            if (!packing.Finished)
            {
                log.Info($"{text}. Not finished in this run; the next maintenance run continues, it is queued now.");
                await queue.EnqueueMaintenanceAsync(repositoryId, repositoryName, "Packing continues", CancellationToken.None).ConfigureAwait(false);
            }
            else if (packing.LooseChunksRemain)
            {
                log.Info($"{text}. The loose files left are not referenced; the next garbage collection removes them.");
            }
            else
            {
                log.Info($"{text}. '{local.Root}' has no loose chunks any more.");
            }

            parts.Add(text);
        }

        var summary = string.Join("; ", parts) + ".";
        return (log.Warned ? SessionState.Warning : SessionState.Succeeded, summary);
    }

    private async Task<string> RepositoryNameAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Repositories.Where(r => r.Id == repositoryId).Select(r => r.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? repositoryId.ToString();
    }
}

/// <summary>An opened restore point and what keeps it (a pin against retention), released together.</summary>
internal sealed class OpenedRestorePoint(IRestorePointReader reader, IDisposable? keep) : IAsyncDisposable
{
    public IRestorePointReader Reader => reader;

    public async ValueTask DisposeAsync()
    {
        await reader.DisposeAsync().ConfigureAwait(false);
        keep?.Dispose();
    }
}
