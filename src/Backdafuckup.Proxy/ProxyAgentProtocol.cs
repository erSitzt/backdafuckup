using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backdafuckup.Core.Agents;

namespace Backdafuckup.Proxy;

/// <summary>
/// The proxy agent's control WebSocket (outbound from the agent to <see cref="ControlPath"/>, authenticated with the
/// agent's key). The server holds all logic and sends commands; the agent reports its block devices, attaches and
/// releases them, and serves an opened device as NBD over a second WebSocket per stream (<see cref="TunnelPath"/>,
/// authenticated with a one-time token).
/// </summary>
public static class ProxyAgentProtocol
{
    public const int Version = 1;

    public const int MaxMessage = 1024 * 1024;

    /// <summary>The agent can serve a hot-added disk (vSphere HotAdd).</summary>
    public const string CapabilityHotAdd = "hotadd-v1";

    /// <summary>The agent serves opened devices as NBD over tunnels.</summary>
    public const string CapabilityNbd = "nbd-v1";

    /// <summary>Logs in to arrays over iSCSI and reads their snapshot LUNs (the SAN self-check passed).</summary>
    public const string CapabilitySan = "san-v1";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string ControlPath(Guid proxyId) => $"api/agent/proxies/{proxyId:D}/control";

    public static string TunnelPath(Guid proxyId, Guid streamId) => $"api/agent/proxies/{proxyId:D}/tunnel/{streamId:D}";

    public const string EnrollPath = "api/agent/proxies/enroll";

    /// <summary>The agent's program file of the server's package, for its self-update (authenticated with the agent key).</summary>
    public static string ProgramPath(Guid proxyId) => $"api/agent/proxies/{proxyId:D}/package/{ProxySelfUpdate.ProgramName}";

    public static Task SendAsync(WebSocket socket, SemaphoreSlim sendLock, ProxyAgentMessage message, CancellationToken cancellationToken) =>
        AgentSocketFraming.SendAsync(socket, sendLock, message, ReadOnlyMemory<byte>.Empty, Json, cancellationToken);

    public static async Task<ProxyAgentMessage?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken) =>
        (await AgentSocketFraming.ReceiveAsync<ProxyAgentMessage>(socket, MaxMessage, Json, "proxy agent", cancellationToken).ConfigureAwait(false))?.Header;
}

/// <summary>The types of <see cref="ProxyAgentMessage"/>.</summary>
public static class ProxyMessageTypes
{
    /// <summary>Agent → server, first message: who and where the agent is, what it can do, its devices.</summary>
    public const string Hello = "hello";

    /// <summary>Server → agent: rescan the SCSI hosts (after a disk was hot-added or a LUN presented); reply: the devices.</summary>
    public const string Rescan = "rescan";

    /// <summary>Server → agent: wait until a device with this serial (and size) appears; reply: the device.</summary>
    public const string WaitDevice = "wait-device";

    /// <summary>Server → agent: open a device under a handle (read-only, or writable for restores).</summary>
    public const string Open = "open";

    /// <summary>Server → agent: connect a tunnel for this stream and serve the handle's device as NBD on it.</summary>
    public const string Tunnel = "tunnel";

    /// <summary>Server → agent: close the handle and remove the device from the kernel (before the disk is detached).</summary>
    public const string Release = "release";

    /// <summary>
    /// Server → agent, after the hello when the agent's program differs from the server's package: download it from
    /// <see cref="ProxyAgentMessage.Path"/>, check it, swap it in and exit (the service manager restarts the agent). Refused
    /// while disks are open.
    /// </summary>
    public const string Update = "update";

    /// <summary>Server → agent: log in to an array's iSCSI target at these portals (once), then rescan its sessions.</summary>
    public const string IscsiLogin = "iscsi-login";

    /// <summary>Agent → server: the answer to a command (same id); <see cref="ProxyAgentMessage.Error"/> when it failed.</summary>
    public const string Reply = "reply";
}

/// <summary>A block device of the proxy, as the agent reports it.</summary>
/// <param name="Name">The kernel name ("sdb").</param>
/// <param name="Serial">The SCSI serial (VPD 0x80/0x83 without the NAA prefix, lower case), e.g. a vSphere disk's UUID without dashes.</param>
public sealed record ProxyDevice(string Name, string Path, long SizeBytes, string? Serial, string? Model);

/// <summary>One message of the control connection; which fields are set depends on <see cref="Type"/>.</summary>
public sealed record ProxyAgentMessage
{
    public required string Type { get; init; }

    /// <summary>Command id; the reply carries the same.</summary>
    public long Id { get; init; }

    public string? Error { get; init; }

    // hello
    public int? ProtocolVersion { get; init; }

    public string? AgentVersion { get; init; }

    public string? FilesHash { get; init; }

    /// <summary>update: the SHA-256 of the program file to download (with its size in <see cref="SizeBytes"/>, its version in <see cref="AgentVersion"/>).</summary>
    public string? Sha256 { get; init; }

    public string? HostName { get; init; }

    public string? SmbiosUuid { get; init; }

    public string? BiosSerial { get; init; }

    public IReadOnlyList<string>? Addresses { get; init; }

    public IReadOnlyList<string>? Capabilities { get; init; }

    public IReadOnlyList<ProxyDevice>? Devices { get; init; }

    /// <summary>hello: the machine's iSCSI initiator name (for the array's host object).</summary>
    public string? InitiatorName { get; init; }

    /// <summary>hello: why the proxy offers no SAN access (the self-check's finding), or null.</summary>
    public string? SanRefusal { get; init; }

    // iscsi-login
    public IReadOnlyList<string>? Portals { get; init; }

    public string? TargetIqn { get; init; }

    // wait-device
    public string? Serial { get; init; }

    public long? SizeBytes { get; init; }

    public int? TimeoutMs { get; init; }

    // open, tunnel, release
    public string? Handle { get; init; }

    public string? Path { get; init; }

    public bool? Writable { get; init; }

    public Guid? StreamId { get; init; }

    public string? Token { get; init; }

    /// <summary>release: also remove the device from the kernel (a hot-added disk before it is detached).</summary>
    public bool? DeleteDevice { get; init; }

    // reply
    public ProxyDevice? Device { get; init; }
}
