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
                DiskStorage = manifest.Disks.ToDictionary(d => d.Key, d => request.DiskStorage.GetValueOrDefault(d.Key) ?? d.StorageId ?? string.Empty),
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

            var total = Math.Max(1, manifest.Disks.Sum(d => d.CapacityBytes));
            long done = 0;
            var mapping = new Dictionary<string, string>();
            foreach (var disk in manifest.Disks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var writer = await restore.CreateDiskAsync(new RestoreDiskTarget(disk.Key, disk.SourceDiskId, disk.Name, disk.CapacityBytes, plan.DiskStorage[disk.Key]), cancellationToken).ConfigureAwait(false);
                var image = await RestorePointDiskImage.OpenAsync(reader, disk.Key, cancellationToken).ConfigureAwait(false);
                var start = done;
                var lastUpdate = DateTimeOffset.MinValue;
                var progress = new Progress<long>(bytes =>
                {
                    log.Info($"Disk {disk.Name}: {bytes * 100.0 / disk.CapacityBytes:0}% restored", $"disk-{disk.Key}");
                    if (time.GetUtcNow() - lastUpdate > TimeSpan.FromSeconds(2))
                    {
                        lastUpdate = time.GetUtcNow();
                        _ = tracker.UpdateTaskAsync(task.Id, t => { t.Progress = Math.Round((start + bytes) * 100.0 / total, 1); t.TransferredBytes = start + bytes; });
                    }
                });
                await writer.WriteAsync(image, progress, cancellationToken).ConfigureAwait(false);
                done += disk.CapacityBytes;
                mapping[disk.SourceDiskId] = writer.DiskId;
                log.Info($"Disk {disk.Name}: restored ({BackupFormat(disk.CapacityBytes)}).", $"disk-{disk.Key}");
            }

            await restore.CreateVmAsync(mapping, cancellationToken).ConfigureAwait(false);
            var restored = await restore.CompleteAsync(cancellationToken).ConfigureAwait(false);
            log.Info($"Restore finished: VM '{restored.Name}' ({restored.VmId}).");
            await FinishAsync(task.Id, SessionState.Succeeded, null, done).ConfigureAwait(false);
            return (validation.Warnings.Count > 0 ? SessionState.Warning : SessionState.Succeeded, $"Restored as '{restored.Name}'.");
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

    private Task FinishAsync(Guid taskId, SessionState state, string? error, long bytes) =>
        tracker.UpdateTaskAsync(taskId, t =>
        {
            t.State = state;
            t.Error = error;
            t.EndedUtc = time.GetUtcNow();
            if (state == SessionState.Succeeded)
            {
                t.Progress = 100;
                t.TransferredBytes = bytes;
            }
        }, CancellationToken.None);

    private static string BackupFormat(long bytes) => VmBackupTask.Format(bytes);
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
