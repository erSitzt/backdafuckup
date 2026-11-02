using System.Globalization;
using System.Text;
using Backdafuckup.FileLevel.Restore;
using Backdafuckup.Server.Data;
using Backdafuckup.Ssh;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.FileRestore;

/// <summary>
/// Restores into a Linux guest in two steps. While the pipeline runs, files are uploaded over SFTP into a staging folder
/// and listed in a manifest; <see cref="Apply"/> then runs the restore script as root, which puts everything in place.
/// Conflicts, keep mode, owners, times and links are decided there, in the guest, so <see cref="Probe"/> reports nothing.
/// </summary>
public sealed class SshStagingTarget(ISshSession session, string stage, string baseFolder, string host) : IFileRestoreTarget, ILinkRestoreTarget
{
    private readonly StringBuilder _manifest = new();
    private readonly StringBuilder _links = new();
    private readonly List<StagedEntry> _entries = [new StagedEntry('-', string.Empty, 0)];
    private (int Index, string Path, int Number, long Size)? _pendingFile;
    private int _files;

    public bool IsUsable => session.IsConnected;

    public string Describe(IReadOnlyList<string> path) => $"{host}:{Absolute(path)}";

    /// <summary>Linux names may contain anything but "/" and NUL, which guest paths never do.</summary>
    public string? CheckName(string name) => null;

    public TargetEntryKind Probe(IReadOnlyList<string> path)
    {
        Flush();
        return TargetEntryKind.Missing;
    }

    public void CreateDirectory(IReadOnlyList<string> path)
    {
        Flush();
        var absolute = Absolute(path);
        _manifest.Append($"D {Add('D', absolute, 0)} {B64(absolute)}\n");
    }

    public void WriteFile(IReadOnlyList<string> path, Stream content, long length, bool replace, CancellationToken cancellationToken)
    {
        Flush();
        var number = ++_files;
        session.Upload($"{stage}/{number}", content, length, cancellationToken);
        var absolute = Absolute(path);
        _pendingFile = (Add('F', absolute, length), absolute, number, length);
    }

    public void SetMetadata(IReadOnlyList<string> path, RestoredItemMeta meta, bool isDirectory)
    {
        var absolute = Absolute(path);
        if (!isDirectory && _pendingFile is { } file && file.Path == absolute)
        {
            // Owner, mode and times go with the file, so the script sets them before it renames the file into place.
            _manifest.Append($"F {file.Index} {B64(absolute)} {file.Number} {Meta(meta)}\n");
            _pendingFile = null;
            return;
        }

        Flush();
        if (isDirectory)
        {
            _manifest.Append($"M {Add('M', absolute, 0)} {B64(absolute)} {Meta(meta)}\n");
        }
    }

    public void CreateSymbolicLink(IReadOnlyList<string> path, string linkTarget, RestoredItemMeta meta)
    {
        Flush();
        var absolute = Absolute(path);
        _links.Append($"L {Add('L', absolute, 0)} {B64(absolute)} {B64(linkTarget)} {Id(meta.UserId)} {Id(meta.GroupId)}\n");
    }

    /// <summary>Uploads the manifest and runs the restore script as root; returns what it reported per entry.</summary>
    public SshApplyResult Apply(bool keep, bool owner, string scriptRoot, CancellationToken cancellationToken)
    {
        Flush();
        session.WriteText($"{stage}/manifest", _manifest.ToString());
        if (_links.Length > 0)
        {
            session.WriteText($"{stage}/links", _links.ToString());
        }

        var result = new SshApplyResult();
        var done = false;
        session.RunRestoreScript(stage, [keep ? "keep" : "overwrite", owner ? "1" : "0", B64(scriptRoot)], line =>
        {
            var parts = line.Trim().Split(' ', 3);
            if (parts[0] == "DONE")
            {
                done = true;
                return;
            }

            if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= _entries.Count)
            {
                return;
            }

            var entry = _entries[index];
            var text = parts.Length > 2 ? Decode(parts[2]) : string.Empty;
            switch (parts[0])
            {
                case "OK" when entry.Kind is 'F':
                    result.Restored.Add(entry);
                    break;
                case "RENAMED":
                    if (entry.Kind is 'F')
                    {
                        result.Restored.Add(entry);
                    }

                    result.Renamed.Add((entry, $"{host}:{text}"));
                    break;
                case "FAIL":
                    result.Failed.Add((entry, text));
                    break;
                case "PARTIAL":
                    result.Partial.Add((entry, text));
                    break;
            }
        }, cancellationToken);
        if (!done)
        {
            throw new IOException($"The restore script on {session.Endpoint} ended without finishing; check the guest (disk space, /var/tmp).");
        }

        return result;
    }

    public string Display(StagedEntry entry) => $"{host}:{entry.Path}";

    public void Dispose()
    {
    }

    private string Absolute(IReadOnlyList<string> path) =>
        path.Count == 0 ? baseFolder : baseFolder.TrimEnd('/') + "/" + string.Join('/', path);

    private int Add(char kind, string path, long size)
    {
        _entries.Add(new StagedEntry(kind, path, size));
        return _entries.Count - 1;
    }

    private void Flush()
    {
        if (_pendingFile is { } file)
        {
            _manifest.Append($"F {file.Index} {B64(file.Path)} {file.Number} - - - - -\n");
            _pendingFile = null;
        }
    }

    /// <summary>mode uid gid mtime atime; "-" where unknown. Times as touch -t wants them, in UTC.</summary>
    private static string Meta(RestoredItemMeta meta) =>
        $"{(meta.UnixMode is { } mode ? "0" + Convert.ToString(mode & 0xFFF, 8) : "-")} {Id(meta.UserId)} {Id(meta.GroupId)} {Time(meta.ModifiedUtc)} {Time(meta.AccessedUtc)}";

    private static string Id(long? id) => id is { } value and >= 0 ? value.ToString(CultureInfo.InvariantCulture) : "-";

    private static string Time(DateTime? utc) =>
        utc is { } value && value.Year is >= 1970 and <= 9999 ? value.ToString("yyyyMMddHHmm.ss", CultureInfo.InvariantCulture) : "-";

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string Decode(string base64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return base64;
        }
    }
}

/// <param name="Kind">D folder, F file, M folder times, L link.</param>
public sealed record StagedEntry(char Kind, string Path, long Size);

/// <summary>What the restore script reported.</summary>
public sealed class SshApplyResult
{
    public List<StagedEntry> Restored { get; } = [];

    public List<(StagedEntry Entry, string NewPath)> Renamed { get; } = [];

    public List<(StagedEntry Entry, string Reason)> Failed { get; } = [];

    public List<(StagedEntry Entry, string Reason)> Partial { get; } = [];
}

/// <summary>SSH host keys trusted on first use, in the catalog (ssh_host_keys).</summary>
public sealed class DbHostKeyStore(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, string trustedBy, Action<string> note) : IHostKeyStore
{
    public string? Verify(string host, int port, string keyType, string fingerprintSha256)
    {
        using var db = dbFactory.CreateDbContext();
        var known = db.SshHostKeys.FirstOrDefault(k => k.Host == host && k.Port == port);
        if (known is null)
        {
            db.SshHostKeys.Add(new SshHostKeyEntity { Host = host, Port = port, KeyType = keyType, Fingerprint = fingerprintSha256, TrustedUtc = time.GetUtcNow(), TrustedBy = trustedBy });
            db.SaveChanges();
            note($"New SSH host key of {host}:{port} trusted on first use: {keyType} SHA256:{fingerprintSha256}.");
            return null;
        }

        return known.KeyType == keyType && known.Fingerprint == fingerprintSha256
            ? null
            : $"it presents {keyType} SHA256:{fingerprintSha256}, but {known.KeyType} SHA256:{known.Fingerprint} was trusted on {known.TrustedUtc.ToLocalTime():g}. " +
              "If the VM was reinstalled, remove the old key under Credentials → Known SSH hosts; otherwise someone may be intercepting the connection.";
    }
}
