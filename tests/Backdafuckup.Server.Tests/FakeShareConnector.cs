using Backdafuckup.Smb;

namespace Backdafuckup.Server.Tests;

/// <summary>
/// SMB shares as local folders: <c>\\server\share</c> is <c>{root}/server/share</c>. Only the password
/// <see cref="Password"/> is accepted. The real SMB client is checked against Samba (see the spec status).
/// </summary>
internal sealed class FakeShareConnector(string root) : ISmbShareConnector
{
    public const string Password = "share-secret";

    public List<(UncPath Path, string User, string Password)> Connections { get; } = [];

    public ISmbShare Connect(UncPath path, string userName, string password, TimeSpan timeout)
    {
        lock (Connections)
        {
            Connections.Add((path, userName, password));
        }

        var endpoint = $"{path.Server} (192.0.2.10):445";
        if (password != Password)
        {
            throw new UnauthorizedAccessException($"Logon as {userName} at {endpoint} failed: wrong user name or password.");
        }

        var directory = Path.Combine(root, path.Server, path.Share);
        Directory.CreateDirectory(directory);
        return new LocalShare(directory, endpoint, path);
    }

    private sealed class LocalShare(string directory, string endpoint, UncPath location) : ISmbShare
    {
        public string Endpoint => endpoint;

        public UncPath Location => location;

        public IReadOnlyList<string> Referrals => [];

        public IReadOnlyList<string> Notes => [];

        public bool IsConnected => true;

        public SmbEntryKind Probe(string path)
        {
            var full = Full(path);
            if (new FileInfo(full).LinkTarget is not null)
            {
                return SmbEntryKind.ReparsePoint;
            }

            return Directory.Exists(full) ? SmbEntryKind.Directory : File.Exists(full) ? SmbEntryKind.File : SmbEntryKind.Missing;
        }

        public void CreateDirectory(string path)
        {
            if (File.Exists(Full(path)))
            {
                throw new IOException($"{path} exists and is not a folder.");
            }

            Directory.CreateDirectory(Full(path));
        }

        public void WriteFile(string path, Stream content, long length, bool replace, CancellationToken cancellationToken)
        {
            var full = Full(path);
            var temp = Path.Combine(Path.GetDirectoryName(full)!, $".bdfu-restore-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var output = File.Create(temp))
                {
                    content.CopyTo(output);
                    if (output.Length != length)
                    {
                        throw new IOException($"{path}: {output.Length} bytes instead of {length}.");
                    }
                }

                if (!replace && File.Exists(full))
                {
                    throw new IOException($"{path} was created by someone else during the restore; it is not replaced.");
                }

                File.Move(temp, full, overwrite: replace);
            }
            finally
            {
                File.Delete(temp);
            }
        }

        public void SetBasicInformation(string path, bool isDirectory, DateTime? createdUtc, DateTime? modifiedUtc, DateTime? accessedUtc, FileAttributes attributes)
        {
            if (modifiedUtc is not { } modified)
            {
                return;
            }

            if (isDirectory)
            {
                Directory.SetLastWriteTimeUtc(Full(path), modified);
            }
            else
            {
                File.SetLastWriteTimeUtc(Full(path), modified);
            }
        }

        public void Dispose()
        {
        }

        private string Full(string path) => path.Length == 0 ? directory : Path.Combine([directory, .. path.Split('\\')]);
    }
}
