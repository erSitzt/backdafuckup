using System.Net;
using System.Net.Sockets;
using SMBLibrary;
using SMBLibrary.Client;
using SMBLibrary.DFS;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Backdafuckup.Smb;

/// <summary>A folder on an SMB share: <c>\\server\share\folder\sub</c> (also accepted with "/").</summary>
public sealed record UncPath(string Server, string Share, IReadOnlyList<string> Folder)
{
    public static UncPath Parse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Trim().Replace('/', '\\');
        if (!normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{path}' is not a share path; write it as \\\\server\\share\\folder.");
        }

        var parts = normalized[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new ArgumentException($"'{path}' names no share; write it as \\\\server\\share\\folder.");
        }

        foreach (var part in parts)
        {
            if (part is "." or ".." || part.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0 || part.Any(char.IsControl))
            {
                throw new ArgumentException($"'{path}' contains the invalid component '{part}'.");
            }
        }

        return new UncPath(parts[0], parts[1], parts[2..]);
    }

    /// <summary>Path below the share root for SMB requests ("" is the root).</summary>
    public string Relative(IEnumerable<string> below) => string.Join('\\', Folder.Concat(below));

    public override string ToString() => $@"\\{Server}\{Share}" + (Folder.Count > 0 ? @"\" + string.Join('\\', Folder) : string.Empty);
}

public enum SmbEntryKind
{
    Missing,
    File,
    Directory,

    /// <summary>A symbolic link, junction or other reparse point (never followed).</summary>
    ReparsePoint,
}

/// <summary>A connected share. Paths are relative to the share root with "\" separators. Not thread-safe.</summary>
public interface ISmbShare : IDisposable
{
    /// <summary>For messages: <c>fs01 (10.0.0.5):445</c>.</summary>
    string Endpoint { get; }

    /// <summary>Where the folder really is: the requested path, or the target of its DFS links.</summary>
    UncPath Location { get; }

    /// <summary>The DFS links followed to get there, e.g. <c>\\corp\dfs\Exchange → \\fs01\exchange</c>.</summary>
    IReadOnlyList<string> Referrals { get; }

    /// <summary>Diagnostics for the session log (DFS referral answers, fallbacks used); grows while the share is used.</summary>
    IReadOnlyList<string> Notes { get; }

    bool IsConnected { get; }

    SmbEntryKind Probe(string path);

    /// <summary>Creates one folder; an existing folder is fine, an existing file or link is an error.</summary>
    void CreateDirectory(string path);

    /// <summary>Writes to a temporary file in the same folder and renames it to <paramref name="path"/> when complete.</summary>
    void WriteFile(string path, Stream content, long length, bool replace, CancellationToken cancellationToken);

    /// <summary>Sets times (null: unchanged) and attributes (0: unchanged).</summary>
    void SetBasicInformation(string path, bool isDirectory, DateTime? createdUtc, DateTime? modifiedUtc, DateTime? accessedUtc, System.IO.FileAttributes attributes);
}

public interface ISmbShareConnector
{
    /// <summary>Resolves the server, connects, logs on and opens the share; errors name server, address, port and account.</summary>
    ISmbShare Connect(UncPath path, string userName, string password, TimeSpan timeout);
}

public sealed class SmbShareConnector : ISmbShareConnector
{
    private const uint FsctlDfsGetReferrals = 0x00060194;
    private const int MaxReferralHops = 4;

    public ISmbShare Connect(UncPath path, string userName, string password, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(path);
        var referrals = new List<string>();
        var notes = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path.ToString() };

        // The DFS name as written (namespace servers are asked for it) and where to connect now.
        var logical = path;
        var current = path;
        for (var hop = 0; ; hop++)
        {
            var ip = Resolve(current.Server);
            var display = ip.ToString() == current.Server ? current.Server : $"{current.Server} ({ip})";
            var client = SmbLogon.Open(current.Server, display, ip, userName, password, timeout);

            // A DFS path: work directly on the share it points to, as Windows does. SMBLibrary does not follow DFS
            // links; through a server that only holds the namespace, files would end up in the wrong place.
            if (hop < MaxReferralHops && Referral(client, logical, current, display, notes) is { } next && visited.Add(next.Target.ToString()))
            {
                referrals.Add($"{current} → {next.Target}");
                client.Logoff();
                client.Disconnect();
                current = next.Target;
                if (!next.IsRoot)
                {
                    // A link target is a name of its own (it may be another namespace).
                    logical = next.Target;
                }

                continue;
            }

            var store = client.TreeConnect(current.Share, out var status);
            if (status != NTStatus.STATUS_SUCCESS || store is null)
            {
                client.Logoff();
                client.Disconnect();
                var reason = status switch
                {
                    NTStatus.STATUS_BAD_NETWORK_NAME => "the share does not exist",
                    NTStatus.STATUS_ACCESS_DENIED => $"{userName} has no access to it",
                    _ => status.ToString(),
                };
                throw new IOException($"\\\\{current.Server}\\{current.Share} on {display}:445 cannot be opened: {reason}.");
            }

            string[] prefixes = [$@"{current.Server}\{current.Share}", $@"\{current.Server}\{current.Share}", $@"{ip}\{current.Share}"];
            return new SmbShare(client, store, $"{display}:445", current, referrals, notes, prefixes);
        }
    }

    /// <summary>
    /// Asks the server for a DFS referral (MS-DFSC, FSCTL_DFS_GET_REFERRALS on IPC$): first for the whole name, which a
    /// namespace server answers with the link or root that covers it, then for <c>\server\share</c> alone, which a domain
    /// controller answers with the namespace servers of a domain-based namespace. Returns where to go next, or null if
    /// the path is an ordinary share or already the target.
    /// </summary>
    private static (UncPath Target, bool IsRoot)? Referral(SMB2Client client, UncPath logical, UncPath current, string display, List<string> notes)
    {
        var ipc = client.TreeConnect("IPC$", out var status);
        if (status != NTStatus.STATUS_SUCCESS || ipc is null)
        {
            notes.Add($"DFS referral not asked: IPC$ on {display} cannot be opened ({status}).");
            return null;
        }

        var full = $@"\{logical.Server}\{logical.Share}" + (logical.Folder.Count > 0 ? @"\" + string.Join('\\', logical.Folder) : string.Empty);
        var root = $@"\{logical.Server}\{logical.Share}";
        try
        {
            foreach (var name in full == root ? [full] : new[] { full, root })
            {
                var response = Ask(ipc, name, display, notes);
                if (response is null)
                {
                    continue;
                }

                var targets = response.ReferralEntries.OfType<DfsReferralEntryV3>()
                    .Where(e => !e.IsNameListReferral && !string.IsNullOrEmpty(e.NetworkAddress))
                    .Select(e => (Prefix: e.DfsPath, Address: e.NetworkAddress, IsRoot: e.ServerType == DfsServerType.Root))
                    .Concat(response.ReferralEntries.OfType<DfsReferralEntryV2>().Where(e => !string.IsNullOrEmpty(e.NetworkAddress))
                        .Select(e => (Prefix: e.DfsPath, Address: e.NetworkAddress, IsRoot: e.ServerType == DfsServerType.Root)))
                    .ToList();
                if (targets.Count == 0)
                {
                    continue;
                }

                // A root referral lists all namespace servers: stay on the current one if it is among them.
                var chosen = targets.FirstOrDefault(t => SameServer(ServerOf(t.Address), current.Server));
                if (chosen.Address is null)
                {
                    chosen = targets[0];
                }

                // The rest of the name after the part the referral covers.
                var prefix = chosen.Prefix?.TrimEnd('\\');
                var consumed = prefix is { Length: > 0 } && full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && (full.Length == prefix.Length || full[prefix.Length] == '\\')
                    ? prefix.Length
                    : Math.Min(full.Length, response.PathConsumed / 2);
                var target = UncPath.Parse(@"\\" + chosen.Address.TrimStart('\\').TrimEnd('\\') + full[consumed..]);
                var same = SameServer(target.Server, current.Server)
                    && string.Equals(target.Share, current.Share, StringComparison.OrdinalIgnoreCase)
                    && target.Folder.SequenceEqual(current.Folder, StringComparer.OrdinalIgnoreCase);
                return same ? null : (target, chosen.IsRoot);
            }

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException or ArgumentOutOfRangeException)
        {
            // A referral we cannot parse: use the path as it is.
            notes.Add($"DFS referral for {full} at {display} not usable: {ex.Message}");
            return null;
        }
        finally
        {
            ipc.Disconnect();
        }
    }

    private static ResponseGetDfsReferral? Ask(ISMBFileStore ipc, string name, string display, List<string> notes)
    {
        var request = new RequestGetDfsReferral { MaxReferralLevel = 4, RequestFileName = name };
        var handle = new SMBLibrary.SMB2.FileID { Persistent = ulong.MaxValue, Volatile = ulong.MaxValue };
        var status = ipc.DeviceIOControl(handle, FsctlDfsGetReferrals, request.GetBytes(), out var output, 8192);
        if (status != NTStatus.STATUS_SUCCESS || output is null)
        {
            // STATUS_NOT_FOUND or STATUS_FS_DRIVER_REQUIRED: not a DFS name on this server.
            notes.Add($"DFS referral for {name} at {display}: {status}.");
            return null;
        }

        var response = new ResponseGetDfsReferral(output);
        notes.Add($"DFS referral for {name} at {display}: {response.PathConsumed / 2} characters matched: " + string.Join("; ", response.ReferralEntries.Select(Describe)) + ".");
        return response;
    }

    private static string ServerOf(string address) => address.TrimStart('\\').Split('\\')[0];

    /// <summary>"dfs01" and "dfs01.corp.example" are the same server.</summary>
    private static bool SameServer(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || (!IPAddress.TryParse(a, out _) && !IPAddress.TryParse(b, out _) && string.Equals(a.Split('.')[0], b.Split('.')[0], StringComparison.OrdinalIgnoreCase));

    private static string Describe(DfsReferralEntry entry) => entry switch
    {
        DfsReferralEntryV3 v3 when v3.IsNameListReferral => $"name list {v3.SpecialName} [{string.Join(", ", v3.ExpandedNames ?? [])}]",
        DfsReferralEntryV3 v3 => $"{v3.ServerType} {v3.DfsPath} → {v3.NetworkAddress}",
        DfsReferralEntryV2 v2 => $"{v2.ServerType} {v2.DfsPath} → {v2.NetworkAddress}",
        DfsReferralEntryV1 v1 => $"{v1.ServerType} {v1.ShareName}",
        _ => entry.GetType().Name,
    };

    private static IPAddress Resolve(string server)
    {
        if (IPAddress.TryParse(server.Trim('[', ']'), out var ip))
        {
            return ip;
        }

        try
        {
            var addresses = Dns.GetHostAddresses(server);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
                ?? throw new IOException($"{server} has no address in DNS.");
        }
        catch (SocketException ex)
        {
            throw new IOException($"{server} cannot be resolved by DNS: {ex.Message}", ex);
        }
    }

    /// <param name="dfsPrefixes">"server\share" forms for rename names on DFS namespace shares (see <see cref="WriteFile"/>).</param>
    private sealed class SmbShare(SMB2Client client, ISMBFileStore store, string endpoint, UncPath location, IReadOnlyList<string> referrals, List<string> notes, string[] dfsPrefixes) : ISmbShare
    {
        private bool _copyNoted;

        public IReadOnlyList<string> Notes => notes;

        private readonly string root = $@"\\{location.Server}\{location.Share}";

        public UncPath Location => location;

        public IReadOnlyList<string> Referrals => referrals;

        private const CreateOptions Sync = CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT;

        // MS-SMB2 2.2.13.1.1: FILE_READ_ATTRIBUTES and FILE_WRITE_ATTRIBUTES.
        private const AccessMask ReadAttributes = (AccessMask)0x80;
        private const AccessMask WriteAttributes = (AccessMask)0x100;

        public string Endpoint => endpoint;

        public bool IsConnected => client.IsConnected;

        public SmbEntryKind Probe(string path)
        {
            var status = store.CreateFile(out var handle, out _, path, ReadAttributes | AccessMask.SYNCHRONIZE, 0,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN, CreateOptions.FILE_OPEN_REPARSE_POINT | Sync, null);
            if (status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE)
            {
                return SmbEntryKind.Missing;
            }

            Check(status, "Reading", path);
            try
            {
                Check(store.GetFileInformation(out var info, handle, FileInformationClass.FileBasicInformation), "Reading the attributes of", path);
                var attributes = ((FileBasicInformation)info).FileAttributes;
                return (attributes & FileAttributes.ReparsePoint) != 0 ? SmbEntryKind.ReparsePoint
                    : (attributes & FileAttributes.Directory) != 0 ? SmbEntryKind.Directory
                    : SmbEntryKind.File;
            }
            finally
            {
                store.CloseFile(handle);
            }
        }

        public void CreateDirectory(string path)
        {
            var status = store.CreateFile(out var handle, out _, path, ReadAttributes | AccessMask.SYNCHRONIZE, FileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE | Sync, null);
            if (status == NTStatus.STATUS_OBJECT_NAME_COLLISION)
            {
                if (Probe(path) != SmbEntryKind.Directory)
                {
                    throw new IOException($"{Show(path)} exists and is not a folder.");
                }

                return;
            }

            Check(status, "Creating the folder", path);
            store.CloseFile(handle);
        }

        public void WriteFile(string path, Stream content, long length, bool replace, CancellationToken cancellationToken)
        {
            var slash = path.LastIndexOf('\\');
            var temp = (slash < 0 ? string.Empty : path[..(slash + 1)]) + $".bdfu-restore-{Guid.NewGuid():N}.tmp";
            var status = store.CreateFile(out var handle, out _, temp, AccessMask.GENERIC_READ | AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
                ShareAccess.None, CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE | Sync, null);
            Check(status, "Creating", temp);
            var renamed = false;
            try
            {
                var buffer = new byte[(int)Math.Min(client.MaxWriteSize, 1024 * 1024)];
                long offset = 0;
                int read;
                while ((read = ReadFull(content, buffer)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = read == buffer.Length ? buffer : buffer[..read];
                    status = store.WriteFile(out var written, handle, offset, chunk);
                    if (status != NTStatus.STATUS_SUCCESS || written != read)
                    {
                        throw new IOException($"Writing {Show(path)} on {endpoint} failed at byte {offset:N0}: {Describe(status)}.");
                    }

                    offset += read;
                }

                if (offset != length)
                {
                    throw new IOException($"{Show(path)}: {offset:N0} bytes were read from the restore point instead of {length:N0}; the file was not written.");
                }

                // On a DFS namespace share Windows reads the new name of a rename as a DFS path (server\share\path), as
                // Samba's own client sends it; SMBLibrary passes the name through unchanged. Behind a DFS link the file
                // lives on the link target, relative to whose share the name must be given. Try these in turn.
                var tried = new List<string>();
                var renameTo = path;
                status = Rename(handle, renameTo, replace);
                tried.Add(renameTo);
                foreach (var candidate in RenameCandidates(handle, path))
                {
                    if (!RenameNameRejected(status))
                    {
                        break;
                    }

                    renameTo = candidate;
                    tried.Add(renameTo);
                    status = Rename(handle, renameTo, replace);
                }

                if (status == NTStatus.STATUS_ACCESS_DENIED && replace && ClearReadOnly(path))
                {
                    status = Rename(handle, renameTo, replace);
                }

                if (status == NTStatus.STATUS_OBJECT_NAME_COLLISION)
                {
                    throw new IOException($"{Show(path)} was created by someone else during the restore; it is not replaced.");
                }

                if (RenameNameRejected(status))
                {
                    // No form of the rename name works (seen behind DFS namespaces): copy the complete temporary file to
                    // its name instead. Not atomic, so noted once in the session log.
                    var located = ActualName(handle);
                    var failure = $"rename tried as {string.Join(", ", tried.Select(t => $"'{t}'"))}: {Describe(status)}; {(located.Name is { } name ? $"the server reports the file at '{name}'" : located.Status == NTStatus.STATUS_SUCCESS ? "the server reports no name for the file" : $"the server does not report where the file is ({located.Status})")}";
                    if (!_copyNoted)
                    {
                        _copyNoted = true;
                        notes.Add($"Files cannot be renamed on {root} ({failure}). They are copied to their name after writing instead; an existing file is overwritten in place, not replaced at once.");
                    }

                    CopyInPlace(handle, path, length, replace, failure, cancellationToken);

                    // The copy is complete: the temporary file goes when its handle closes.
                    Check(store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true }), "Removing the temporary file of", path);
                    status = NTStatus.STATUS_SUCCESS;
                }

                if (status != NTStatus.STATUS_SUCCESS)
                {
                    throw new IOException($"Replacing {Show(path)} on {endpoint} failed: {Describe(status)}.");
                }

                renamed = true;
            }
            finally
            {
                if (!renamed)
                {
                    // Remove the incomplete temporary file; the target file is untouched.
                    store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
                }

                store.CloseFile(handle);
            }
        }

        public void SetBasicInformation(string path, bool isDirectory, DateTime? createdUtc, DateTime? modifiedUtc, DateTime? accessedUtc, System.IO.FileAttributes attributes)
        {
            var status = store.CreateFile(out var handle, out _, path, WriteAttributes | AccessMask.SYNCHRONIZE, 0,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN,
                (isDirectory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE) | CreateOptions.FILE_OPEN_REPARSE_POINT | Sync, null);
            Check(status, "Opening", path);
            try
            {
                Check(store.SetFileInformation(handle, new FileBasicInformation
                {
                    CreationTime = Utc(createdUtc),
                    LastWriteTime = Utc(modifiedUtc),
                    LastAccessTime = Utc(accessedUtc),
                    ChangeTime = new SetFileTime(null),
                    FileAttributes = (FileAttributes)(uint)attributes,
                }), "Setting times and attributes of", path);
            }
            finally
            {
                store.CloseFile(handle);
            }
        }

        public void Dispose()
        {
            try
            {
                store.Disconnect();
                client.Logoff();
            }
            finally
            {
                client.Disconnect();
            }
        }

        /// <summary>Copies the written temporary file to <paramref name="path"/> (fallback when renaming is not possible).</summary>
        private void CopyInPlace(object temporary, string path, long length, bool replace, string why, CancellationToken cancellationToken)
        {
            var disposition = replace ? CreateDisposition.FILE_OVERWRITE_IF : CreateDisposition.FILE_CREATE;
            var options = CreateOptions.FILE_NON_DIRECTORY_FILE | Sync;
            var status = store.CreateFile(out var target, out _, path, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, disposition, options, null);
            if (status == NTStatus.STATUS_ACCESS_DENIED && replace && ClearReadOnly(path))
            {
                status = store.CreateFile(out target, out _, path, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, disposition, options, null);
            }

            if (status == NTStatus.STATUS_OBJECT_NAME_COLLISION)
            {
                throw new IOException($"{Show(path)} was created by someone else during the restore; it is not replaced.");
            }

            if (status != NTStatus.STATUS_SUCCESS)
            {
                throw new IOException($"Writing {Show(path)} on {endpoint} failed: {Describe(status)} ({why}).");
            }

            var complete = false;
            try
            {
                var chunk = (int)Math.Min(Math.Min(client.MaxReadSize, client.MaxWriteSize), 1024 * 1024);
                long offset = 0;
                while (offset < length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    status = store.ReadFile(out var data, temporary, offset, (int)Math.Min(chunk, length - offset));
                    if (status != NTStatus.STATUS_SUCCESS || data is null || data.Length == 0)
                    {
                        throw new IOException($"Reading back the temporary file of {Show(path)} on {endpoint} failed at byte {offset:N0}: {Describe(status)}.");
                    }

                    status = store.WriteFile(out var written, target, offset, data);
                    if (status != NTStatus.STATUS_SUCCESS || written != data.Length)
                    {
                        throw new IOException($"Writing {Show(path)} on {endpoint} failed at byte {offset:N0}: {Describe(status)}.");
                    }

                    offset += data.Length;
                }

                complete = true;
            }
            finally
            {
                if (!complete)
                {
                    // A partial file must not look restored.
                    store.SetFileInformation(target, new FileDispositionInformation { DeletePending = true });
                }

                store.CloseFile(target);
            }
        }

        /// <summary>Further names for the rename: DFS forms, then the folder the server reports for the open file.</summary>
        private IEnumerable<string> RenameCandidates(object handle, string path)
        {
            foreach (var prefix in dfsPrefixes)
            {
                yield return prefix + "\\" + path;
            }

            if (ActualName(handle).Name is { } actual)
            {
                var slash = actual.LastIndexOf('\\');
                var name = path[(path.LastIndexOf('\\') + 1)..];
                var candidate = slash < 0 ? name : actual[..slash] + "\\" + name;
                if (!string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                {
                    yield return candidate;
                }
            }
        }

        /// <summary>Where the server says an open file is, relative to its share (no leading "\\"), or why it does not say.</summary>
        private (string? Name, NTStatus Status) ActualName(object handle)
        {
            var status = store.GetFileInformation(out var info, handle, FileInformationClass.FileAllInformation);
            return status == NTStatus.STATUS_SUCCESS && info is FileAllInformation all && !string.IsNullOrEmpty(all.NameInformation.FileName)
                ? (all.NameInformation.FileName.TrimStart('\\'), status)
                : (null, status);
        }

        /// <summary>
        /// The server does not accept the rename as such (the name, or renaming at all), as opposed to a conflict or missing
        /// rights. Seen behind DFS namespaces: OBJECT_PATH_NOT_FOUND, and INVALID_PARAMETER.
        /// </summary>
        private static bool RenameNameRejected(NTStatus status) => status is NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_OBJECT_NAME_NOT_FOUND
            or NTStatus.STATUS_OBJECT_NAME_INVALID or NTStatus.STATUS_PATH_NOT_COVERED or NTStatus.STATUS_INVALID_PARAMETER
            or NTStatus.STATUS_NOT_SUPPORTED;

        private NTStatus Rename(object handle, string path, bool replace) =>
            store.SetFileInformation(handle, new FileRenameInformationType2 { FileName = path, ReplaceIfExists = replace });

        /// <summary>A read-only file cannot be replaced; clear the flag (the restored file brings its own attributes).</summary>
        private bool ClearReadOnly(string path)
        {
            var status = store.CreateFile(out var handle, out _, path, WriteAttributes | AccessMask.SYNCHRONIZE, 0,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_OPEN_REPARSE_POINT | Sync, null);
            if (status != NTStatus.STATUS_SUCCESS)
            {
                return false;
            }

            try
            {
                return store.SetFileInformation(handle, new FileBasicInformation { FileAttributes = FileAttributes.Normal }) == NTStatus.STATUS_SUCCESS;
            }
            finally
            {
                store.CloseFile(handle);
            }
        }

        private static SetFileTime Utc(DateTime? value) =>
            new(value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null);

        private static int ReadFull(Stream content, byte[] buffer)
        {
            var total = 0;
            int n;
            while (total < buffer.Length && (n = content.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += n;
            }

            return total;
        }

        private void Check(NTStatus status, string action, string path)
        {
            if (status != NTStatus.STATUS_SUCCESS)
            {
                throw new IOException($"{action} {Show(path)} on {endpoint} failed: {Describe(status)}.");
            }
        }

        private string Show(string path) => path.Length == 0 ? root : $@"{root}\{path}";

        private static string Describe(NTStatus status) => status switch
        {
            NTStatus.STATUS_ACCESS_DENIED => "access denied",
            NTStatus.STATUS_SHARING_VIOLATION => "the file is in use",
            NTStatus.STATUS_DISK_FULL => "the share is full",
            NTStatus.STATUS_OBJECT_NAME_INVALID => "the name is not allowed there",
            NTStatus.STATUS_OBJECT_PATH_NOT_FOUND => "the folder does not exist",
            NTStatus.STATUS_MEDIA_WRITE_PROTECTED => "the share is read-only",
            _ => status.ToString(),
        };
    }
}

/// <summary>Windows file name rules (what an SMB server on Windows accepts).</summary>
public static class WindowsNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Why <paramref name="name"/> is not a valid Windows file name, or null.</summary>
    public static string? Check(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var bad = name.FirstOrDefault(c => c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || c < ' ');
        if (bad != default)
        {
            return bad < ' ' ? "the name contains a control character, which Windows does not allow" : $"the name contains '{bad}', which Windows does not allow";
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            return "the name ends with a space or a dot, which Windows does not allow";
        }

        var stem = name.Split('.')[0].TrimEnd(' ');
        if (Reserved.Contains(stem))
        {
            return $"{stem} is a reserved device name on Windows";
        }

        return name.Length > 255 ? "the name is longer than 255 characters" : null;
    }
}
