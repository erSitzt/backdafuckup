using System.Text.Json;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.ScaleOut;

/// <summary>A chain to move: a job's data of one object (job null: restore points of no job, moved one by one).</summary>
public sealed record ChainToMove(Guid ScaleOutId, Guid? JobId, string ObjectNativeId, string ObjectName, Guid FromExtentId, Guid ToExtentId);

/// <summary>What a move did: points and SQL Server backups switched, bytes copied; deferred when the chain's backup kept running.</summary>
/// <summary>
/// What a chain takes: <see cref="Footprint"/> is the stored size of every chunk its restore points and SQL Server backups
/// use on the source extent (shared ones included); <see cref="ToCopy"/> the part of it a target extent does not have.
/// </summary>
public sealed record ChainSize(long Footprint, long ToCopy);

public sealed record ChainMoveResult(int Points, int SqlBackups, long BytesCopied, bool Deferred, string Summary);

/// <summary>
/// Moves one chain between two extents of a scale-out repository, keeping every id:
/// <list type="number">
/// <item>copies its restore points oldest first without the chain's lease (only chunks the target lacks are read; records
/// are stored unchanged and hash-checked), each committed on the target and journalled as copied;</item>
/// <item>copies and switches its SQL Server backups one by one (they depend on no other data);</item>
/// <item>takes the chain's lease (waiting for a running backup of the chain), copies what was written meanwhile, and
/// switches the catalog rows with compare-and-set, so a point deleted meanwhile is not switched but abandoned;</item>
/// <item>deletes the source copies (pinned or busy ones are left for the balancer's cleanup) and queues garbage collection.</item>
/// </list>
/// A crash before the switch leaves the catalog unchanged and the copies journalled for the next run; after it, the
/// journal names the source copies still to delete.
/// </summary>
public sealed class ChainMover(
    IDbContextFactory<AppDbContext> dbFactory,
    RepositoryManager repositories,
    RestorePointAccess access,
    ChainLocks locks,
    SessionQueue queue,
    Sql.SqlBackupService sqlBackups,
    IVolumeSpace volumes,
    TimeProvider time)
{
    private static readonly TimeSpan DeleteWait = TimeSpan.FromSeconds(5);

    /// <summary>Test hook: runs under the chain's lease right before the switch transaction.</summary>
    internal Func<ChainToMove, Task>? BeforeSwitch { get; set; }

    /// <summary>Test hook: runs after the switch committed, before the source copies are deleted.</summary>
    internal Func<ChainToMove, Task>? AfterSwitch { get; set; }

    /// <summary>How long a SQL Server run of the VM that started before the switch is awaited before its backups are moved.</summary>
    internal TimeSpan SqlStragglerWait { get; set; } = TimeSpan.FromMinutes(10);

    public async Task<ChainMoveResult> MoveAsync(
        ChainToMove chain, Guid sessionId, ScaleOutSettings settings, IOperationLog log, Action<long, long>? progress, CancellationToken cancellationToken)
    {
        if (chain.FromExtentId == chain.ToExtentId)
        {
            throw new InvalidOperationException($"{chain.ObjectName} is on that extent already.");
        }

        var (source, sourceName) = await OpenAsync(chain.FromExtentId, cancellationToken).ConfigureAwait(false);
        var (target, targetName) = await OpenAsync(chain.ToExtentId, cancellationToken).ConfigureAwait(false);
        var targetRow = await CheckTargetAsync(chain, cancellationToken).ConfigureAwait(false);
        var throttle = new Throttle(settings.MaxMoveMBps, time);

        // What the target lacks, measured: a chain shares chunks with others (dedupe), so its own new data says little.
        var size = await MeasureAsync(chain.JobId, chain.ObjectNativeId, chain.FromExtentId, chain.ToExtentId, cancellationToken).ConfigureAwait(false);
        if (volumes.Probe(targetRow.Path) is { } space)
        {
            var reserve = space.Total / 100 * settings.NewChainMinFreePercent;
            if (size.ToCopy > space.Free - reserve)
            {
                throw new InvalidOperationException(
                    $"{targetName} ({targetRow.Path}) has {VmBackupTask.Format(space.Free)} free; {chain.ObjectName} needs {VmBackupTask.Format(size.ToCopy)} there, " +
                    $"and {VmBackupTask.Format(reserve)} ({settings.NewChainMinFreePercent} %) stay free for new chains: not moved.");
            }
        }

        var total = size.ToCopy;
        long copied = 0;
        void Copied(long bytes)
        {
            copied += bytes;
            progress?.Invoke(Math.Min(copied, Math.Max(total, copied)), Math.Max(total, copied));
        }

        await MarkMovingAsync(chain, sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            log.Info($"Moving {Describe(chain)} from extent {sourceName} to {targetName}: {VmBackupTask.Format(size.ToCopy)} to copy" +
                (size.Footprint > size.ToCopy ? $" ({VmBackupTask.Format(size.Footprint)} in all; {targetName} has the rest already)." : "."));

            // 1. Restore points, oldest first, without the lease; again while each pass still finds more than one new point.
            for (var pass = 0; pass < 5; pass++)
            {
                if (await CopyPassAsync(chain, sessionId, source, target, throttle, Copied, log, cancellationToken).ConfigureAwait(false) <= 1)
                {
                    break;
                }
            }

            // 2. SQL Server backups: copied and switched one by one.
            var sql = await MoveSqlBackupsAsync(chain, sessionId, source, target, throttle, Copied, log, cancellationToken).ConfigureAwait(false);

            // 3. Under the chain's lease: what its backups wrote meanwhile, then the switch.
            var points = 0;
            if (chain.JobId is { } jobId)
            {
                IAsyncDisposable lease;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    wait.CancelAfter(TimeSpan.FromMinutes(settings.MoveSwitchWaitMinutes));
                    try
                    {
                        lease = await locks.For(jobId, chain.ObjectNativeId)
                            .AcquireAsync($"Move of {chain.ObjectName} to extent {targetName}", holder => log.Info($"{chain.ObjectName}: waiting for '{holder}' before switching the chain."), wait.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        var deferred = $"{chain.ObjectName}: its backup ran longer than {settings.MoveSwitchWaitMinutes} minute(s); the chain stays on {sourceName}, its copies on {targetName} are reused by the next move.";
                        log.Warning(deferred);
                        return new ChainMoveResult(0, sql, copied, true, deferred);
                    }
                }

                await using (lease.ConfigureAwait(false))
                {
                    await CopyPassAsync(chain, sessionId, source, target, throttle, Copied, log, cancellationToken).ConfigureAwait(false);
                    if (BeforeSwitch is { } before)
                    {
                        await before(chain).ConfigureAwait(false);
                    }

                    points = await SwitchAsync(chain, sessionId, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                points = await SwitchAsync(chain, sessionId, cancellationToken).ConfigureAwait(false);
            }

            if (AfterSwitch is { } after)
            {
                await after(chain).ConfigureAwait(false);
            }

            // 4. The source copies, then what SQL Server runs that started before the switch still wrote there.
            await CleanUpAsync(chain.FromExtentId, chain.ToExtentId, cancellationToken).ConfigureAwait(false);
            if (chain.JobId is { } job && sqlBackups.RunningFor(job, chain.ObjectNativeId) is { } running)
            {
                log.Info($"{chain.ObjectName}: waiting for its SQL Server backup run to finish, then its backups follow the chain.");
                await Task.WhenAny(running, Task.Delay(SqlStragglerWait, time, cancellationToken)).ConfigureAwait(false);
            }

            sql += await MoveSqlBackupsAsync(chain, sessionId, source, target, throttle, Copied, log, cancellationToken).ConfigureAwait(false);
            var left = await CleanUpAsync(chain.FromExtentId, chain.ToExtentId, cancellationToken).ConfigureAwait(false);
            await QueueGcAsync(chain.FromExtentId, cancellationToken).ConfigureAwait(false);

            var summary = $"{Describe(chain)}: {points} restore point(s) and {sql} SQL Server backup(s) moved from {sourceName} to {targetName}, {VmBackupTask.Format(copied)} copied" +
                (left > 0 ? $"; {left} source cop(ies) still in use are deleted later." : ".");
            log.Info(summary);
            return new ChainMoveResult(points, sql, copied, false, summary);
        }
        finally
        {
            await UnmarkMovingAsync(chain, sessionId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes what moves left behind: source copies of switched objects (once nobody reads them) and target copies of
    /// abandoned ones. Returns how many are still waiting (pinned or busy).
    /// </summary>
    public async Task<int> CleanUpAsync(Guid? extentId, Guid? otherExtentId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var items = await db.RepositoryMoveItems
            .Where(m => (m.State == MoveItemState.Switched || m.State == MoveItemState.Abandoned)
                && (extentId == null || m.SourceExtentId == extentId || m.TargetExtentId == extentId || m.SourceExtentId == otherExtentId || m.TargetExtentId == otherExtentId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var waiting = 0;
        var collect = new HashSet<Guid>();
        foreach (var item in items)
        {
            var holder = item.State == MoveItemState.Switched ? item.SourceExtentId : item.TargetExtentId;
            DeleteOutcome outcome;
            try
            {
                var repository = (IRepositoryTransfer)await repositories.GetAsync(holder, cancellationToken).ConfigureAwait(false);
                outcome = item.ObjectType == MoveItemType.RestorePoint
                    ? await repository.TryDeleteRestorePointAsync(item.ObjectId, DeleteWait, cancellationToken).ConfigureAwait(false)
                    : await repository.TryDeleteSqlBackupAsync(item.ObjectId, DeleteWait, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException)
            {
                // The extent is unavailable (maintenance, offline, removed): later.
                item.Error = ex.Message;
                waiting++;
                continue;
            }

            if (outcome is DeleteOutcome.Pinned or DeleteOutcome.Busy)
            {
                waiting++;
                continue;
            }

            item.State = item.State == MoveItemState.Switched ? MoveItemState.SourceDeleted : MoveItemState.TargetDeleted;
            item.UpdatedUtc = time.GetUtcNow();
            collect.Add(holder);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in collect.Where(id => id != extentId))
        {
            await QueueGcAsync(id, cancellationToken).ConfigureAwait(false);
        }

        return waiting;
    }

    /// <summary>One pass over the chain's points on the source: copies those the target lacks; returns how many it copied.</summary>
    private async Task<int> CopyPassAsync(
        ChainToMove chain, Guid sessionId, IRepositoryTransfer source, IRepositoryTransfer target, Throttle throttle, Action<long> copied, IOperationLog log, CancellationToken cancellationToken)
    {
        List<RestorePointEntity> points;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            points = await ChainPoints(db, chain).Where(r => r.RepositoryId == chain.FromExtentId && r.State == RestorePointState.Available)
                .OrderBy(r => r.CreatedUtc).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var count = 0;
        foreach (var point in points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await JournalledAsync(point.Id, chain.ToExtentId, MoveItemState.Copied, cancellationToken).ConfigureAwait(false) && target.ContainsRestorePoint(point.Id))
            {
                continue;
            }

            PinnedObject pinned;
            try
            {
                pinned = await access.PinRestorePointAsync(point.Id, chain.FromExtentId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
            {
                continue; // deleted by retention, or moved, meanwhile
            }

            using (pinned)
            {
                if (pinned.RepositoryId != chain.FromExtentId)
                {
                    continue;
                }

                var (bytes, path) = await CopyPointAsync(point, source, target, throttle, copied, cancellationToken).ConfigureAwait(false);
                await JournalAsync(chain, sessionId, MoveItemType.RestorePoint, point.Id, bytes, path, cancellationToken).ConfigureAwait(false);
                log.Info($"{chain.ObjectName}: restore point of {point.CreatedUtc.ToLocalTime():g} copied ({VmBackupTask.Format(bytes)} new on the target).", $"move-{chain.JobId}-{chain.ObjectNativeId}");
                count++;
            }
        }

        return count;
    }

    /// <summary>Copies one pinned restore point with its id; reuses an identical copy a crashed move left. Returns the bytes copied.</summary>
    private async Task<(long Bytes, string? Path)> CopyPointAsync(RestorePointEntity point, IRepositoryTransfer source, IRepositoryTransfer target, Throttle throttle, Action<long> copied, CancellationToken cancellationToken)
    {
        var files = await source.ReadRestorePointFilesAsync(point.Id, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize(files[0].Content, CoreJsonContext.Default.RestorePointManifest)
            ?? throw new InvalidDataException($"The manifest of restore point {point.Id} cannot be read.");
        if (target.ContainsRestorePoint(point.Id))
        {
            var existing = await ((IBackupRepository)target).OpenRestorePointAsync(point.Id, cancellationToken).ConfigureAwait(false);
            await using (existing.ConfigureAwait(false))
            {
                if (JsonSerializer.Serialize(existing.Manifest with { RepositoryId = manifest.RepositoryId }, CoreJsonContext.Default.RestorePointManifest)
                    == JsonSerializer.Serialize(manifest, CoreJsonContext.Default.RestorePointManifest))
                {
                    return (0, null);
                }
            }

            await target.TryDeleteRestorePointAsync(point.Id, DeleteWait, cancellationToken).ConfigureAwait(false);
        }

        var ids = await source.ListRestorePointChunksAsync(point.Id, cancellationToken).ConfigureAwait(false);
        long bytes = 0;
        string path;
        await using (var import = target.BeginImport(point.Id, manifest))
        {
            var missing = ids.Where(id => !import.TryReuseChunk(id)).ToList();
            var locator = (IChunkLocator)source;
            var plan = locator.PlanRead(missing);
            if (plan.Missing.Count > 0)
            {
                throw new InvalidDataException($"{plan.Missing.Count} chunk(s) of the restore point of {point.VmName} from {point.CreatedUtc.ToLocalTime():g} are missing on the source extent; verify it.");
            }

            await foreach (var chunk in locator.ReadStoredAsync(plan, cancellationToken).ConfigureAwait(false))
            {
                await import.PutEncodedChunkAsync(chunk.Id, chunk.Record, cancellationToken).ConfigureAwait(false);
                bytes += chunk.Record.Length;
                copied(chunk.Record.Length);
                await throttle.WaitAsync(chunk.Record.Length, cancellationToken).ConfigureAwait(false);
            }

            foreach (var file in files.Skip(1))
            {
                await import.WriteFileAsync(file.Name, file.Content, cancellationToken).ConfigureAwait(false);
            }

            path = (await import.CommitAsync(null, cancellationToken).ConfigureAwait(false)).Path;
        }

        var verify = await ((IBackupRepository)target).VerifyAsync(point.Id, VerifyDepth.Quick, null, cancellationToken).ConfigureAwait(false);
        if (!verify.Ok)
        {
            await target.TryDeleteRestorePointAsync(point.Id, DeleteWait, cancellationToken).ConfigureAwait(false);
            throw new InvalidDataException($"The copy of the restore point of {point.VmName} from {point.CreatedUtc.ToLocalTime():g} did not verify: {string.Join("; ", verify.Problems.Take(3))}");
        }

        return (bytes, path);
    }

    /// <summary>Copies the chain's SQL Server backups on the source and switches each at once; returns how many it switched.</summary>
    private async Task<int> MoveSqlBackupsAsync(
        ChainToMove chain, Guid sessionId, IRepositoryTransfer source, IRepositoryTransfer target, Throttle throttle, Action<long> copied, IOperationLog log, CancellationToken cancellationToken)
    {
        List<SqlBackupEntity> backups;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            backups = await db.SqlBackups.AsNoTracking()
                .Where(b => b.JobId == chain.JobId && b.VmNativeId == chain.ObjectNativeId && b.RepositoryId == chain.FromExtentId && b.State == SqlBackupState.Available)
                .OrderBy(b => b.CreatedUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var switched = 0;
        foreach (var backup in backups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PinnedObject pinned;
            try
            {
                pinned = await access.PinSqlBackupAsync(backup.Id, chain.FromExtentId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
            {
                continue;
            }

            string? path;
            long bytes;
            using (pinned)
            {
                if (pinned.RepositoryId != chain.FromExtentId)
                {
                    continue;
                }

                (path, bytes) = await CopySqlBackupAsync(backup, source, target, throttle, copied, cancellationToken).ConfigureAwait(false);
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var rows = await db.SqlBackups.Where(b => b.Id == backup.Id && b.RepositoryId == chain.FromExtentId && b.State == SqlBackupState.Available)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.RepositoryId, chain.ToExtentId).SetProperty(b => b.ManifestPath, path), cancellationToken).ConfigureAwait(false);
            if (rows == 1)
            {
                await db.SqlBackupSets.Where(s => s.SqlBackupId == backup.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.RepositoryId, chain.ToExtentId), cancellationToken).ConfigureAwait(false);
            }

            db.RepositoryMoveItems.Add(Item(chain, sessionId, MoveItemType.SqlBackup, backup.Id, bytes, rows == 1 ? MoveItemState.Switched : MoveItemState.Abandoned));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            switched += rows;
        }

        if (switched > 0)
        {
            log.Info($"{chain.ObjectName}: {switched} SQL Server backup(s) moved.", $"move-sql-{chain.JobId}-{chain.ObjectNativeId}");
        }

        return switched;
    }

    private static async Task<(string? Path, long Bytes)> CopySqlBackupAsync(SqlBackupEntity backup, IRepositoryTransfer source, IRepositoryTransfer target, Throttle throttle, Action<long> copied, CancellationToken cancellationToken)
    {
        var files = await source.ReadSqlBackupFilesAsync(backup.Id, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize(files[0].Content, Core.Repository.SqlBackupJsonContext.Default.SqlBackupManifest)
            ?? throw new InvalidDataException($"The manifest of SQL Server backup {backup.Id} cannot be read.");
        if (target.ContainsSqlBackup(backup.Id))
        {
            await target.TryDeleteSqlBackupAsync(backup.Id, DeleteWait, cancellationToken).ConfigureAwait(false);
        }

        var ids = await source.ListSqlBackupChunksAsync(backup.Id, cancellationToken).ConfigureAwait(false);
        long bytes = 0;
        await using var import = target.BeginSqlImport(backup.Id, manifest);
        var missing = ids.Where(id => !import.TryReuseChunk(id)).ToList();
        var locator = (IChunkLocator)source;
        var plan = locator.PlanRead(missing);
        if (plan.Missing.Count > 0)
        {
            throw new InvalidDataException($"{plan.Missing.Count} chunk(s) of the SQL Server backup of {backup.VmName} from {backup.CreatedUtc.ToLocalTime():g} are missing on the source extent.");
        }

        await foreach (var chunk in locator.ReadStoredAsync(plan, cancellationToken).ConfigureAwait(false))
        {
            await import.PutEncodedChunkAsync(chunk.Id, chunk.Record, cancellationToken).ConfigureAwait(false);
            bytes += chunk.Record.Length;
            copied(chunk.Record.Length);
            await throttle.WaitAsync(chunk.Record.Length, cancellationToken).ConfigureAwait(false);
        }

        foreach (var file in files.Skip(1))
        {
            await import.WriteFileAsync(file.Name, file.Content, cancellationToken).ConfigureAwait(false);
        }

        var info = await import.CommitAsync(null, cancellationToken).ConfigureAwait(false);
        return (info.Path, bytes);
    }

    /// <summary>
    /// Under the lease: points the journal has as copied switch to the target where their row is still on the source and
    /// available (compare-and-set); the others are abandoned. The placement follows. Returns how many switched.
    /// </summary>
    private async Task<int> SwitchAsync(ChainToMove chain, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var items = await db.RepositoryMoveItems
            .Where(m => m.ObjectType == MoveItemType.RestorePoint && m.State == MoveItemState.Copied && m.TargetExtentId == chain.ToExtentId && m.SourceExtentId == chain.FromExtentId
                && m.JobId == chain.JobId && m.ObjectNativeId == chain.ObjectNativeId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // Copies reused from an earlier run have no path in the journal: read them from the target once.
        Dictionary<Guid, string>? paths = null;
        if (items.Any(i => i.TargetPath is null))
        {
            var target = (IBackupRepository)await repositories.GetAsync(chain.ToExtentId, cancellationToken).ConfigureAwait(false);
            paths = [];
            await foreach (var info in target.ListRestorePointsAsync(cancellationToken).ConfigureAwait(false))
            {
                paths[info.RestorePointId] = info.Path;
            }
        }

        var switched = new List<Guid>();
        foreach (var item in items)
        {
            var path = item.TargetPath ?? paths?.GetValueOrDefault(item.ObjectId);
            var rows = path is null ? 0 : await db.RestorePoints
                .Where(r => r.Id == item.ObjectId && r.RepositoryId == chain.FromExtentId && r.State == RestorePointState.Available)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.RepositoryId, chain.ToExtentId).SetProperty(r => r.ManifestPath, path), cancellationToken).ConfigureAwait(false);
            item.State = rows == 1 ? MoveItemState.Switched : MoveItemState.Abandoned;
            item.UpdatedUtc = time.GetUtcNow();
            if (rows == 1)
            {
                switched.Add(item.ObjectId);
            }
        }

        if (switched.Count > 0)
        {
            await db.SqlBackupSets.Where(s => s.RestorePointId != null && switched.Contains(s.RestorePointId.Value))
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.RepositoryId, chain.ToExtentId), cancellationToken).ConfigureAwait(false);
        }

        if (chain.JobId is { } jobId)
        {
            // Nothing of the chain may stay behind: a point that could not be copied keeps the chain where it is.
            var left = await db.RestorePoints.Where(r => r.JobId == jobId && r.VmNativeId == chain.ObjectNativeId && r.RepositoryId == chain.FromExtentId && r.State == RestorePointState.Available)
                .Select(r => r.CreatedUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (left.Count > 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"{left.Count} restore point(s) of {chain.ObjectName} (from {left.Min().ToLocalTime():g}) are still on the source extent; the chain is not switched.");
            }

            var placement = await db.ChainPlacements.FirstOrDefaultAsync(p => p.JobId == jobId && p.ObjectNativeId == chain.ObjectNativeId, cancellationToken).ConfigureAwait(false);
            if (placement is null)
            {
                db.ChainPlacements.Add(new ChainPlacementEntity
                {
                    ScaleOutId = chain.ScaleOutId, JobId = jobId, ObjectNativeId = chain.ObjectNativeId, ExtentId = chain.ToExtentId, PlacedUtc = time.GetUtcNow(), Reason = "moved",
                });
            }
            else
            {
                placement.ExtentId = chain.ToExtentId;
                placement.PlacedUtc = time.GetUtcNow();
                placement.Reason = "moved";
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return switched.Count;
    }

    private static IQueryable<RestorePointEntity> ChainPoints(AppDbContext db, ChainToMove chain) =>
        db.RestorePoints.Where(r => r.JobId == chain.JobId && r.VmNativeId == chain.ObjectNativeId);

    /// <summary>
    /// Measures a chain on <paramref name="fromExtentId"/>: the stored size of every chunk its restore points and SQL Server
    /// backups use there, and of those <paramref name="toExtentId"/> does not have (all of them without a target). Reads the
    /// indexes only; an object deleted meanwhile is left out.
    /// </summary>
    public async Task<ChainSize> MeasureAsync(Guid? jobId, string objectNativeId, Guid fromExtentId, Guid? toExtentId, CancellationToken cancellationToken)
    {
        List<Guid> points, backups;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            points = await db.RestorePoints.Where(r => r.JobId == jobId && r.VmNativeId == objectNativeId && r.RepositoryId == fromExtentId && r.State == RestorePointState.Available)
                .Select(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            backups = await db.SqlBackups.Where(b => b.JobId == jobId && b.VmNativeId == objectNativeId && b.RepositoryId == fromExtentId && b.State == SqlBackupState.Available)
                .Select(b => b.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var source = (IRepositoryTransfer)await repositories.GetAsync(fromExtentId, cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<ChunkId>();
        foreach (var (id, sql) in points.Select(p => (p, false)).Concat(backups.Select(b => (b, true))))
        {
            try
            {
                ids.UnionWith(sql
                    ? await source.ListSqlBackupChunksAsync(id, cancellationToken).ConfigureAwait(false)
                    : await source.ListRestorePointChunksAsync(id, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is KeyNotFoundException or FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
            {
                // Deleted by retention meanwhile.
            }
        }

        var locator = (IChunkLocator)source;
        var footprint = locator.PlanRead(ids).StoredBytes;
        if (toExtentId is not { } to)
        {
            return new ChainSize(footprint, footprint);
        }

        var target = (IChunkLocator)await repositories.GetAsync(to, cancellationToken).ConfigureAwait(false);
        return new ChainSize(footprint, locator.PlanRead(target.PlanRead(ids).Missing).StoredBytes);
    }

    private async Task<(IRepositoryTransfer Repository, string Name)> OpenAsync(Guid extentId, CancellationToken cancellationToken)
    {
        string name;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            name = await db.Repositories.Where(r => r.Id == extentId).Select(r => r.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Extent {extentId} does not exist.");
        }

        return ((IRepositoryTransfer)await repositories.GetAsync(extentId, cancellationToken).ConfigureAwait(false), name);
    }

    private async Task<BackupRepositoryEntity> CheckTargetAsync(ChainToMove chain, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var extents = await db.Repositories.Where(r => r.Id == chain.FromExtentId || r.Id == chain.ToExtentId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var from = extents.FirstOrDefault(e => e.Id == chain.FromExtentId);
        var to = extents.FirstOrDefault(e => e.Id == chain.ToExtentId);
        if (from?.ScaleOutId != chain.ScaleOutId || to?.ScaleOutId != chain.ScaleOutId)
        {
            throw new InvalidOperationException("Chains move only between extents of the same scale-out repository.");
        }

        if (to.ExtentState != ExtentState.Active || to.Maintenance)
        {
            throw new InvalidOperationException($"Extent {to.Name} takes no chains: it is {(to.Maintenance ? "in maintenance" : to.ExtentState.ToString().ToLowerInvariant())}.");
        }

        if (to.ChunkSize != from.ChunkSize)
        {
            throw new InvalidOperationException($"Extents {from.Name} and {to.Name} use different chunk sizes.");
        }

        // A move to another target supersedes copies an interrupted move left elsewhere: those are abandoned (and deleted).
        await db.RepositoryMoveItems.Where(m => m.State == MoveItemState.Copied && m.JobId == chain.JobId && m.ObjectNativeId == chain.ObjectNativeId && m.TargetExtentId != chain.ToExtentId)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.State, MoveItemState.Abandoned).SetProperty(m => m.UpdatedUtc, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return to;
    }

    private async Task MarkMovingAsync(ChainToMove chain, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.ChainPlacements.Where(p => p.JobId == chain.JobId && p.ObjectNativeId == chain.ObjectNativeId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.MoveSessionId, sessionId), cancellationToken).ConfigureAwait(false);
    }

    private async Task UnmarkMovingAsync(ChainToMove chain, Guid sessionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
        await db.ChainPlacements.Where(p => p.JobId == chain.JobId && p.ObjectNativeId == chain.ObjectNativeId && p.MoveSessionId == sessionId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.MoveSessionId, (Guid?)null), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> JournalledAsync(Guid objectId, Guid targetExtentId, MoveItemState state, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.RepositoryMoveItems.AnyAsync(m => m.ObjectId == objectId && m.TargetExtentId == targetExtentId && m.State == state, cancellationToken).ConfigureAwait(false);
    }

    private async Task JournalAsync(ChainToMove chain, Guid sessionId, MoveItemType type, Guid objectId, long bytes, string? path, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.RepositoryMoveItems.FirstOrDefaultAsync(m => m.ObjectId == objectId && m.TargetExtentId == chain.ToExtentId && m.State == MoveItemState.Copied, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var item = Item(chain, sessionId, type, objectId, bytes, MoveItemState.Copied);
            item.TargetPath = path;
            db.RepositoryMoveItems.Add(item);
        }
        else
        {
            existing.SessionId = sessionId;
            existing.TargetPath = path ?? existing.TargetPath;
            existing.BytesWritten += bytes;
            existing.Attempts++;
            existing.UpdatedUtc = time.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private RepositoryMoveItemEntity Item(ChainToMove chain, Guid sessionId, MoveItemType type, Guid objectId, long bytes, MoveItemState state) => new()
    {
        SessionId = sessionId,
        ScaleOutId = chain.ScaleOutId,
        JobId = chain.JobId,
        ObjectNativeId = chain.ObjectNativeId,
        ObjectType = type,
        ObjectId = objectId,
        SourceExtentId = chain.FromExtentId,
        TargetExtentId = chain.ToExtentId,
        State = state,
        BytesWritten = bytes,
        Attempts = 1,
        CreatedUtc = time.GetUtcNow(),
        UpdatedUtc = time.GetUtcNow(),
    };

    private async Task QueueGcAsync(Guid extentId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.Repositories.Where(r => r.Id == extentId && !r.Maintenance).Select(r => r.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) is { } name)
        {
            try
            {
                await queue.EnqueueMaintenanceAsync(extentId, name, "Chains moved", cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Queued already.
            }
        }
    }

    private static string Describe(ChainToMove chain) => chain.JobId is null ? $"{chain.ObjectName} (restore points of no job)" : chain.ObjectName;

    /// <summary>Keeps a move at or below a rate (MB/s), so backups on the same disks keep their speed.</summary>
    private sealed class Throttle(int? megabytesPerSecond, TimeProvider time)
    {
        private readonly long _started = time.GetTimestamp();
        private long _bytes;

        public async Task WaitAsync(long bytes, CancellationToken cancellationToken)
        {
            if (megabytesPerSecond is not { } rate)
            {
                return;
            }

            _bytes += bytes;
            var due = TimeSpan.FromSeconds(_bytes / (rate * 1_000_000.0));
            var elapsed = time.GetElapsedTime(_started);
            if (due > elapsed)
            {
                await Task.Delay(due - elapsed, time, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
