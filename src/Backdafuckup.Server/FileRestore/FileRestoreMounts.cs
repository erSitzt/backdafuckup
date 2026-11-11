using System.Collections.Concurrent;
using System.IO.Pipelines;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.FileLevel;
using Backdafuckup.Server.Api;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Backdafuckup.Server.FileRestore;

/// <summary>
/// The open file browsers. Each one is a session of kind FileBrowse whose log is the audit trail (who opened which
/// restore point, every download). File system work runs on <see cref="FileLevelWorkers"/>, never on request threads.
/// </summary>
public sealed class FileRestoreMounts(
    IDbContextFactory<AppDbContext> dbFactory,
    RepositoryManager repositories,
    RunningSessions running,
    SessionEventWriter events,
    SessionTracker tracker,
    ILiveNotifier notifier,
    ChunkCache cache,
    FileLevelWorkers workers,
    IOptions<EngineOptions> engine,
    IOptions<FileRestoreOptions> options,
    TimeProvider time,
    ILogger<FileRestoreMounts> logger)
{
    private readonly ConcurrentDictionary<Guid, BrowseMount> _mounts = new();
    private readonly SemaphoreSlim _openGate = new(1, 1);

    public FileRestoreOptions Options => options.Value;

    public async Task<BrowseMount> OpenAsync(Guid restorePointId, string user, CancellationToken cancellationToken)
    {
        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _mounts.Values.FirstOrDefault(m => m.Point.Id == restorePointId && m.User == user && !m.IsClosed);
            if (existing is not null)
            {
                existing.Touch(time.GetUtcNow());
                return existing;
            }

            if (_mounts.Count >= options.Value.MaxMounts)
            {
                throw new MountLimitException(
                    $"{options.Value.MaxMounts} file browsers are open already. Close one under Sessions, or wait until one closes after {options.Value.IdleTimeout.TotalMinutes:0} minutes without use.");
            }

            return await OpenNewAsync(restorePointId, user, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _openGate.Release();
        }
    }

    public BrowseMount Get(Guid mountId, string user)
    {
        if (_mounts.TryGetValue(mountId, out var mount) && mount.User == user && !mount.IsClosed)
        {
            mount.Touch(time.GetUtcNow());
            return mount;
        }

        throw new MountNotFoundException(
            $"This file browser is closed (by a user, after {options.Value.IdleTimeout.TotalMinutes:0} minutes without use, or by a restart of the backup service). Open it again.");
    }

    public IReadOnlyList<BrowseMount> ForUser(string user) => [.. _mounts.Values.Where(m => m.User == user && !m.IsClosed)];

    public bool HasMounts(Guid repositoryId) => _mounts.Values.Any(m => m.Point.RepositoryId == repositoryId && !m.IsClosed);

    public FileMountDto ToDto(BrowseMount mount) => new(
        mount.Id,
        mount.Point.Id,
        mount.SessionId,
        mount.Point.VmName,
        mount.Point.CreatedUtc,
        mount.Point.Consistency,
        mount.Point.ConsistencyReason,
        [.. mount.Image.Volumes.Select(ToDto)],
        mount.Image.Warnings,
        (int)options.Value.IdleTimeout.TotalMinutes,
        mount.Point.VmNativeId,
        mount.Point.ConsistencyWarning,
        mount.Point.ObjectKind);

    public async Task<GuestEntryPageDto> ListAsync(BrowseMount mount, string volume, GuestPath path, int offset, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, options.Value.MaxPageSize);
        offset = Math.Max(0, offset);
        var entries = await workers.Run(
            () =>
            {
                lock (mount.Gate)
                {
                    return mount.ListSorted(volume, path);
                }
            },
            cancellationToken).ConfigureAwait(false);
        return new GuestEntryPageDto(volume, path.ToString(), entries.Count, offset, [.. entries.Skip(offset).Take(limit).Select(ToDto)]);
    }

    /// <summary>Streams one file. Reading runs on its own thread; the response is written asynchronously.</summary>
    public async Task StreamFileAsync(BrowseMount mount, string volume, GuestPath path, HttpResponse response, CancellationToken cancellationToken)
    {
        using var lease = mount.Lease();

        // Also stops the reader thread when the response fails for another reason than the browser going away.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ready = new TaskCompletionSource<GuestEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 4 << 20, resumeWriterThreshold: 2 << 20));
        long copied = 0;
        var producer = FileLevelWorkers.RunDedicated(
            () =>
            {
                try
                {
                    using var image = GuestImage.Open(mount.Disks, describe: false);
                    var fs = image.OpenFileSystem(volume);
                    var entry = fs.Stat(path);
                    using var input = fs.OpenRead(path);
                    ready.TrySetResult(entry);
                    using var output = pipe.Writer.AsStream(leaveOpen: true);
                    var buffer = new byte[1 << 20];
                    int n;
                    while ((n = input.Read(buffer)) > 0)
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, n);
                        copied += n;
                    }

                    pipe.Writer.Complete();
                }
                catch (Exception ex)
                {
                    ready.TrySetException(ex);
                    pipe.Writer.Complete(ex);
                    throw;
                }
            },
            "file-download");

        GuestEntry file;
        try
        {
            file = await ready.Task.ConfigureAwait(false);
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            await ObserveAsync(producer).ConfigureAwait(false);
            throw;
        }

        response.ContentType = "application/octet-stream";
        response.ContentLength = file.Size;
        response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = file.Name, FileName = AsciiName(file.Name) }.ToString();
        try
        {
            await pipe.Reader.CopyToAsync(response.Body, cancellationToken).ConfigureAwait(false);
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            await producer.ConfigureAwait(false);
            mount.CountDownload(copied);
            mount.Log.Info($"{mount.User} downloaded {Describe(mount, volume, path)} ({Fmt(copied)}).");
        }
        catch (Exception ex)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await pipe.Reader.CompleteAsync(ex).ConfigureAwait(false);
            await ObserveAsync(producer).ConfigureAwait(false);
            mount.Log.Warning($"Download of {Describe(mount, volume, path)} by {mount.User} broke off after {Fmt(copied)}: {ex.Message}");
            if (!response.HasStarted)
            {
                response.ContentLength = null;
                response.Headers.ContentDisposition = default;
                throw;
            }

            // Too late for an error response: abort, so the browser reports a failed download, not a short file.
            response.HttpContext.Abort();
        }
    }

    public async Task CloseAsync(BrowseMount mount, string reason, SessionState state = SessionState.Succeeded, bool flushLog = true)
    {
        if (!mount.MarkClosed())
        {
            return;
        }

        _mounts.TryRemove(mount.Id, out _);
        try
        {
            await workers.Run(() =>
            {
                lock (mount.Gate)
                {
                    mount.Image.Dispose();
                }

                return true;
            }).ConfigureAwait(false);
            await mount.Reader.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            mount.Pin.Dispose();
            running.Unregister(mount.SessionId);
        }

        var summary = $"{reason}. {mount.Downloads} download(s), {Fmt(mount.DownloadedBytes)}.";
        mount.Log.Info($"File browser closed: {summary}");
        if (flushLog)
        {
            await events.FlushAsync().ConfigureAwait(false);
        }

        await tracker.UpdateSessionAsync(
            mount.SessionId,
            s =>
            {
                s.State = state;
                s.Summary = summary;
                s.EndedUtc = time.GetUtcNow();
                s.Progress = 100;
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Closes browsers that were not used for <see cref="FileRestoreOptions.IdleTimeout"/>.</summary>
    public async Task SweepAsync()
    {
        var limit = time.GetUtcNow() - options.Value.IdleTimeout;
        foreach (var mount in _mounts.Values.Where(m => m.Leases == 0 && m.LastUsedUtc < limit).ToList())
        {
            await CloseAsync(mount, $"closed after {options.Value.IdleTimeout.TotalMinutes:0} minutes without use").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes every browser when the service stops. The session log may not be written any more then (its writer can have
    /// stopped first), so it is not waited for; a browser whose session cannot be updated is ended by the next start.
    /// </summary>
    public async Task CloseAllAsync(string reason)
    {
        foreach (var mount in _mounts.Values.ToList())
        {
            try
            {
                await CloseAsync(mount, reason, SessionState.Stopped, flushLog: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or ObjectDisposedException or InvalidOperationException or DbUpdateException or Npgsql.NpgsqlException)
            {
                logger.LogWarning(ex, "Closing the file browser of session {Session} at shutdown did not finish; the next start ends the session.", mount.SessionId);
            }
        }
    }

    private async Task<BrowseMount> OpenNewAsync(Guid restorePointId, string user, CancellationToken cancellationToken)
    {
        RestorePointEntity point;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            point = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == restorePointId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The restore point no longer exists.");
        }

        if (point.State != RestorePointState.Available)
        {
            throw new InvalidOperationException($"The restore point of {point.VmName} from {point.CreatedUtc.ToLocalTime():g} is not available ({point.State}).");
        }

        var repository = await repositories.GetAsync(point.RepositoryId, cancellationToken).ConfigureAwait(false);
        var pin = repository.Pin(point.Id);
        IRestorePointReader? reader = null;
        GuestImage? image = null;
        Guid? sessionId = null;
        try
        {
            // Pin first, then check again: retention may have started deleting it in between.
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                var state = await db.RestorePoints.Where(r => r.Id == point.Id).Select(r => (RestorePointState?)r.State).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (state != RestorePointState.Available)
                {
                    throw new InvalidOperationException($"The restore point of {point.VmName} is being deleted.");
                }
            }

            reader = await repository.OpenRestorePointAsync(point.Id, cancellationToken).ConfigureAwait(false);
            var disks = await OpenDisksAsync(reader, cache, cancellationToken).ConfigureAwait(false);

            var browsed = new BrowsedRestorePoint(point.Id, point.RepositoryId, point.VmName, point.VmNativeId, point.CreatedUtc, point.Consistency, point.ConsistencyReason, point.ConsistencyWarning, point.ObjectKind);
            sessionId = await CreateSessionAsync(browsed, user, cancellationToken).ConfigureAwait(false);
            var log = events.CreateLog(sessionId.Value, null);
            log.Info($"Opened by {user}: restore point of {point.VmName} from {point.CreatedUtc.ToLocalTime():g} ({point.Consistency}-consistent), {disks.Count} disk(s).");
            image = await workers.Run(() => GuestImage.Open(disks), cancellationToken).ConfigureAwait(false);
            foreach (var volume in image.Volumes)
            {
                if (volume.Readable)
                {
                    log.Info($"Volume {Title(volume)} ({volume.FileSystem}, {Fmt(volume.Length)}).");
                }
                else
                {
                    log.Warning($"Volume {Title(volume)} ({Fmt(volume.Length)}) cannot be browsed: {volume.Reason}.");
                }
            }

            foreach (var warning in image.Warnings)
            {
                log.Warning(warning);
            }

            if (point.Consistency != "application")
            {
                log.Info("The restore point is crash-consistent: the newest changes before the snapshot may be missing in some files and folders.");
            }

            var mount = new BrowseMount(Guid.CreateVersion7(), user, browsed, sessionId.Value, log, pin, reader, disks, image, time.GetUtcNow());
            var stop = running.Register(sessionId.Value, SessionKind.FileBrowse, CancellationToken.None);
            stop.Token.Register(() => _ = Task.Run(() => CloseAsync(mount, $"stopped by a user", SessionState.Stopped)));
            _mounts[mount.Id] = mount;
            return mount;
        }
        catch (Exception ex)
        {
            image?.Dispose();
            if (reader is not null)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }

            pin.Dispose();
            if (sessionId is { } id)
            {
                events.CreateLog(id, null).Error($"The restore point could not be opened: {ex.Message}");
                await events.FlushAsync().ConfigureAwait(false);
                await tracker.UpdateSessionAsync(id, s => { s.State = SessionState.Failed; s.Summary = ex.Message; s.EndedUtc = time.GetUtcNow(); }, CancellationToken.None).ConfigureAwait(false);
            }

            logger.LogWarning(ex, "Opening restore point {RestorePoint} for file-level restore failed.", restorePointId);
            throw;
        }
    }

    /// <summary>A selection of one folder: 1 to 10,000 paths, all in the same folder, not the volume root (400 otherwise).</summary>
    internal static List<GuestPath> ParseSelection(IReadOnlyList<string>? paths)
    {
        const int maxSelection = 10_000;
        if (paths is not { Count: > 0 })
        {
            throw new ArgumentException("Select at least one file or folder.");
        }

        if (paths.Count > maxSelection)
        {
            throw new ArgumentException($"Select at most {maxSelection:N0} items; select their folder instead.");
        }

        var parsed = paths.Select(GuestPath.Parse).Distinct().ToList();
        if (parsed.Any(p => p.IsRoot))
        {
            throw new ArgumentException("Select files or folders inside the volume, not the volume itself.");
        }

        var parent = parsed[0].Parent!;
        if (parsed.Any(p => !p.Parent!.Equals(parent)))
        {
            throw new ArgumentException("All selected items must be in the same folder.");
        }

        return parsed;
    }

    /// <summary>Checks on a worker thread that the volume is readable and every selected item exists (404 otherwise).</summary>
    internal Task CheckSelectionAsync(BrowseMount mount, string volume, IReadOnlyList<GuestPath> paths, CancellationToken cancellationToken) =>
        workers.Run(
            () =>
            {
                lock (mount.Gate)
                {
                    var fs = mount.Image.OpenFileSystem(volume);
                    foreach (var path in paths)
                    {
                        fs.Stat(path);
                    }
                }

                return true;
            },
            cancellationToken);

    /// <summary>The disks of a restore point as sources for <see cref="GuestImage"/> (indexes read once, chunks through the cache).</summary>
    internal static async Task<List<GuestDiskSource>> OpenDisksAsync(IRestorePointReader reader, ChunkCache cache, CancellationToken cancellationToken)
    {
        var disks = new List<GuestDiskSource>();
        foreach (var disk in reader.Manifest.Disks)
        {
            var index = await reader.ReadDiskIndexAsync(disk.Key, cancellationToken).ConfigureAwait(false);
            var chunks = reader.Chunks;
            disks.Add(new GuestDiskSource(disk.Key, disk.Name, () => new RestorePointDiskStream(index, chunks, cache)));
        }

        return disks;
    }

    private async Task<Guid> CreateSessionAsync(BrowsedRestorePoint point, string user, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var session = new SessionEntity
        {
            Kind = SessionKind.FileBrowse,
            ObjectKind = point.ObjectKind,
            RepositoryId = point.RepositoryId,
            Name = $"Browse files {point.VmName} ({point.CreatedUtc.ToLocalTime():g})",
            TriggeredBy = user,
            State = SessionState.Running,
            CreatedUtc = now,
            StartedUtc = now,
            ServerInstanceId = engine.Value.ServerInstanceId,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await notifier.SessionUpdatedAsync(session.ToDto()).ConfigureAwait(false);
        return session.Id;
    }

    private static GuestVolumeDto ToDto(GuestVolume v) => new(
        v.Id,
        Title(v),
        v.MountPoint,
        v.Label,
        v.FileSystem,
        v.FileSystem switch { "NTFS" or "FAT" => "windows", "ext" or "XFS" => "unix", _ => null },
        v.Length,
        v.DiskName,
        v.PartitionType,
        v.Readable,
        v.Reason);

    private static GuestEntryDto ToDto(GuestEntry e) => new(
        e.Name,
        e.Kind switch
        {
            GuestEntryKind.File => "file",
            GuestEntryKind.Directory => "directory",
            GuestEntryKind.Symlink => "symlink",
            GuestEntryKind.Junction => "junction",
            GuestEntryKind.ReparsePoint => "reparse",
            _ => "special",
        },
        e.Size,
        e.ModifiedUtc is { } m ? new DateTimeOffset(m, TimeSpan.Zero) : null,
        e.CreatedUtc is { } c ? new DateTimeOffset(c, TimeSpan.Zero) : null,
        e.Attributes is { } a ? a.ToString() : null,
        e.Mode is { } mode ? Convert.ToString(mode, 8).PadLeft(4, '0') : null,
        e.UserId,
        e.GroupId,
        e.LinkTarget,
        e.Unsupported);

    /// <summary>
    /// Drive letter or mount point (with the label), else the label, the LVM volume, the partition type; a file system on
    /// the whole disk (a Kubernetes PVC) is named after its disk; the id is the last resort.
    /// </summary>
    internal static string Title(GuestVolume v)
    {
        var wholeDisk = v.PartitionType is null && v.LvmName is null && v.StartOffset == 0 && !string.IsNullOrEmpty(v.DiskName) ? v.DiskName : null;
        var name = v.MountPoint ?? (string.IsNullOrEmpty(v.Label) ? null : v.Label) ?? v.LvmName ?? v.PartitionType ?? wholeDisk ?? v.Id;
        return v.MountPoint is not null && !string.IsNullOrEmpty(v.Label) ? $"{name} ({v.Label})" : name;
    }

    internal static string Describe(BrowseMount mount, string volume, GuestPath path) =>
        Describe(mount.Image.Volumes.FirstOrDefault(x => x.Id == volume), volume, path);

    /// <summary>A guest path as the user knows it: <c>C:\Users\x</c> or <c>/home/x</c>.</summary>
    internal static string Describe(GuestVolume? v, string volume, GuestPath path)
    {
        // The drive letter or mount point alone ("C:", "/home"); a label would make the path unreadable. A Linux volume
        // without a mount point is named before a colon: "data-redis-0:/appendonlydir".
        var prefix = v?.MountPoint ?? (v is null ? volume : Title(v));
        return v?.FileSystem is "NTFS" or "FAT"
            ? $"{prefix.TrimEnd('\\')}\\{string.Join('\\', path.Components)}"
            : v?.MountPoint is null ? $"{prefix}:{path}" : $"{prefix.TrimEnd('/')}{path}";
    }

    internal static string AsciiName(string name)
    {
        var ascii = new string([.. name.Select(ch => ch is >= ' ' and < (char)127 and not '"' and not '\\' ? ch : '_')]);
        return string.IsNullOrWhiteSpace(ascii) ? "download" : ascii;
    }

    internal static string Fmt(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MiB",
        >= 1L << 10 => $"{bytes / 1024.0:0.##} KiB",
        _ => $"{bytes} bytes",
    };

    internal static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Reported by the caller.
        }
    }
}

/// <summary>Closes idle file browsers every minute and all of them when the service stops.</summary>
public sealed class FileRestoreSweeper(FileRestoreMounts mounts, FileRestoreDownloads downloads, TimeProvider time, ILogger<FileRestoreSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await mounts.SweepAsync().ConfigureAwait(false);
                    downloads.Purge(time.GetUtcNow());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Closing idle file browsers failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await mounts.CloseAllAsync("the backup service stopped").ConfigureAwait(false);
    }
}
