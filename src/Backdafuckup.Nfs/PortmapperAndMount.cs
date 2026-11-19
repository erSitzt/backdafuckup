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

    public override async ValueTask<RpcAcceptStatus> HandleAsync(RpcCall call, XdrReader arguments, XdrWriter reply, RpcContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(context);
        switch (call.Procedure)
        {
            case 0: // NULL
                break;
            case 1: // MNT: an export, or a directory below one (as Linux nfsd allows)
                var path = arguments.ReadString(MaxPathBytes).TrimEnd('/');
                var export = exports.FindByPath(path)
                    ?? exports.All.Where(e => path.StartsWith(e.Path + "/", StringComparison.Ordinal)).MaxBy(e => e.Path.Length);
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
                    var (status, handle) = await ResolveAsync(export, path, new NfsCaller(call.Credential, context.Remote), cancellationToken).ConfigureAwait(false);
                    reply.WriteUInt32((uint)status);
                    if (status == NfsStatus.Ok)
                    {
                        _mounts[(context.Remote.Address.ToString(), path)] = 0;
                        reply.WriteOpaque(handle.Span);
                        reply.WriteUInt32(1).WriteUInt32(RpcConstants.AuthSys); // auth_flavors: AUTH_SYS
                    }
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
                return RpcAcceptStatus.ProcedureUnavailable;
        }

        return RpcAcceptStatus.Success;
    }

    /// <summary>The handle of the export's root, or of the directory the rest of the path names below it.</summary>
    private static async ValueTask<(NfsStatus Status, ReadOnlyMemory<byte> Handle)> ResolveAsync(NfsExport export, string path, NfsCaller caller, CancellationToken cancellationToken)
    {
        var handle = export.FileSystem.RootHandle;
        try
        {
            foreach (var part in path[export.Path.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var (next, attributes) = await export.FileSystem.LookupAsync(caller, handle, part, cancellationToken).ConfigureAwait(false);
                if (attributes.Type != NfsFileType.Directory)
                {
                    return (NfsStatus.NotDir, default);
                }

                handle = next;
            }
        }
        catch (NfsException ex)
        {
            return (ex.Status, default);
        }

        return (NfsStatus.Ok, handle);
    }
}
