using System.Text.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.DataMover;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Engine;

/// <summary>Restores a whole VM from a restore point to the original or an alternate location.</summary>
public sealed class RestoreSessionRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    ConnectionManager connections,
    RepositoryManager repositories,
    SessionEventWriter events,
    SessionTracker tracker,
    Telemetry.BackupMetrics metrics,
    TimeProvider time)
{
    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<RestoreRequestDto>(session.Parameters ?? "null")
            ?? throw new InvalidOperationException("The restore session has no parameters.");
        RestorePointEntity point;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            point = await db.RestorePoints.Include(r => r.Disks).AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == request.RestorePointId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The restore point no longer exists.");
        }

        var targetConnection = request.TargetConnectionId ?? point.ConnectionId
            ?? throw new InvalidOperationException("No target pool selected.");
        var task = await tracker.AddTaskAsync(session.Id, point.VmNativeId, point.VmName, cancellationToken).ConfigureAwait(false);
        var log = events.CreateLog(session.Id, task.Id, point.VmName);
        await tracker.UpdateTaskAsync(task.Id, t => { t.State = SessionState.Running; t.StartedUtc = time.GetUtcNow(); }, CancellationToken.None).ConfigureAwait(false);

        try
        {
            var repository = await repositories.GetAsync(point.RepositoryId, cancellationToken).ConfigureAwait(false);
            using var pin = repository.Pin(point.Id);
            await using var reader = await repository.OpenRestorePointAsync(point.Id, cancellationToken).ConfigureAwait(false);
            var manifest = reader.Manifest;
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
            var storage = DiskStorage(manifest, request.DiskStorage, originalLocation: request.Mode == RestoreModeDto.OriginalLocation);
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
                OriginalVmIds = originalLocation ? await OriginalVmIdsAsync(targetConnection, point.VmNativeId, cancellationToken).ConfigureAwait(false) : [],
                SessionId = session.Id,
            };

            log.Info($"Restoring {manifest.Vm.Name} from {manifest.CreatedUtc.ToLocalTime():g} to {(plan.Mode == RestoreMode.OriginalLocation ? "the original location" : "an alternate location")} as '{plan.VmName}'" +
                     (resume ? ", with its memory: it resumes where it was at the backup." : "."));
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

            var total = Math.Max(1, manifest.Disks.Sum(d => d.CapacityBytes) + (resume ? manifest.Memory!.CapacityBytes : 0));
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
                var image = await RestorePointDiskImage.OpenAsync(reader, target.DiskKey, cancellationToken).ConfigureAwait(false);
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
                await writer.WriteAsync(image, progress, cancellationToken).ConfigureAwait(false);
                done += target.CapacityBytes;
                transferred += writer.TransferredBytes;
                log.Info($"{label}: restored ({BackupFormat(target.CapacityBytes)}, {BackupFormat(writer.TransferredBytes)} transferred).", $"disk-{target.DiskKey}");
                return writer.DiskId;
            }

            foreach (var disk in manifest.Disks)
            {
                mapping[MetadataId(disk.Key, disk.SourceDiskId)] = await RestoreImageAsync(
                    new RestoreDiskTarget(disk.Key, disk.SourceDiskId, disk.Name, disk.CapacityBytes, plan.DiskStorage[disk.Key]), $"Disk {disk.Name}").ConfigureAwait(false);
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
                $"Restore finished: VM '{restored.Name}' ({restored.VmId}), {VmBackupTask.Format(done)} restored in {MetricsText.Duration(final.Seconds)} " +
                $"({MetricsText.Rate(final.ProcessingRate)}), {VmBackupTask.Format(transferred)} transferred ({MetricsText.Rate(transferred / Math.Max(0.001, final.Seconds))}).");
            await FinishAsync(task.Id, SessionState.Succeeded, null, done, final).ConfigureAwait(false);
            return (warnings ? SessionState.Warning : SessionState.Succeeded, $"Restored as '{restored.Name}'.");
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
    /// A resume needs the RAM image, the snapshot with memory's metadata and its disk ids, the pool the VM was backed up in
    /// (its memory needs the same kind of CPUs), and no domain controller: XAPI keeps the snapshot's VM generation id, so
    /// Windows would continue the directory of the backup without noticing a restore (USN rollback).
    /// </summary>
    private async Task CheckResumeAsync(RestorePointManifest manifest, RestorePointEntity point, Guid targetConnection, CancellationToken cancellationToken)
    {
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

        if (manifest.DomainController == true)
        {
            throw new InvalidOperationException($"{point.VmName} was a domain controller: resumed, it would continue its directory from the backup without Windows noticing a restore " +
                "(USN rollback). Restore it without memory: Windows then restores Active Directory non-authoritatively at its first start.");
        }

        if (point.ConnectionId != targetConnection)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
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

            if (state == SessionState.Succeeded)
            {
                t.Progress = 100;
                t.TransferredBytes = bytes;
                t.Metrics = metrics ?? t.Metrics;
            }
        }, CancellationToken.None);

    private static string BackupFormat(long bytes) => VmBackupTask.Format(bytes);

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

/// <summary>Garbage collection of a repository (after retention or on demand).</summary>
public sealed class MaintenanceSessionRunner(RepositoryManager repositories, SessionEventWriter events)
{
    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var log = events.CreateLog(session.Id, null);
        var repository = await repositories.GetAsync(session.RepositoryId ?? throw new InvalidOperationException("No repository."), cancellationToken).ConfigureAwait(false);
        log.Info("Removing data that is no longer referenced by any restore point.");
        var progress = new Progress<string>(message => log.Info(message, "gc"));
        var result = await repository.CollectGarbageAsync(progress, cancellationToken).ConfigureAwait(false);
        var summary = $"{result.DeletedChunks} unreferenced chunk(s) removed, {VmBackupTask.Format(result.FreedBytes)} freed.";
        log.Info(summary, "gc");
        return (SessionState.Succeeded, summary);
    }
}
