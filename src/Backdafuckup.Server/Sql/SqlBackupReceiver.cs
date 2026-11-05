using Backdafuckup.Agent.Protocol;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Sql;

/// <summary>
/// Takes the backup files of one agent job (<see cref="SqlJobKinds.LogBackup"/>, <see cref="SqlJobKinds.FullBackup"/>)
/// straight into the repository: one SQL backup object per instance and kind, files streamed into chunks and checked
/// against the agent's length and SHA-256, committed together with the catalog rows when the agent asks
/// (<see cref="CommitAsync"/>). Disposing without commit discards what was written; the agent keeps the files queued.
/// </summary>
public sealed class SqlBackupReceiver(
    IBackupRepository repository,
    Guid repositoryId,
    ManifestSource source,
    ManifestJob? job,
    Guid? sessionId,
    IDbContextFactory<AppDbContext> dbFactory,
    TimeProvider time) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<(string Instance, SqlBackupKind Kind), Group> _groups = [];
    private bool _committed;

    /// <summary>Files committed by this receiver (with their uploads), for the run's log and chain watch.</summary>
    public List<SqlBackupUpload> Committed { get; } = [];

    /// <summary>Bytes of the committed files and the bytes they added to the repository.</summary>
    public (long Bytes, long NewStoredBytes) Totals => (Committed.Sum(c => c.Length), _groups.Values.Sum(g => g.NewStoredBytes));

    public async Task<SqlBackupUploadReply> ReceiveAsync(SqlBackupUpload upload, Stream body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var set = SqlBackupCatalog.ToBackupSet(upload.BackupSet);
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await db.SqlBackupSets.AnyAsync(s => s.BackupSetUuid == set.BackupSetUuid, cancellationToken).ConfigureAwait(false))
            {
                return new SqlBackupUploadReply { Known = true };
            }
        }

        var kind = upload.Kind switch
        {
            SqlBackupActions.Log => SqlBackupKind.Log,
            SqlBackupActions.Full => SqlBackupKind.Full,
            "system" => SqlBackupKind.SystemFull,
            _ => throw new InvalidOperationException($"Unknown backup kind '{upload.Kind}'."),
        };
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_committed)
            {
                throw new InvalidOperationException("This job's backups are committed already.");
            }

            if (!_groups.TryGetValue((upload.Instance, kind), out var group))
            {
                group = new Group(repository.BeginSqlBackup(Guid.CreateVersion7(), source.VmId), upload.ProductVersion);
                _groups[(upload.Instance, kind)] = group;
            }

            var stored = await group.Writer.WriteFileAsync(upload.FileId, body, cancellationToken).ConfigureAwait(false);
            if (stored.Length != upload.Length || !string.Equals(stored.Sha256, upload.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // Left out of the manifest; its chunks go with the next GC. The agent keeps the file and sends it again.
                throw new InvalidDataException(
                    $"{upload.FileName} arrived with {stored.Length:N0} bytes and SHA-256 {stored.Sha256}, the agent read {upload.Length:N0} bytes and {upload.Sha256}.");
            }

            group.NewStoredBytes += stored.NewStoredBytes;
            group.Files.Add((upload, new SqlBackupFile
            {
                Key = upload.FileId,
                Database = upload.Database,
                FileName = upload.FileName,
                Length = stored.Length,
                Sha256 = stored.Sha256,
                Index = stored.Index,
                BackupSet = set,
            }));
            return new SqlBackupUploadReply();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Commits every object and records it in the catalog; returns the committed file ids.</summary>
    public async Task<SqlBackupUploadReply> CommitAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reply = new SqlBackupUploadReply();
            if (_committed)
            {
                reply.Committed.AddRange(Committed.Select(c => c.FileId));
                return reply;
            }

            foreach (var ((instance, kind), group) in _groups)
            {
                if (group.Files.Count == 0 || group.Committed)
                {
                    reply.Committed.AddRange(group.Committed ? group.Files.Select(f => f.Upload.FileId) : []);
                    continue;
                }

                var manifest = new SqlBackupManifest
                {
                    SqlBackupId = group.Writer.SqlBackupId,
                    RepositoryId = repository.Info.RepositoryId,
                    CreatedUtc = time.GetUtcNow(),
                    Kind = kind,
                    Job = job,
                    SessionId = sessionId,
                    Source = source,
                    Instance = instance,
                    ProductVersion = group.ProductVersion,
                    Chunking = new ManifestChunking(repository.Info.ChunkSize, repository.Info.Hash, repository.Info.Compression),
                    Files = [.. group.Files.Select(f => f.File)],
                };
                var info = await group.Writer.CommitAsync(manifest, cancellationToken).ConfigureAwait(false);
                group.Committed = true;
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await SqlBackupCatalog.RecordSqlBackupAsync(db, repositoryId, info, time.GetUtcNow(), cancellationToken, group.NewStoredBytes).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                Committed.AddRange(group.Files.Select(f => f.Upload));
                reply.Committed.AddRange(group.Files.Select(f => f.Upload.FileId));
            }

            _committed = true;
            return reply;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var group in _groups.Values)
        {
            await group.Writer.DisposeAsync().ConfigureAwait(false);
        }

        _lock.Dispose();
    }

    private sealed class Group(ISqlBackupWriter writer, string? productVersion)
    {
        public ISqlBackupWriter Writer { get; } = writer;

        public string? ProductVersion { get; } = productVersion;

        public List<(SqlBackupUpload Upload, SqlBackupFile File)> Files { get; } = [];

        public long NewStoredBytes { get; set; }

        public bool Committed { get; set; }
    }
}
