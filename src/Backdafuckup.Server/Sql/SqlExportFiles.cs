using System.Collections.Concurrent;
using System.Security.Cryptography;
using Backdafuckup.Contracts;
using Backdafuckup.Server.Hosting;

namespace Backdafuckup.Server.Sql;

/// <summary>
/// The .bak files of exports that the console offers for download: one folder per export session under the data
/// directory (<c>sql-exports/&lt;session&gt;/</c>), deleted after <see cref="Lifetime"/>. Downloads use one-time links,
/// like the file browser's, because a browser download is a plain GET.
/// </summary>
public sealed class SqlExportFiles(ServerPaths paths, TimeProvider time, ILogger<SqlExportFiles> logger)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private static readonly TimeSpan LinkLifetime = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (Guid Session, string User, DateTimeOffset ExpiresUtc)> _tickets = new(StringComparer.Ordinal);

    public string Root => Path.Combine(paths.DataDirectory, "sql-exports");

    /// <summary>Stores an uploaded file for the session; a partial upload is never kept.</summary>
    public async Task<long> SaveAsync(Guid session, string name, Stream body, long? expectedBytes, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{name}' is not the name of a .bak file.");
        }

        var folder = Path.Combine(Root, session.ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder))
        {
            File.Delete(old);
        }

        var target = Path.Combine(folder, fileName);
        var partial = target + ".part";
        long written;
        try
        {
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                await body.CopyToAsync(file, 1 << 20, cancellationToken).ConfigureAwait(false);
                written = file.Length;
            }

            if (expectedBytes is { } expected && written != expected)
            {
                throw new IOException($"The upload ended after {written:N0} of {expected:N0} bytes.");
            }

            File.Move(partial, target);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        return written;
    }

    /// <summary>The session's file, if it is still there.</summary>
    public SqlExportFileDto? Find(Guid session)
    {
        var folder = Path.Combine(Root, session.ToString("N"));
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var file = new DirectoryInfo(folder).EnumerateFiles("*.bak").FirstOrDefault();
        return file is null ? null : new SqlExportFileDto(file.Name, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) + Lifetime);
    }

    /// <summary>A link that works once, for this user, for a minute.</summary>
    public string? IssueLink(Guid session, string user)
    {
        if (Find(session) is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        foreach (var expired in _tickets.Where(t => t.Value.ExpiresUtc < now).Select(t => t.Key).ToList())
        {
            _tickets.TryRemove(expired, out _);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _tickets[token] = (session, user, now + LinkLifetime);
        return $"api/sql/exports/downloads/{token}";
    }

    /// <summary>The file of a link (null if the link is unknown, used, expired or another user's).</summary>
    public (string Path, string Name)? Redeem(string token, string user)
    {
        if (!_tickets.TryRemove(token, out var ticket) || ticket.ExpiresUtc < time.GetUtcNow() || ticket.User != user)
        {
            return null;
        }

        var folder = Path.Combine(Root, ticket.Session.ToString("N"));
        var file = Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*.bak").FirstOrDefault() : null;
        return file is null ? null : (file.FullName, file.Name);
    }

    /// <summary>Deletes the files older than <see cref="Lifetime"/> and leftover partial uploads.</summary>
    public void Sweep()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        var cutoff = time.GetUtcNow().UtcDateTime - Lifetime;
        foreach (var folder in new DirectoryInfo(Root).EnumerateDirectories())
        {
            try
            {
                var files = folder.EnumerateFiles().ToList();
                if (files.All(f => f.LastWriteTimeUtc < cutoff || f.Name.EndsWith(".part", StringComparison.Ordinal) && f.LastWriteTimeUtc < cutoff.AddHours(23)))
                {
                    folder.Delete(recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Removing the expired SQL export {Folder} failed: {Message}", folder.FullName, ex.Message);
            }
        }
    }
}

/// <summary>Removes expired .bak files at start and every hour.</summary>
public sealed class SqlExportSweeper(SqlExportFiles files, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            files.Sweep();
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
