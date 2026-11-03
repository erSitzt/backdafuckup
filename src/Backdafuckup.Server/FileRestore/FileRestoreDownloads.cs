using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Security.Cryptography;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Operations;
using Backdafuckup.FileLevel;
using Backdafuckup.FileLevel.Zip;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Backdafuckup.Server.FileRestore;

/// <summary>
/// ZIP downloads from a file browser. The selection is posted first and returns a one-time link: a browser download
/// is a plain GET (no request body, URL length limits), and the link must not be reusable from logs or history.
/// The archive is written on its own thread into a pipe that is copied asynchronously to the response.
/// </summary>
public sealed class FileRestoreDownloads(
    FileRestoreMounts mounts,
    IOptions<FileRestoreOptions> options,
    TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);

    /// <summary>Checks the selection and returns a link that works once, for this user, for a short time.</summary>
    public async Task<FileDownloadTicketDto> PrepareZipAsync(BrowseMount mount, FileDownloadRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var paths = FileRestoreMounts.ParseSelection(request.Paths);
        var parent = paths[0].Parent!;

        // Fail now (404, unreadable volume) rather than after the browser has started the download.
        await mounts.CheckSelectionAsync(mount, request.Volume, paths, cancellationToken).ConfigureAwait(false);

        var zone = ResolveZone(request.TimeZone);
        var now = time.GetUtcNow();
        Purge(now);
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var fileName = ZipFileName(mount, request.Volume, parent, paths, zone);
        var lifetime = options.Value.DownloadLinkLifetime;
        _tickets[token] = new Ticket(mount.Id, mount.User, request.Volume, parent, [.. paths], zone, fileName, now + lifetime);
        return new FileDownloadTicketDto($"api/file-restore/downloads/{token}", fileName, (int)lifetime.TotalSeconds);
    }

    /// <summary>
    /// A link for a ZIP of single files from any volumes of the browser, under the given names (e.g. the files of a SQL
    /// database), plus optional text files. <paramref name="what"/> names the content in the audit log.
    /// </summary>
    public FileDownloadTicketDto PrepareFiles(
        BrowseMount mount, IReadOnlyList<(string Volume, GuestPath Path, string Name)> files, IReadOnlyList<(string Name, string Text)> extras,
        string fileName, string what, string? timeZone)
    {
        var now = time.GetUtcNow();
        Purge(now);
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var lifetime = options.Value.DownloadLinkLifetime;
        _tickets[token] = new Ticket(mount.Id, mount.User, string.Empty, GuestPath.Root, [], ResolveZone(timeZone), SafeFileName(fileName), now + lifetime, files, extras, what);
        return new FileDownloadTicketDto($"api/file-restore/downloads/{token}", SafeFileName(fileName), (int)lifetime.TotalSeconds);
    }

    /// <summary>
    /// Streams the ZIP of a ticket. Errors before the first byte become problem responses; later ones abort the
    /// connection, so the browser reports a failed download instead of saving a short archive.
    /// </summary>
    public async Task StreamZipAsync(string token, string user, HttpResponse response, CancellationToken requestAborted)
    {
        var now = time.GetUtcNow();
        Purge(now);
        if (!_tickets.TryRemove(token, out var ticket) || ticket.ExpiresUtc < now || ticket.User != user)
        {
            throw new MountNotFoundException("This download link has expired or was used already. Start the download again in the file browser.");
        }

        var mount = mounts.Get(ticket.MountId, user);
        using var lease = mount.Lease();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 4 << 20, resumeWriterThreshold: 2 << 20, useSynchronizationContext: false));
        var what = DescribeSelection(mount, ticket);
        var zipOptions = new GuestZipOptions
        {
            MaxEntries = options.Value.MaxZipEntries,
            Writer = new ZipWriterOptions { DosTimeZone = ticket.Zone },
            Describe = path => FileRestoreMounts.Describe(mount, ticket.Files?.FirstOrDefault(f => f.Path.Equals(path)).Volume ?? ticket.Volume, path),
        };
        GuestZipResult? result = null;
        var producer = FileLevelWorkers.RunDedicated(
            () =>
            {
                try
                {
                    using var image = GuestImage.Open(mount.Disks, describe: false);
                    using var output = pipe.Writer.AsStream(leaveOpen: true);
                    result = ticket.Files is { } files
                        ? GuestZipExport.WriteFiles([.. files.Select(f => (image.OpenFileSystem(f.Volume), f.Path, f.Name))], ticket.Extras ?? [], output, zipOptions, stop.Token)
                        : GuestZipExport.Write(image.OpenFileSystem(ticket.Volume), ticket.Paths, output, zipOptions, stop.Token);
                    output.Flush();
                    pipe.Writer.Complete();
                }
                catch (Exception ex)
                {
                    pipe.Writer.Complete(ex);
                    throw;
                }
            },
            "zip-download");

        ReadResult first;
        try
        {
            first = await pipe.Reader.ReadAsync(requestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            await FileRestoreMounts.ObserveAsync(producer).ConfigureAwait(false);
            mount.Log.Warning($"ZIP download of {what} by {user} failed: {ex.Message}");
            throw;
        }

        response.ContentType = "application/zip";
        response.Headers.CacheControl = "no-store";
        response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = ticket.FileName,
            FileName = FileRestoreMounts.AsciiName(ticket.FileName),
        }.ToString();
        long sent = 0;
        try
        {
            var read = first;
            while (true)
            {
                foreach (var segment in read.Buffer)
                {
                    await response.Body.WriteAsync(segment, requestAborted).ConfigureAwait(false);
                    sent += segment.Length;
                }

                pipe.Reader.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted)
                {
                    break;
                }

                read = await pipe.Reader.ReadAsync(requestAborted).ConfigureAwait(false);
            }

            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            await producer.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await pipe.Reader.CompleteAsync(ex).ConfigureAwait(false);
            await FileRestoreMounts.ObserveAsync(producer).ConfigureAwait(false);
            var reason = requestAborted.IsCancellationRequested ? "the browser cancelled it" : ex.Message;
            mount.Log.Warning($"ZIP download of {what} by {user} broke off after {FileRestoreMounts.Fmt(sent)}: {reason}");
            response.HttpContext.Abort();
            return;
        }

        var done = result!;
        mount.CountDownload(done.ContentBytes);
        mount.Log.Info(
            $"{user} downloaded {what} as ZIP: {done.Files:N0} file(s), {done.Directories:N0} folder(s), {FileRestoreMounts.Fmt(done.ContentBytes)} (ZIP {FileRestoreMounts.Fmt(done.ArchiveBytes)}).");
        if (done.SkippedCount > 0)
        {
            var examples = string.Join("; ", done.Skipped.Take(5).Select(s => $"{s.Path}: {s.Reason}"));
            mount.Log.Warning(
                $"{done.SkippedCount:N0} item(s) are not in the ZIP and are listed in {done.SkippedListName} inside it. {examples}{(done.SkippedCount > 5 ? "; …" : string.Empty)}");
        }
    }

    /// <summary>Drops expired links (also called on every request; there are only ever a few).</summary>
    public void Purge(DateTimeOffset now)
    {
        foreach (var (token, ticket) in _tickets)
        {
            if (ticket.ExpiresUtc < now)
            {
                _tickets.TryRemove(token, out _);
            }
        }
    }

    private static string DescribeSelection(BrowseMount mount, Ticket ticket)
    {
        if (ticket.What is not null)
        {
            return ticket.What;
        }

        var names = string.Join(", ", ticket.Paths.Take(5).Select(p => p.Name));
        var more = ticket.Paths.Length > 5 ? $" and {ticket.Paths.Length - 5:N0} more" : string.Empty;
        return $"{names}{more} in {FileRestoreMounts.Describe(mount, ticket.Volume, ticket.Parent)}";
    }

    private static string ZipFileName(BrowseMount mount, string volume, GuestPath parent, List<GuestPath> paths, TimeZoneInfo zone)
    {
        string label;
        if (paths.Count == 1)
        {
            label = paths[0].Name;
        }
        else if (!parent.IsRoot)
        {
            label = parent.Name;
        }
        else
        {
            var v = mount.Image.Volumes.FirstOrDefault(x => x.Id == volume);
            label = v is null ? "files" : FileRestoreMounts.Title(v);
        }

        var created = TimeZoneInfo.ConvertTime(mount.Point.CreatedUtc, zone);
        return SafeFileName($"{mount.Point.VmName}_{created:yyyy-MM-dd_HHmm}_{label}") + ".zip";
    }

    /// <summary>A name every browser and file system accepts.</summary>
    private static string SafeFileName(string name)
    {
        var chars = name.Select(c => c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().Trim('.');
        return result.Length > 150 ? result[..150] : result;
    }

    private static TimeZoneInfo ResolveZone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Local;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Ticket(
        Guid MountId,
        string User,
        string Volume,
        GuestPath Parent,
        GuestPath[] Paths,
        TimeZoneInfo Zone,
        string FileName,
        DateTimeOffset ExpiresUtc,
        IReadOnlyList<(string Volume, GuestPath Path, string Name)>? Files = null,
        IReadOnlyList<(string Name, string Text)>? Extras = null,
        string? What = null);
}
