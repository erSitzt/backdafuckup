using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.ScaleOut;

/// <summary>Settings of scale-out repositories (section <c>ScaleOut</c>).</summary>
public sealed class ScaleOutOptions
{
    /// <summary>Allows two extents of one scale-out on the same volume (tests, a lab); their free space would count twice.</summary>
    public bool AllowSameVolume { get; set; }

    /// <summary>How long switching maintenance on waits for an extent to become idle before it answers "waiting for …".</summary>
    public TimeSpan MaintenanceWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the balancer checks extents, cleans up after moves and starts automatic rebalancing.</summary>
    public TimeSpan BalancerInterval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>A refused scale-out request: the message says why, the status code how (400, 404, 409).</summary>
public sealed class ScaleOutRequestException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Adds extents (converting a standalone repository), seals them and switches their maintenance.</summary>
public sealed class ScaleOutAdmin(
    IDbContextFactory<AppDbContext> dbFactory,
    RepositoryManager repositories,
    IVolumeSpace volumes,
    TimeProvider time,
    IOptions<ScaleOutOptions> options)
{
    /// <summary>
    /// Adds a directory as an extent of the scale-out repository <paramref name="repositoryId"/>, or converts the
    /// standalone repository <paramref name="repositoryId"/> into the first extent of a new scale-out repository.
    /// Returns the scale-out repository and the new extent; an existing directory's restore points are imported after.
    /// </summary>
    public async Task<(BackupRepositoryEntity ScaleOut, BackupRepositoryEntity Extent, bool Existing)> AddExtentAsync(Guid repositoryId, AddExtentDto request, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var target = await db.Repositories.FirstOrDefaultAsync(r => r.Id == repositoryId, cancellationToken).ConfigureAwait(false)
            ?? throw new ScaleOutRequestException($"Repository {repositoryId} does not exist.", StatusCodes.Status404NotFound);
        if (target.ScaleOutId is not null)
        {
            throw new ScaleOutRequestException($"{target.Name} is an extent; add the new extent to its scale-out repository.");
        }

        var converting = target.Kind != RepositoryKind.ScaleOut;
        var scaleOutName = request.ScaleOutName?.Trim();
        if (converting && string.IsNullOrEmpty(scaleOutName))
        {
            throw new ScaleOutRequestException($"{target.Name} becomes the first extent of a new scale-out repository: give the scale-out repository a name.");
        }

        var name = request.Name.Trim();
        if (name.Length == 0 || await db.Repositories.AnyAsync(r => r.Name == name || (converting && r.Name == scaleOutName), cancellationToken).ConfigureAwait(false) || name == scaleOutName)
        {
            throw new ScaleOutRequestException($"The names must be new and different: a repository named '{(await db.Repositories.AnyAsync(r => r.Name == name, cancellationToken).ConfigureAwait(false) ? name : scaleOutName)}' exists already.", StatusCodes.Status409Conflict);
        }

        if (request.PackSizeMiB is < 16 or > 512)
        {
            throw new ScaleOutRequestException("The pack size must be between 16 and 512 MiB.");
        }

        var extents = converting ? [target] : await db.Repositories.Where(r => r.ScaleOutId == target.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (await db.Sessions.AnyAsync(s => s.Kind == SessionKind.RepositoryMove && (s.State == SessionState.Queued || s.State == SessionState.Running || s.State == SessionState.Stopping), cancellationToken).ConfigureAwait(false))
        {
            throw new ScaleOutRequestException("A move between extents is running; add the extent when it has finished.", StatusCodes.Status409Conflict);
        }

        if (converting)
        {
            var jobIds = await db.Jobs.Where(j => j.RepositoryId == target.Id).Select(j => j.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (await db.Sessions.AnyAsync(s => s.JobId != null && jobIds.Contains(s.JobId.Value) && s.Kind == SessionKind.Backup && (s.State == SessionState.Queued || s.State == SessionState.Running || s.State == SessionState.Stopping), cancellationToken).ConfigureAwait(false))
            {
                throw new ScaleOutRequestException($"Backups to {target.Name} are running or queued; convert it when they have finished.", StatusCodes.Status409Conflict);
            }
        }

        var path = Path.GetFullPath(request.Path.Trim());
        var all = await db.Repositories.Where(r => r.Kind == RepositoryKind.LocalDirectory).Select(r => new { r.Name, r.Path }).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (all.FirstOrDefault(r => SamePathOrNested(r.Path, path)) is { } clash)
        {
            throw new ScaleOutRequestException($"{path} is or overlaps the directory of repository {clash.Name} ({clash.Path}).", StatusCodes.Status409Conflict);
        }

        if (!options.Value.AllowSameVolume)
        {
            Directory.CreateDirectory(path);
            var volume = volumes.VolumeOf(path);
            if (volume is not null && extents.FirstOrDefault(e => volumes.VolumeOf(e.Path) == volume) is { } same)
            {
                throw new ScaleOutRequestException(
                    $"{path} is on the same volume ({volume}) as extent {same.Name} ({same.Path}); extents must be on different volumes, or their free space counts twice.");
            }
        }

        var existing = File.Exists(Path.Combine(path, LocalDirectoryRepository.DescriptorFileName));
        var chunkSize = target.ChunkSize;
        var compression = target.CompressionLevel;
        LocalDirectoryRepository directory;
        try
        {
            directory = existing
                ? await LocalDirectoryRepository.OpenAsync(path, cancellationToken, time).ConfigureAwait(false)
                : await LocalDirectoryRepository.CreateAsync(path, cancellationToken, chunkSize, compression, time, request.PackSizeMiB * 1024L * 1024).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new ScaleOutRequestException($"The directory {path} cannot be used: {ex.Message}");
        }

        BackupRepositoryEntity extent;
        BackupRepositoryEntity scaleOut;
        await using (directory)
        {
            if (directory.Info.ChunkSize != chunkSize)
            {
                throw new ScaleOutRequestException(
                    $"{path} holds a repository with chunks of {directory.Info.ChunkSize / 1024} KiB; the extents of {(converting ? target.Name : target.Name)} use {chunkSize / 1024} KiB. Restore points move between extents only with equal chunk sizes.");
            }

            if (await db.Repositories.AnyAsync(r => r.RepositoryUuid == directory.Info.RepositoryId, cancellationToken).ConfigureAwait(false))
            {
                throw new ScaleOutRequestException($"The repository in {path} is registered already.", StatusCodes.Status409Conflict);
            }

            var stats = directory.GetStorageStats();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (converting)
            {
                scaleOut = new BackupRepositoryEntity
                {
                    Name = scaleOutName!,
                    Kind = RepositoryKind.ScaleOut,
                    Path = string.Empty,
                    ChunkSize = chunkSize,
                    CompressionLevel = compression,
                    MaxConcurrentTasks = target.MaxConcurrentTasks,
                    ScaleOutSettings = new ScaleOutSettings(),
                };
                scaleOut.RepositoryUuid = scaleOut.Id;
                db.Repositories.Add(scaleOut);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                // The repository keeps its name (and its metric history) as the first extent; its jobs write to the scale-out.
                target.ScaleOutId = scaleOut.Id;
                target.ExtentState = ExtentState.Active;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await db.Jobs.Where(j => j.RepositoryId == target.Id).ExecuteUpdateAsync(u => u.SetProperty(j => j.RepositoryId, scaleOut.Id), cancellationToken).ConfigureAwait(false);

                // Every existing chain stays where it is.
                var now = time.GetUtcNow();
                var chains = await db.RestorePoints.Where(r => r.RepositoryId == target.Id && r.JobId != null)
                    .Select(r => new { JobId = r.JobId!.Value, r.VmNativeId })
                    .Union(db.SqlBackups.Where(b => b.RepositoryId == target.Id && b.JobId != null).Select(b => new { JobId = b.JobId!.Value, b.VmNativeId }))
                    .Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
                var jobs = await db.Jobs.Select(j => j.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var chain in chains.Where(c => jobs.Contains(c.JobId)))
                {
                    db.ChainPlacements.Add(new ChainPlacementEntity
                    {
                        ScaleOutId = scaleOut.Id, JobId = chain.JobId, ObjectNativeId = chain.VmNativeId, ExtentId = target.Id, PlacedUtc = now, Reason = "adopted",
                    });
                }
            }
            else
            {
                scaleOut = target;
            }

            extent = new BackupRepositoryEntity
            {
                Name = name,
                Path = path,
                RepositoryUuid = directory.Info.RepositoryId,
                ChunkSize = directory.Info.ChunkSize,
                CompressionLevel = directory.Info.CompressionLevel,
                MaxConcurrentTasks = target.MaxConcurrentTasks,
                TotalBytes = stats.TotalBytes,
                FreeBytes = stats.FreeBytes,
                ScaleOutId = scaleOut.Id,
                ExtentState = ExtentState.Active,
            };
            db.Repositories.Add(extent);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return (scaleOut, extent, existing);
    }

    /// <summary>
    /// Seals or unseals an extent and switches its maintenance. Maintenance on refuses new reads and writes at once and
    /// closes the extent when it is idle; the result says what it still waits for (null: closed).
    /// </summary>
    public async Task<string?> SetExtentStateAsync(Guid extentId, ExtentStateDto request, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var extent = await db.Repositories.FirstOrDefaultAsync(r => r.Id == extentId, cancellationToken).ConfigureAwait(false)
            ?? throw new ScaleOutRequestException($"Repository {extentId} does not exist.", StatusCodes.Status404NotFound);
        if (extent.ScaleOutId is not { } scaleOutId)
        {
            throw new ScaleOutRequestException($"{extent.Name} is not an extent of a scale-out repository.");
        }

        var scaleOutName = await db.Repositories.Where(r => r.Id == scaleOutId).Select(r => r.Name).FirstAsync(cancellationToken).ConfigureAwait(false);
        if (request.Sealed is { } seal)
        {
            if (!seal && extent.ExtentState == ExtentState.Evacuating)
            {
                throw new ScaleOutRequestException($"{extent.Name} is being evacuated; cancel the evacuation first (it stays sealed).", StatusCodes.Status409Conflict);
            }

            if (seal && extent.ExtentState == ExtentState.Active
                && !await db.Repositories.AnyAsync(r => r.ScaleOutId == scaleOutId && r.Id != extentId && r.ExtentState == ExtentState.Active && !r.Maintenance, cancellationToken).ConfigureAwait(false))
            {
                throw new ScaleOutRequestException($"{extent.Name} is the only active extent of {scaleOutName}; new chains would have nowhere to go. Add an extent first.", StatusCodes.Status409Conflict);
            }

            if (extent.ExtentState != ExtentState.Evacuating)
            {
                extent.ExtentState = seal ? ExtentState.Sealed : ExtentState.Active;
            }
        }

        string? waiting = null;
        if (request.Maintenance is { } maintenance)
        {
            if (maintenance && !extent.Maintenance)
            {
                extent.Maintenance = true;
                extent.MaintenanceSinceUtc = time.GetUtcNow();
            }
            else if (!maintenance && extent.Maintenance)
            {
                extent.Maintenance = false;
                extent.MaintenanceSinceUtc = null;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (request.Maintenance == true)
        {
            var busy = await repositories.CloseForMaintenanceAsync(extent.Id, RepositoryManager.MaintenanceReason(extent, scaleOutName), options.Value.MaintenanceWait, cancellationToken).ConfigureAwait(false);
            waiting = busy is { } activity ? $"Entering maintenance: {extent.Name} closes once nothing uses it any more (now: {activity})." : null;
        }
        else if (request.Maintenance == false)
        {
            repositories.Reopen(extent.Id);
        }

        return waiting;
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are the same directory or one inside the other.</summary>
    private static bool SamePathOrNested(string a, string b)
    {
        if (string.IsNullOrEmpty(a))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var x = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)) + Path.DirectorySeparatorChar;
        var y = Path.TrimEndingDirectorySeparator(b) + Path.DirectorySeparatorChar;
        return x.StartsWith(y, comparison) || y.StartsWith(x, comparison);
    }
}
