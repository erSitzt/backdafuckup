using System.Net;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Backdafuckup.Smb;

/// <summary>A Windows computer reached over SMB 445 with administrator credentials (ADMIN$/C$, service control manager).</summary>
public interface IRemoteWindows : IDisposable
{
    /// <summary>The address in use, e.g. <c>10.0.0.5</c>.</summary>
    string Target { get; }

    /// <summary>True if the administrative share (C$, ADMIN$) can be opened.</summary>
    bool HasShare(string share);

    void CreateDirectory(string share, string path);

    void WriteFile(string share, string path, Stream content);

    IRemoteServices Services();
}

/// <summary>Services of the remote computer by name (all as Local System, auto start).</summary>
public interface IRemoteServices : IDisposable
{
    ServiceStatus? Query(string name);

    void Create(string name, string displayName, string binaryPath);

    void Start(string name);

    void Stop(string name);

    void Delete(string name);
}

public interface IRemoteWindowsConnector
{
    /// <summary>Connects and logs on; throws with a description naming address, port and account on failure.</summary>
    IRemoteWindows Connect(string address, string userName, string password, TimeSpan timeout);
}

/// <summary>SMB 2/3 with NTLMv2 through SMBLibrary.</summary>
public sealed class SmbRemoteWindowsConnector : IRemoteWindowsConnector
{
    public IRemoteWindows Connect(string address, string userName, string password, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(userName);
        var (domain, user) = SplitUser(userName, address);
        var client = new SMB2Client((int)timeout.TotalMilliseconds, enableSMB311Support: true);
        var connected = IPAddress.TryParse(address, out var ip)
            ? client.Connect(ip, SMBTransportType.DirectTCPTransport)
            : client.Connect(address, SMBTransportType.DirectTCPTransport);
        if (!connected)
        {
            throw new IOException($"No SMB connection to {address}:445 (firewall, or the guest is not a Windows computer with file sharing).");
        }

        var status = client.Login(domain, user, password ?? string.Empty, AuthenticationMethod.NTLMv2);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            client.Disconnect();
            var reason = status switch
            {
                NTStatus.STATUS_LOGON_FAILURE => "wrong user name or password",
                NTStatus.STATUS_ACCOUNT_RESTRICTION or NTStatus.STATUS_ACCOUNT_DISABLED => "the account is restricted or disabled",
                NTStatus.STATUS_ACCESS_DENIED => "access denied (NTLM may be blocked by policy)",
                _ => status.ToString(),
            };
            throw new UnauthorizedAccessException($"Logon as {userName} at {address}:445 failed: {reason}.");
        }

        return new SmbRemoteWindows(client, address);
    }

    /// <summary><c>DOMAIN\user</c>, <c>user@domain</c>, <c>.\user</c> (local account of the target) or <c>user</c>.</summary>
    internal static (string Domain, string User) SplitUser(string userName, string address)
    {
        var backslash = userName.IndexOf('\\', StringComparison.Ordinal);
        if (backslash > 0)
        {
            // ".\user": a local account of the target; an empty domain makes Windows check its own accounts.
            var domain = userName[..backslash];
            return (domain == "." ? string.Empty : domain, userName[(backslash + 1)..]);
        }

        if (userName.Contains('@', StringComparison.Ordinal))
        {
            return (string.Empty, userName);
        }

        return (string.Empty, userName);
    }

    private sealed class SmbRemoteWindows(SMB2Client client, string address) : IRemoteWindows
    {
        private readonly Dictionary<string, ISMBFileStore> _shares = new(StringComparer.OrdinalIgnoreCase);

        public string Target => address;

        public bool HasShare(string share) => TryShare(share, out _) == NTStatus.STATUS_SUCCESS;

        public void CreateDirectory(string share, string path)
        {
            var store = Share(share);
            var current = string.Empty;
            foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current.Length == 0 ? part : current + "\\" + part;
                var status = store.CreateFile(out var handle, out _, current, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Directory,
                    ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN_IF, CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
                if (status != NTStatus.STATUS_SUCCESS)
                {
                    throw new IOException($"Creating \\\\{address}\\{share}\\{current} failed: {status}.");
                }

                store.CloseFile(handle);
            }
        }

        public void WriteFile(string share, string path, Stream content)
        {
            var store = Share(share);
            var status = store.CreateFile(out var handle, out _, path, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
                ShareAccess.None, CreateDisposition.FILE_OVERWRITE_IF, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
            if (status != NTStatus.STATUS_SUCCESS)
            {
                var hint = status == NTStatus.STATUS_SHARING_VIOLATION ? " (the file is in use)" : string.Empty;
                throw new IOException($"Writing \\\\{address}\\{share}\\{path} failed: {status}{hint}.");
            }

            try
            {
                var buffer = new byte[(int)Math.Min(client.MaxWriteSize, 1024 * 1024)];
                long offset = 0;
                int read;
                while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
                {
                    var chunk = read == buffer.Length ? buffer : buffer[..read];
                    status = store.WriteFile(out var written, handle, offset, chunk);
                    if (status != NTStatus.STATUS_SUCCESS || written != read)
                    {
                        throw new IOException($"Writing \\\\{address}\\{share}\\{path} failed at byte {offset}: {status}.");
                    }

                    offset += read;
                }
            }
            finally
            {
                store.CloseFile(handle);
            }
        }

        public IRemoteServices Services() => new RemoteServices(ScmClient.Open(Share("IPC$"), $"{address}"), address);

        public void Dispose()
        {
            foreach (var share in _shares.Values)
            {
                share.Disconnect();
            }

            client.Logoff();
            client.Disconnect();
        }

        private ISMBFileStore Share(string share)
        {
            var status = TryShare(share, out var store);
            if (status != NTStatus.STATUS_SUCCESS)
            {
                var hint = status == NTStatus.STATUS_ACCESS_DENIED
                    ? " The account needs administrator rights there; local accounts other than the built-in Administrator are restricted by UAC for network logons (LocalAccountTokenFilterPolicy)."
                    : status == NTStatus.STATUS_BAD_NETWORK_NAME ? " The administrative shares are disabled on the target." : string.Empty;
                throw new UnauthorizedAccessException($"\\\\{address}\\{share} cannot be opened: {status}.{hint}");
            }

            return store!;
        }

        private NTStatus TryShare(string share, out ISMBFileStore? store)
        {
            if (_shares.TryGetValue(share, out store))
            {
                return NTStatus.STATUS_SUCCESS;
            }

            store = client.TreeConnect(share, out var status);
            if (status == NTStatus.STATUS_SUCCESS && store is not null)
            {
                _shares[share] = store;
            }

            return status;
        }
    }

    private sealed class RemoteServices(ScmClient scm, string address) : IRemoteServices
    {
        private readonly ScmHandle _manager = scm.OpenManager(ScmClient.ScManagerAllAccess);

        public ServiceStatus? Query(string name)
        {
            var service = scm.Open(_manager, name, ScmClient.ServiceAllAccess);
            if (service is null)
            {
                return null;
            }

            try
            {
                return scm.Query(service);
            }
            finally
            {
                scm.Close(service);
            }
        }

        public void Create(string name, string displayName, string binaryPath) =>
            scm.Close(scm.Create(_manager, name, displayName, binaryPath, ScmClient.ServiceAutoStart));

        public void Start(string name) => With(name, scm.Start);

        public void Stop(string name) => With(name, scm.Stop);

        public void Delete(string name) => With(name, scm.Delete);

        public void Dispose()
        {
            scm.Close(_manager);
            scm.Dispose();
        }

        private void With(string name, Action<ScmHandle> action)
        {
            var service = scm.Open(_manager, name, ScmClient.ServiceAllAccess)
                ?? throw new ScmException($"Opening the service {name} on {address}", ScmException.ServiceDoesNotExist);
            try
            {
                action(service);
            }
            finally
            {
                scm.Close(service);
            }
        }
    }
}
