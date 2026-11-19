using System.Collections.Concurrent;

namespace Backdafuckup.Nfs;

/// <summary>
/// Portmapper v2 (RFC 1833, section 3): tells clients on which port MOUNT and NFS listen. Mappings are registered by this
/// server only; SET and UNSET from the network are refused, CALLIT is not offered (it would amplify traffic).
/// </summary>
public sealed class PortmapperProgram : RpcProgram
{
    public const uint ProgramNumber = 100000;
    public const uint ProtocolTcp = 6;
    public const uint ProtocolUdp = 17;

    private readonly ConcurrentDictionary<(uint Program, uint Version, uint Protocol), uint> _mappings = new();

    public override uint Number => ProgramNumber;

    public override uint MinVersion => 2;

    public override uint MaxVersion => 2;

    public void Register(uint program, uint version, uint protocol, int port) => _mappings[(program, version, protocol)] = (uint)port;

    public override ValueTask<RpcAcceptStatus> HandleAsync(RpcCall call, XdrReader arguments, XdrWriter reply, RpcContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(reply);
        switch (call.Procedure)
        {
            case 0: // NULL
                break;
            case 1: // SET
            case 2: // UNSET
                arguments.ReadUInt32();
                arguments.ReadUInt32();
                arguments.ReadUInt32();
                arguments.ReadUInt32();
                reply.WriteBool(false);
                break;
            case 3: // GETPORT
                var program = arguments.ReadUInt32();
                var version = arguments.ReadUInt32();
                var protocol = arguments.ReadUInt32();
                arguments.ReadUInt32();
                reply.WriteUInt32(_mappings.GetValueOrDefault((program, version, protocol)));
                break;
            case 4: // DUMP
                foreach (var ((program2, version2, protocol2), port) in _mappings.OrderBy(m => m.Key))
                {
                    reply.WriteBool(true).WriteUInt32(program2).WriteUInt32(version2).WriteUInt32(protocol2).WriteUInt32(port);
                }

                reply.WriteBool(false);
                break;
            default:
                return ValueTask.FromResult(RpcAcceptStatus.ProcedureUnavailable);
        }

        return ValueTask.FromResult(RpcAcceptStatus.Success);
    }
}

/// <summary>
/// MOUNT v3 (RFC 1813, appendix I): gives an allowed host the root handle of an export. Hosts outside an export's
/// allowlist get MNT3ERR_ACCES, and the export table reports them for diagnostics.
/// </summary>
public sealed class MountProgram(NfsExportTable exports) : RpcProgram
{
    public const uint ProgramNumber = 100005;
    private const int MaxPathBytes = 1024;
    private readonly ConcurrentDictionary<(string Host, string Path), byte> _mounts = new();

    public override uint Number => ProgramNumber;

    public override uint MinVersion => 3;

    public override uint MaxVersion => 3;

    /// <summary>The hosts that mounted, by address and export path (DUMP).</summary>
    public IReadOnlyCollection<(string Host, string Path)> Mounts => [.. _mounts.Keys];

    public override ValueTask<RpcAcceptStatus> HandleAsync(RpcCall call, XdrReader arguments, XdrWriter reply, RpcContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(context);
        switch (call.Procedure)
        {
            case 0: // NULL
                break;
            case 1: // MNT
                var path = arguments.ReadString(MaxPathBytes);
                var export = exports.FindByPath(path);
                if (export is null)
                {
                    reply.WriteUInt32((uint)NfsStatus.NoEnt);
                }
                else if (!export.Allows(context.Remote.Address))
                {
                    exports.Refused?.Invoke(context.Remote.Address, export.Path);
                    reply.WriteUInt32((uint)NfsStatus.Access);
                }
                else
                {
                    _mounts[(context.Remote.Address.ToString(), export.Path)] = 0;
                    reply.WriteUInt32((uint)NfsStatus.Ok).WriteOpaque(export.FileSystem.RootHandle.Span);
                    reply.WriteUInt32(1).WriteUInt32(RpcConstants.AuthSys); // auth_flavors: AUTH_SYS
                }

                break;
            case 2: // DUMP
                foreach (var (host, mounted) in _mounts.Keys.OrderBy(m => m.Host, StringComparer.Ordinal))
                {
                    reply.WriteBool(true).WriteString(host).WriteString(mounted);
                }

                reply.WriteBool(false);
                break;
            case 3: // UMNT
                _mounts.TryRemove((context.Remote.Address.ToString(), arguments.ReadString(MaxPathBytes)), out _);
                break;
            case 4: // UMNTALL
                foreach (var key in _mounts.Keys.Where(k => k.Host == context.Remote.Address.ToString()))
                {
                    _mounts.TryRemove(key, out _);
                }

                break;
            case 5: // EXPORT: each export with the hosts it allows
                foreach (var item in exports.All)
                {
                    reply.WriteBool(true).WriteString(item.Path);
                    foreach (var host in item.AllowedClients ?? new HashSet<System.Net.IPAddress>())
                    {
                        reply.WriteBool(true).WriteString(host.ToString());
                    }

                    reply.WriteBool(false);
                }

                reply.WriteBool(false);
                break;
            default:
                return ValueTask.FromResult(RpcAcceptStatus.ProcedureUnavailable);
        }

        return ValueTask.FromResult(RpcAcceptStatus.Success);
    }
}
