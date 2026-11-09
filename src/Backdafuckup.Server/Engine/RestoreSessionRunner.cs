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
            byte[] metadata;
            await using (var blob = await reader.OpenBlobAsync(manifest.Metadata ?? throw new InvalidDataException("The restore point has no VM metadata."), cancellationToken).ConfigureAwait(false))
            {
                using var memory = new MemoryStream();
                await blob.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                metadata = memory.ToArray();
            }

            var plan = new VmRestorePlan
            {
                Mode = request.Mode == RestoreModeDto.OriginalLocation ? RestoreMode.OriginalLocation : RestoreMode.AlternateLocation,
                Manifest = manifest,
                Metadata = new VmMetadataBlob(manifest.Metadata.Format, metadata),
                VmName = string.IsNullOrWhiteSpace(request.VmName) ? manifest.Vm.Name : request.VmName,
                TargetHostId = request.TargetHostId,
                DiskStorage = DiskStorage(manifest, request.DiskStorage, originalLocation: request.Mode == RestoreModeDto.OriginalLocation),
                NetworkMap = request.NetworkMap,
                RegenerateMacs = request.Mode == RestoreModeDto.AlternateLocation && request.RegenerateMacs,
                PowerOn = request.PowerOn,
                ReplaceExisting = request.ReplaceExisting,
                DeleteReplaced = request.DeleteReplaced,
                SessionId = session.Id,
            };

            log.Info($"Restoring {manifest.Vm.Name} from {manifest.CreatedUtc.ToLocalTime():g} to {(plan.Mode == RestoreMode.OriginalLocation ? "the original location" : "an alternate location")} as '{plan.VmName}'.");
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

            var total = Math.Max(1, manifest.Disks.Sum(d => d.CapacityBytes));
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
            var mapping = new Dictionary<string, string>();
            foreach (var disk in manifest.Disks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var writer = await restore.CreateDiskAsync(new RestoreDiskTarget(disk.Key, disk.SourceDiskId, disk.Name, disk.CapacityBytes, plan.DiskStorage[disk.Key]), cancellationToken).ConfigureAwait(false);
                var image = await RestorePointDiskImage.OpenAsync(reader, disk.Key, cancellationToken).ConfigureAwait(false);
                var start = done;
                var sentBefore = transferred;
                var lastUpdate = DateTimeOffset.MinValue;
                var progress = new Progress<long>(bytes =>
                {
                    log.Info($"Disk {disk.Name}: {bytes * 100.0 / disk.CapacityBytes:0}% restored", $"disk-{disk.Key}");
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
                done += disk.CapacityBytes;
                transferred += writer.TransferredBytes;
                mapping[disk.SourceDiskId] = writer.DiskId;
                log.Info($"Disk {disk.Name}: restored ({BackupFormat(disk.CapacityBytes)}, {BackupFormat(writer.TransferredBytes)} transferred).", $"disk-{disk.Key}");
            }

            foreach (var disk in manifest.ExcludedDisks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                mapping[disk.SourceDiskId] = await restore.CreateEmptyDiskAsync(
                    new RestoreDiskTarget(disk.Key, disk.SourceDiskId, disk.Name, disk.CapacityBytes, plan.DiskStorage[disk.Key]), cancellationToken).ConfigureAwait(false);
                log.Info($"Disk {disk.Name}: created empty ({BackupFormat(disk.CapacityBytes)}); it was left out of the backup.", $"disk-{disk.Key}");
            }

            var created = await restore.CreateVmAsync(mapping, cancellationToken).ConfigureAwait(false);
            if (manifest.DomainController == true)
            {
                warnings |= CheckGenerationId(manifest, created, plan.VmName, log);
            }

            var restored = await restore.CompleteAsync(cancellationToken).ConfigureAwait(false);
            var final = Metrics(done, transferred) with { CurrentBytesPerSecond = 0 };
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
