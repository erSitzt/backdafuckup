using System.Net;
using System.Net.Sockets;

namespace Backdafuckup.Nfs;

/// <summary>One registration in rpcbind: program, version, transport ("tcp", "udp6"), universal address and owner.</summary>
public sealed record RpcbMapping(uint Program, uint Version, string NetId, string Address, string Owner);

/// <summary>
/// The rpcbind of this machine (Linux), reached through its Unix socket. rpcbind takes registrations (RPCBPROC_SET) only
/// from local callers, which on current versions means this socket; it records the caller's uid as the owner, and only
/// the owner (or root) may remove a registration again. Used when port 111 belongs to rpcbind already.
/// </summary>
public sealed class SystemPortmapper(string socketPath)
{
    public const uint RpcbProgram = 100000;
    public const uint RpcbVersion = 3;
    private const uint ProcSet = 1;
    private const uint ProcUnset = 2;
    private const uint ProcDump = 4;

    /// <summary>The usual places of rpcbind's socket.</summary>
    public static readonly IReadOnlyList<string> DefaultSockets = ["/run/rpcbind.sock", "/var/run/rpcbind.sock"];

    public string SocketPath { get; } = socketPath ?? throw new ArgumentNullException(nameof(socketPath));

    /// <summary>rpcbind's socket on this machine, or null when there is none (no rpcbind, or not Linux).</summary>
    public static string? FindSocket(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        return DefaultSockets.FirstOrDefault(path => File.Exists(path) && (File.GetAttributes(path) & FileAttributes.Directory) == 0);
    }

    /// <summary>Registers <paramref name="program"/> on a transport; false when rpcbind has a mapping for it already.</summary>
    public async Task<bool> SetAsync(uint program, uint version, ProtocolType protocol, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var reply = await CallAsync(ProcSet, w => WriteRpcb(w, program, version, NetId(protocol, endpoint.AddressFamily), UniversalAddress(endpoint)), cancellationToken).ConfigureAwait(false);
        return reply.ReadBool();
    }

    /// <summary>Removes the registrations of <paramref name="program"/> on all transports (rpcbind allows it to their owner only).</summary>
    public async Task<bool> UnsetAsync(uint program, uint version, CancellationToken cancellationToken)
    {
        var reply = await CallAsync(ProcUnset, w => WriteRpcb(w, program, version, string.Empty, string.Empty), cancellationToken).ConfigureAwait(false);
        return reply.ReadBool();
    }

    /// <summary>The port rpcbind maps <paramref name="program"/> to on a transport, or null when it has no mapping.</summary>
    public async Task<int?> GetPortAsync(uint program, uint version, ProtocolType protocol, AddressFamily family, CancellationToken cancellationToken)
    {
        var netId = NetId(protocol, family);
        var mapping = (await DumpAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(m => m.Program == program && m.Version == version && m.NetId == netId);
        return mapping is not null && TryParsePort(mapping.Address, out var port) ? port : null;
    }

    /// <summary>
    /// All registrations (RPCBPROC_DUMP), with their owners ("superuser", or the uid of a local caller). GETADDR is no use
    /// here: rpcbind merges the answer with the caller's address, which a caller on the Unix socket does not have.
    /// </summary>
    public async Task<IReadOnlyList<RpcbMapping>> DumpAsync(CancellationToken cancellationToken)
    {
        var reply = await CallAsync(ProcDump, null, cancellationToken).ConfigureAwait(false);
        var mappings = new List<RpcbMapping>();
        while (reply.ReadBool())
        {
            mappings.Add(new RpcbMapping(reply.ReadUInt32(), reply.ReadUInt32(), reply.ReadString(64), reply.ReadString(256), reply.ReadString(256)));
        }

        return mappings;
    }

    /// <summary>The universal address of RFC 5665: "h1.h2.h3.h4.p1.p2" for IPv4, the text form plus ".p1.p2" for IPv6.</summary>
    public static string UniversalAddress(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
        return $"{address}.{endpoint.Port >> 8}.{endpoint.Port & 0xff}";
    }

    public static bool TryParsePort(string? universalAddress, out int port)
    {
        port = 0;
        if (string.IsNullOrEmpty(universalAddress))
        {
            return false;
        }

        var last = universalAddress.LastIndexOf('.');
        var previous = last > 0 ? universalAddress.LastIndexOf('.', last - 1) : -1;
        if (previous < 0
            || !int.TryParse(universalAddress.AsSpan(previous + 1, last - previous - 1), out var high)
            || !int.TryParse(universalAddress.AsSpan(last + 1), out var low)
            || high is < 0 or > 255 || low is < 0 or > 255)
        {
            return false;
        }

        port = (high << 8) | low;
        return true;
    }

    public static string NetId(ProtocolType protocol, AddressFamily family) => (protocol, family) switch
    {
        (ProtocolType.Tcp, AddressFamily.InterNetworkV6) => "tcp6",
        (ProtocolType.Udp, AddressFamily.InterNetworkV6) => "udp6",
        (ProtocolType.Tcp, _) => "tcp",
        (ProtocolType.Udp, _) => "udp",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Only TCP and UDP are registered."),
    };

    private static void WriteRpcb(XdrWriter writer, uint program, uint version, string netId, string address) =>
        writer.WriteUInt32(program).WriteUInt32(version).WriteString(netId).WriteString(address).WriteString("backdafuckup");

    private async Task<XdrReader> CallAsync(uint procedure, Action<XdrWriter>? arguments, CancellationToken cancellationToken)
    {
        await using var client = await RpcClient.ConnectUnixAsync(SocketPath, cancellationToken).ConfigureAwait(false);
        return await client.CallAsync(RpcbProgram, RpcbVersion, procedure, arguments, cancellationToken).ConfigureAwait(false);
    }
}
