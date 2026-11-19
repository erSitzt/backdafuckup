using System.Net;

namespace Backdafuckup.Nfs;

/// <summary>ONC RPC v2 constants (RFC 5531).</summary>
public static class RpcConstants
{
    public const uint Version = 2;
    public const uint Call = 0;
    public const uint Reply = 1;
    public const uint MsgAccepted = 0;
    public const uint MsgDenied = 1;
    public const uint RpcMismatch = 0;
    public const uint AuthError = 1;
    public const uint AuthNone = 0;
    public const uint AuthSys = 1;

    /// <summary>auth_stat: the credential is malformed or of a flavor the server does not take.</summary>
    public const uint AuthBadCred = 1;

    /// <summary>auth_stat: the credential's flavor is too weak for this program.</summary>
    public const uint AuthTooWeak = 5;

    /// <summary>The largest credential or verifier body (RFC 5531: 400 bytes).</summary>
    public const int MaxAuthBytes = 400;
}

/// <summary>The accept_stat of an accepted reply.</summary>
public enum RpcAcceptStatus : uint
{
    Success = 0,
    ProgramUnavailable = 1,
    ProgramMismatch = 2,
    ProcedureUnavailable = 3,
    GarbageArgs = 4,
    SystemError = 5,
}

/// <summary>AUTH_SYS credentials (RFC 5531, appendix A): who the client says it is.</summary>
public sealed record AuthSys(uint Stamp, string MachineName, uint Uid, uint Gid, IReadOnlyList<uint> Gids);

/// <summary>Where a call came from (for allowlists and diagnostics).</summary>
public sealed record RpcContext(IPEndPoint Remote, string Transport);

/// <summary>A decoded call: header, credential and the arguments (a slice of the message).</summary>
public sealed record RpcCall(uint Xid, uint Program, uint ProgramVersion, uint Procedure, uint CredentialFlavor, AuthSys? Credential, ReadOnlyMemory<byte> Arguments);

/// <summary>A program served over RPC (portmapper, MOUNT, NFS).</summary>
public abstract class RpcProgram
{
    public abstract uint Number { get; }

    public abstract uint MinVersion { get; }

    public abstract uint MaxVersion { get; }

    /// <summary>
    /// Handles a call of a supported version: writes the result into <paramref name="reply"/> and returns Success, or
    /// returns ProcedureUnavailable / GarbageArgs / SystemError (nothing written). <see cref="XdrException"/> means GarbageArgs.
    /// </summary>
    public abstract ValueTask<RpcAcceptStatus> HandleAsync(RpcCall call, XdrReader arguments, XdrWriter reply, RpcContext context, CancellationToken cancellationToken);

    /// <summary>A procedure that must not run twice for a retransmitted call (its reply is kept and sent again).</summary>
    public virtual bool IsNonIdempotent(uint procedure) => false;
}

/// <summary>
/// The duplicate request cache: replies of non-idempotent calls by (client address, xid, program, procedure), kept for a
/// while, so that a retransmission (the reply was lost) gets the same answer instead of e.g. NOENT for a second REMOVE.
/// A retransmission of a call still running is dropped; the client sends it again.
/// </summary>
public sealed class RpcReplyCache(TimeSpan window, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(IPAddress Client, uint Xid, uint Program, uint Procedure), Entry> _entries = new();
    private long _nextSweep;

    private sealed class Entry(DateTimeOffset created)
    {
        public DateTimeOffset Created { get; } = created;

        public byte[]? Reply { get; set; }

        public bool Done { get; set; }
    }

    public int Count => _entries.Count;

    /// <summary>Null: run the call (and <see cref="Store"/> its reply). Otherwise the cached reply, or an empty array to drop the call.</summary>
    internal byte[]? TryBegin(IPAddress client, RpcCall call)
    {
        Sweep();
        var key = (client, call.Xid, call.Program, call.Procedure);
        var entry = new Entry(_time.GetUtcNow());
        if (_entries.TryAdd(key, entry))
        {
            return null;
        }

        var existing = _entries[key];
        lock (existing)
        {
            return existing.Done ? existing.Reply ?? [] : [];
        }
    }

    internal void Store(IPAddress client, RpcCall call, byte[]? reply)
    {
        if (_entries.TryGetValue((client, call.Xid, call.Program, call.Procedure), out var entry))
        {
            lock (entry)
            {
                entry.Reply = reply;
                entry.Done = true;
            }
        }
    }

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        if (now.UtcTicks < Interlocked.Read(ref _nextSweep))
        {
            return;
        }

        Interlocked.Exchange(ref _nextSweep, (now + TimeSpan.FromSeconds(10)).UtcTicks);
        foreach (var (key, entry) in _entries)
        {
            if (now - entry.Created > window)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }
}

/// <summary>Decodes calls, routes them to their program, and encodes the replies (RFC 5531).</summary>
public sealed class RpcDispatcher(IEnumerable<RpcProgram> programs, Action<string>? log = null, RpcReplyCache? replies = null)
{
    private readonly IReadOnlyList<RpcProgram> _programs = [.. programs];

    public IReadOnlyList<RpcProgram> Programs => _programs;

    /// <summary>The reply to one call message, or null when the message is no call (or too broken to answer).</summary>
    public async ValueTask<byte[]?> DispatchAsync(ReadOnlyMemory<byte> message, RpcContext context, CancellationToken cancellationToken)
    {
        var reader = new XdrReader(message);
        uint xid;
        try
        {
            xid = reader.ReadUInt32();
            if (reader.ReadUInt32() != RpcConstants.Call)
            {
                return null;
            }
        }
        catch (XdrException)
        {
            return null;
        }

        RpcCall call;
        try
        {
            var rpcVersion = reader.ReadUInt32();
            var program = reader.ReadUInt32();
            var version = reader.ReadUInt32();
            var procedure = reader.ReadUInt32();
            var flavor = reader.ReadUInt32();
            var body = reader.ReadOpaque(RpcConstants.MaxAuthBytes);
            reader.ReadUInt32(); // verifier flavor
            reader.ReadOpaque(RpcConstants.MaxAuthBytes);
            if (rpcVersion != RpcConstants.Version)
            {
                return Denied(xid, w => w.WriteUInt32(RpcConstants.RpcMismatch).WriteUInt32(RpcConstants.Version).WriteUInt32(RpcConstants.Version));
            }

            AuthSys? credential = null;
            if (flavor == RpcConstants.AuthSys)
            {
                try
                {
                    credential = ParseAuthSys(body);
                }
                catch (XdrException)
                {
                    return Denied(xid, w => w.WriteUInt32(RpcConstants.AuthError).WriteUInt32(RpcConstants.AuthBadCred));
                }
            }
            else if (flavor != RpcConstants.AuthNone)
            {
                return Denied(xid, w => w.WriteUInt32(RpcConstants.AuthError).WriteUInt32(RpcConstants.AuthBadCred));
            }

            call = new RpcCall(xid, program, version, procedure, flavor, credential, message[reader.Position..]);
        }
        catch (XdrException)
        {
            return Accepted(xid, RpcAcceptStatus.GarbageArgs, null);
        }

        var target = _programs.FirstOrDefault(p => p.Number == call.Program);
        if (target is null)
        {
            return Accepted(xid, RpcAcceptStatus.ProgramUnavailable, null);
        }

        if (call.ProgramVersion < target.MinVersion || call.ProgramVersion > target.MaxVersion)
        {
            return Accepted(xid, RpcAcceptStatus.ProgramMismatch, w => w.WriteUInt32(target.MinVersion).WriteUInt32(target.MaxVersion));
        }

        var cached = replies is not null && target.IsNonIdempotent(call.Procedure);
        if (cached && replies!.TryBegin(context.Remote.Address, call) is { } known)
        {
            return known.Length == 0 ? null : known;
        }

        var answer = await HandleAsync(target, call, context, cancellationToken).ConfigureAwait(false);
        if (cached)
        {
            replies!.Store(context.Remote.Address, call, answer);
        }

        return answer;
    }

    private async ValueTask<byte[]> HandleAsync(RpcProgram target, RpcCall call, RpcContext context, CancellationToken cancellationToken)
    {
        var xid = call.Xid;
        var result = new XdrWriter();
        RpcAcceptStatus status;
        try
        {
            status = await target.HandleAsync(call, new XdrReader(call.Arguments), result, context, cancellationToken).ConfigureAwait(false);
        }
        catch (XdrException ex)
        {
            log?.Invoke($"RPC {call.Program}/{call.ProgramVersion} procedure {call.Procedure} from {context.Remote}: garbage arguments ({ex.Message}).");
            status = RpcAcceptStatus.GarbageArgs;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            log?.Invoke($"RPC {call.Program}/{call.ProgramVersion} procedure {call.Procedure} from {context.Remote} failed: {ex.Message}");
            status = RpcAcceptStatus.SystemError;
        }

        return Accepted(xid, status, status == RpcAcceptStatus.Success ? w => w.WriteFixedOpaque(result.Written.Span) : null);
    }

    /// <summary>The AUTH_SYS body: stamp, machine name (≤ 255), uid, gid, up to 16 groups.</summary>
    public static AuthSys ParseAuthSys(ReadOnlyMemory<byte> body)
    {
        var reader = new XdrReader(body);
        var stamp = reader.ReadUInt32();
        var machine = reader.ReadString(255);
        var uid = reader.ReadUInt32();
        var gid = reader.ReadUInt32();
        var count = reader.ReadUInt32();
        if (count > 16)
        {
            throw new XdrException($"AUTH_SYS names {count} groups; at most 16 are allowed.");
        }

        var gids = new uint[count];
        for (var i = 0; i < gids.Length; i++)
        {
            gids[i] = reader.ReadUInt32();
        }

        return new AuthSys(stamp, machine, uid, gid, gids);
    }

    private static byte[] Accepted(uint xid, RpcAcceptStatus status, Action<XdrWriter>? body)
    {
        var writer = new XdrWriter();
        writer.WriteUInt32(xid).WriteUInt32(RpcConstants.Reply).WriteUInt32(RpcConstants.MsgAccepted)
            .WriteUInt32(RpcConstants.AuthNone).WriteUInt32(0) // verifier: AUTH_NONE, empty
            .WriteUInt32((uint)status);
        body?.Invoke(writer);
        return writer.ToArray();
    }

    private static byte[] Denied(uint xid, Action<XdrWriter> body)
    {
        var writer = new XdrWriter();
        writer.WriteUInt32(xid).WriteUInt32(RpcConstants.Reply).WriteUInt32(RpcConstants.MsgDenied);
        body(writer);
        return writer.ToArray();
    }
}
