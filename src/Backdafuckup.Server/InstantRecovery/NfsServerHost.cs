using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Backdafuckup.InstantRecovery;
using Backdafuckup.Nfs;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Hosting;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.InstantRecovery;

/// <summary>The NFS server's state, for the console, the API and error messages.</summary>
/// <param name="Portmapper">How the portmapper is answered: "own" or "rpcbind" (registered with this machine's rpcbind).</param>
public sealed record NfsServerStatus(bool Enabled, bool Listening, string ListenAddress, int PortmapperPort, int NfsPort, int MountPort, string? Portmapper, string? Error)
{
    public static NfsServerStatus Off(InstantRecoveryOptions o) =>
        new(false, false, o.ListenAddress, o.PortmapperPort, o.NfsPort, o.MountPort, null, "The NFS server for instant recovery is turned off (InstantRecovery:Enabled in the server's appsettings.json).");
}

/// <summary>
/// The NFS server inside the backup server that hypervisors mount as the datastore of an instant recovery: portmapper,
/// MOUNT and NFSv3 on the configured address and ports, serving the exports that running instant recoveries add. It
/// listens only when turned on. A port it cannot bind does not stop the backup server: the reason (which program holds
/// the port, a missing capability, an address this server does not have) is kept in <see cref="Status"/>, logged, and
/// tried again when an instant recovery starts.
/// </summary>
public sealed class NfsServerHost : IHostedService, IAsyncDisposable
{
    private readonly InstantRecoveryOptions _options;
    private readonly CredentialProtector _protector;
    private readonly ILogger<NfsServerHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<RpcListener> _listeners = [];
    private readonly Lock _keyLock = new();
    private SystemPortmapper? _registeredWith;
    private NfsServerStatus _status;
    private byte[]? _handleKey;

    public NfsServerHost(IOptions<InstantRecoveryOptions> options, ServerPaths paths, CredentialProtector protector, ILogger<NfsServerHost> logger, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        _options = options.Value;
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Clients = new NfsClientLog(time ?? throw new ArgumentNullException(nameof(time)));
        OverlayDirectory = string.IsNullOrWhiteSpace(_options.OverlayDirectory) ? Path.Combine(paths.DataDirectory, "instant-recovery") : _options.OverlayDirectory;
        _status = _options.Enabled
            ? new NfsServerStatus(true, false, _options.ListenAddress, _options.PortmapperPort, _options.NfsPort, _options.MountPort, null, "The NFS server has not started yet.")
            : NfsServerStatus.Off(_options);
        Exports.Refused = (address, path) =>
        {
            Clients.RecordRefusal(address, path);
            _logger.LogWarning("Refused NFS client {Address} for export {Path}: the address is not in the export's allowlist (the hypervisor host's VMkernel addresses)", address, path);
        };
    }

    public NfsExportTable Exports { get; } = new();

    /// <summary>Which hosts reached which ports, for "the host never reached port 2049" in a failed mount.</summary>
    public NfsClientLog Clients { get; }

    public string OverlayDirectory { get; }

    public InstantRecoveryOptions Options => _options;

    public NfsServerStatus Status => Volatile.Read(ref _status);

    /// <summary>The ports a hypervisor must reach, as they are bound now (for <see cref="NfsClientLog.Describe"/>).</summary>
    public IReadOnlyList<(int Port, string Transport, string Role)> ExpectedPorts
    {
        get
        {
            var s = Status;
            var ports = new List<(int, string, string)>();
            if (s.Portmapper == "own")
            {
                ports.Add((s.PortmapperPort, "tcp", "portmapper"));
            }

            if (s.MountPort != s.NfsPort)
            {
                ports.Add((s.MountPort, "tcp", "MOUNT"));
            }

            ports.Add((s.NfsPort, "tcp", s.MountPort == s.NfsPort ? "MOUNT and NFS" : "NFS"));
            return ports;
        }
    }

    /// <summary>
    /// The key of the file handles' HMAC: random once per installation, kept protected like stored credentials, so handles
    /// stay valid across restarts (the hypervisor reconnects without stale handles).
    /// </summary>
    public byte[] HandleKey
    {
        get
        {
            lock (_keyLock)
            {
                return _handleKey ??= LoadOrCreateHandleKey();
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("The NFS server for instant recovery is turned off (InstantRecovery:Enabled); no NFS ports are opened");
            return;
        }

        await EnsureListeningAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => CloseAsync(cancellationToken);

    /// <summary>Binds the ports unless they are bound already; the status says why not.</summary>
    public async Task<NfsServerStatus> EnsureListeningAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Status;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Status.Listening)
            {
                return Status;
            }

            try
            {
                var status = await ListenAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _status, status);
                _logger.LogInformation(
                    "NFS server for instant recovery listening on {Address}: NFS {NfsPort}/tcp, MOUNT {MountPort}, portmapper {Portmapper}",
                    status.ListenAddress, status.NfsPort, status.MountPort, status.Portmapper == "own" ? $"{status.PortmapperPort} (own)" : "registered with rpcbind");
            }
            catch (NfsHostException ex)
            {
                await CloseListenersAsync().ConfigureAwait(false);
                Volatile.Write(ref _status, Status with { Listening = false, Error = ex.Message });
                _logger.LogError("Instant recovery is not available: {Error}", ex.Message);
            }

            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void AddExport(NfsExport export) => Exports.Add(export);

    public bool RemoveExport(ulong exportId) => Exports.Remove(exportId);

    private async Task<NfsServerStatus> ListenAsync(CancellationToken cancellationToken)
    {
        var o = _options;
        if (!IPAddress.TryParse(o.ListenAddress, out var address))
        {
            throw new NfsHostException($"InstantRecovery:ListenAddress \"{o.ListenAddress}\" is not an IP address.");
        }

        if (!address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && !LocalAddresses().Contains(address))
        {
            throw new NfsHostException($"InstantRecovery:ListenAddress {address} is not an address of this server (its addresses: {string.Join(", ", LocalAddresses())}).");
        }

        if (o.PortmapperMode != PortmapperMode.System && o.PortmapperPort != 0 && (o.PortmapperPort == o.NfsPort || o.PortmapperPort == o.MountPort))
        {
            throw new NfsHostException($"InstantRecovery:PortmapperPort {o.PortmapperPort} is also the NFS or MOUNT port; give each its own port.");
        }

        if (o.PortmapperMode == PortmapperMode.System && !OperatingSystem.IsLinux())
        {
            throw new NfsHostException("InstantRecovery:PortmapperMode System registers with rpcbind, which exists on Linux only; use Own or Automatic.");
        }

        try
        {
            Directory.CreateDirectory(OverlayDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NfsHostException($"Cannot create the overlay directory {OverlayDirectory} (InstantRecovery:OverlayDirectory): {ex.Message}", ex);
        }

        var portmapper = new PortmapperProgram();
        var limits = new NfsServerLimits { MaxTransfer = o.MaxTransferBytes, PreferredTransfer = o.MaxTransferBytes };
        var dispatcher = new RpcDispatcher(
            [portmapper, new MountProgram(Exports), new Nfs3Program(Exports, limits)],
            log: message => _logger.LogDebug("NFS: {Message}", message),
            replies: new RpcReplyCache(o.ReplyCacheLifetime));
        var rpc = new RpcServerOptions { MaxRecordBytes = o.MaxTransferBytes + 4096, MaxInFlight = o.MaxInFlightPerConnection };

        // Port 0 (any free port, for tests) is never "the same port".
        var shared = o.MountPort != 0 && o.MountPort == o.NfsPort;
        var nfs = Bind(new IPEndPoint(address, o.NfsPort), dispatcher, rpc, udp: false, shared ? "MOUNT and NFS" : "NFS");
        var mount = shared ? nfs : Bind(new IPEndPoint(address, o.MountPort), dispatcher, rpc, o.Udp, "MOUNT");
        var nfsEndpoint = nfs.Endpoint;
        var mountEndpoint = mount.Endpoint;

        string mode;
        var portmapperPort = o.PortmapperPort;
        if (o.PortmapperMode == PortmapperMode.System)
        {
            await RegisterWithRpcbindAsync(nfsEndpoint, mountEndpoint, cancellationToken).ConfigureAwait(false);
            mode = "rpcbind";
        }
        else
        {
            try
            {
                var own = Bind(new IPEndPoint(address, o.PortmapperPort), dispatcher, rpc, o.Udp, "portmapper");
                portmapperPort = own.Endpoint.Port;
                mode = "own";
            }
            catch (NfsHostException ex) when (o.PortmapperMode == PortmapperMode.Automatic && ex.Bind?.Error is SocketError.AddressAlreadyInUse or SocketError.AccessDenied
                && SystemPortmapper.FindSocket(o.RpcbindSocket) is not null)
            {
                _logger.LogInformation("Port {Port} for the portmapper is not available ({Reason}); registering with this machine's rpcbind instead", o.PortmapperPort, ex.Message);
                await RegisterWithRpcbindAsync(nfsEndpoint, mountEndpoint, cancellationToken).ConfigureAwait(false);
                mode = "rpcbind";
            }
        }

        portmapper.Register(MountProgram.ProgramNumber, 3, PortmapperProgram.ProtocolTcp, mountEndpoint.Port);
        if (mount.Udp)
        {
            portmapper.Register(MountProgram.ProgramNumber, 3, PortmapperProgram.ProtocolUdp, mountEndpoint.Port);
        }

        portmapper.Register(Nfs3Program.ProgramNumber, 3, PortmapperProgram.ProtocolTcp, nfsEndpoint.Port);
        foreach (var listener in _listeners)
        {
            listener.Start();
        }

        return new NfsServerStatus(true, true, address.ToString(), portmapperPort, nfsEndpoint.Port, mountEndpoint.Port, mode, null);
    }

    private RpcListener Bind(IPEndPoint endpoint, RpcDispatcher dispatcher, RpcServerOptions options, bool udp, string role)
    {
        RpcListener? bound = null;
        try
        {
            bound = new RpcListener(
                endpoint, dispatcher, options, udp,
                connected: remote => Contact(remote, "tcp"),
                datagram: remote => Contact(remote, "udp"));
        }
        catch (RpcBindException ex)
        {
            throw new NfsHostException(Explain(ex, role), ex);
        }

        _listeners.Add(bound);
        return bound;

        void Contact(IPEndPoint remote, string transport)
        {
            var port = bound?.Endpoint.Port ?? endpoint.Port;
            if (Clients.Record(remote.Address, port, transport))
            {
                _logger.LogInformation("NFS client {Address} reached {Port}/{Transport} ({Role}) for the first time", remote.Address, port, transport, role);
            }
        }
    }

    /// <summary>Why a port could not be bound, and what to do about it.</summary>
    internal static string Explain(RpcBindException ex, string role)
    {
        var transport = ex.Protocol == ProtocolType.Udp ? "udp" : "tcp";
        var where = $"{ex.Endpoint.Address}:{ex.Endpoint.Port}/{transport} ({role})";
        var portmapperHint = role == "portmapper"
            ? OperatingSystem.IsLinux() ? " Set InstantRecovery:PortmapperMode to System to register with rpcbind instead, or free the port." : " Free the port, or turn off the program that holds it."
            : " Free the port, or choose another one in the InstantRecovery section of appsettings.json (hypervisors expect NFS on 2049).";
        switch (ex.Error)
        {
            case SocketError.AddressAlreadyInUse:
                return $"Cannot listen on {where}: the port is in use by {PortOwners.Describe(ex.Endpoint.Port, ex.Protocol) ?? "another program"}.{portmapperHint}";
            case SocketError.AccessDenied when OperatingSystem.IsLinux() && ex.Endpoint.Port is > 0 and < 1024:
                return $"Cannot listen on {where}: a port below 1024 needs the capability CAP_NET_BIND_SERVICE "
                    + "(AmbientCapabilities=CAP_NET_BIND_SERVICE in the systemd unit, see deploy/linux/backdafuckup.service), or set InstantRecovery:PortmapperMode to System.";
            case SocketError.AccessDenied:
                return $"Cannot listen on {where}: access denied; {PortOwners.Describe(ex.Endpoint.Port, ex.Protocol) ?? "another program"} holds the port exclusively.{portmapperHint}";
            case SocketError.AddressNotAvailable:
                return $"Cannot listen on {where}: {ex.Endpoint.Address} is not an address of this server (its addresses: {string.Join(", ", LocalAddresses())}).";
            default:
                return $"Cannot listen on {where}: {ex.InnerException?.Message} (error {ex.Error}).";
        }
    }

    private async Task RegisterWithRpcbindAsync(IPEndPoint nfs, IPEndPoint mount, CancellationToken cancellationToken)
    {
        var socket = SystemPortmapper.FindSocket(_options.RpcbindSocket)
            ?? throw new NfsHostException($"No rpcbind found to register with (looked for {string.Join(" and ", SystemPortmapper.DefaultSockets)}); install and start rpcbind, or use InstantRecovery:PortmapperMode Own.");
        var rpcbind = new SystemPortmapper(socket);
        var registrations = new List<(uint Program, string Name, ProtocolType Protocol, IPEndPoint Endpoint)>
        {
            (Nfs3Program.ProgramNumber, "NFS v3", ProtocolType.Tcp, nfs),
            (MountProgram.ProgramNumber, "MOUNT v3", ProtocolType.Tcp, mount),
        };
        if (_options.Udp && _listeners.Any(l => l.Endpoint.Equals(mount) && l.Udp))
        {
            registrations.Add((MountProgram.ProgramNumber, "MOUNT v3", ProtocolType.Udp, mount));
        }

        try
        {
            // A mapping to the port we just bound is a leftover of this server (the port is ours now): replace it. Any other
            // mapping belongs to another NFS server on this machine.
            var mappings = await rpcbind.DumpAsync(cancellationToken).ConfigureAwait(false);
            foreach (var program in registrations.Select(r => (r.Program, r.Name)).Distinct())
            {
                var existing = mappings.Where(m => m.Program == program.Program && m.Version == 3).ToList();
                foreach (var m in existing)
                {
                    var ours = registrations.Any(r => r.Program == m.Program && SystemPortmapper.NetId(r.Protocol, r.Endpoint.AddressFamily) == m.NetId
                        && SystemPortmapper.TryParsePort(m.Address, out var port) && port == r.Endpoint.Port);
                    if (!ours)
                    {
                        var port = SystemPortmapper.TryParsePort(m.Address, out var p) ? p.ToString(System.Globalization.CultureInfo.InvariantCulture) : m.Address;
                        throw new NfsHostException($"rpcbind ({socket}) maps {program.Name}/{m.NetId} to port {port} already (registered by {m.Owner}): another NFS server runs on this machine (the kernel's: systemctl disable --now nfs-server).");
                    }
                }

                if (existing.Count > 0 && !await rpcbind.UnsetAsync(program.Program, 3, cancellationToken).ConfigureAwait(false))
                {
                    throw new NfsHostException($"rpcbind ({socket}) has an old registration of {program.Name} (owner {existing[0].Owner}) that this service may not remove; remove it as root: rpcinfo -d {program.Program} 3.");
                }
            }

            foreach (var r in registrations)
            {
                if (!await rpcbind.SetAsync(r.Program, 3, r.Protocol, r.Endpoint, cancellationToken).ConfigureAwait(false))
                {
                    throw new NfsHostException($"rpcbind ({socket}) refused to register {r.Name}/{(r.Protocol == ProtocolType.Udp ? "udp" : "tcp")} at port {r.Endpoint.Port}.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or XdrException)
        {
            throw new NfsHostException($"Cannot register with rpcbind ({socket}): {ex.Message}", ex);
        }

        _registeredWith = rpcbind;
    }

    private async Task CloseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseListenersAsync().ConfigureAwait(false);
            if (Status.Listening)
            {
                Volatile.Write(ref _status, Status with { Listening = false, Error = "The NFS server was stopped." });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CloseListenersAsync()
    {
        if (_registeredWith is { } rpcbind)
        {
            _registeredWith = null;
            foreach (var program in new[] { Nfs3Program.ProgramNumber, MountProgram.ProgramNumber })
            {
                try
                {
                    await rpcbind.UnsetAsync(program, 3, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or XdrException)
                {
                    _logger.LogWarning("Could not remove the registration of program {Program} from rpcbind ({Socket}): {Error}", program, rpcbind.SocketPath, ex.Message);
                }
            }
        }

        foreach (var listener in _listeners)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }

        _listeners.Clear();
    }

    private byte[] LoadOrCreateHandleKey()
    {
        Directory.CreateDirectory(OverlayDirectory);
        var path = Path.Combine(OverlayDirectory, "handle-key.protected");
        if (File.Exists(path))
        {
            try
            {
                return Convert.FromBase64String(_protector.Unprotect(File.ReadAllText(path).Trim()));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                throw new InvalidOperationException(
                    $"The key of the instant recovery file handles ({path}) cannot be read with this server's key ring: {ex.Message}. "
                    + "Restore the key ring (the keys directory of the data directory) from the server's backup.", ex);
            }
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, _protector.Protect(Convert.ToBase64String(key)));
        File.Move(temporary, path, overwrite: false);
        return key;
    }

    private static List<IPAddress> LocalAddresses()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Distinct()];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseListenersAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

/// <summary>The NFS server cannot listen; the message says why and what to do.</summary>
public sealed class NfsHostException(string message, Exception? inner = null) : Exception(message, inner)
{
    public RpcBindException? Bind => InnerException as RpcBindException;
}
