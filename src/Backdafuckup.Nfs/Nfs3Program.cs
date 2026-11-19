namespace Backdafuckup.Nfs;

/// <summary>What the server announces in FSINFO and PATHCONF.</summary>
public sealed record NfsServerLimits
{
    /// <summary>Largest READ and WRITE (rtmax/wtmax); the record limit of the transport must allow it.</summary>
    public int MaxTransfer { get; init; } = 1024 * 1024;

    public int PreferredTransfer { get; init; } = 1024 * 1024;

    public int PreferredDirectoryRead { get; init; } = 64 * 1024;

    public ulong MaxFileSize { get; init; } = (1UL << 44) - 1;

    public uint MaxName { get; init; } = 255;
}

/// <summary>
/// NFS version 3 (RFC 1813) over the exports of an <see cref="NfsExportTable"/>: every handle is routed to its export, which
/// must allow the caller's address, and the call goes to that export's <see cref="INfsFileSystem"/>. LINK, SYMLINK, MKNOD
/// and READLINK answer NFS3ERR_NOTSUPP. Errors carry no attributes (they are optional in every reply).
/// </summary>
public sealed class Nfs3Program(NfsExportTable exports, NfsServerLimits limits, Action<NfsCallRecord>? observe = null) : RpcProgram
{
    public const uint ProgramNumber = 100003;
    private const int MaxNameBytes = 1024;

    public override uint Number => ProgramNumber;

    public override uint MinVersion => 3;

    public override uint MaxVersion => 3;

    private static readonly string[] Names =
    [
        "NULL", "GETATTR", "SETATTR", "LOOKUP", "ACCESS", "READLINK", "READ", "WRITE", "CREATE", "MKDIR", "SYMLINK", "MKNOD", "REMOVE", "RMDIR", "RENAME", "LINK",
        "READDIR", "READDIRPLUS", "FSSTAT", "FSINFO", "PATHCONF", "COMMIT",
    ];

    /// <summary>The name of a procedure (for logs and metrics).</summary>
    public static string ProcedureName(uint procedure) => procedure < Names.Length ? Names[procedure] : $"PROC{procedure}";

    public override async ValueTask<RpcAcceptStatus> HandleAsync(RpcCall call, XdrReader arguments, XdrWriter reply, RpcContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(context);
        if (call.Procedure >= Names.Length)
        {
            return RpcAcceptStatus.ProcedureUnavailable;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var caller = new NfsCaller(call.Credential, context.Remote);
        var status = NfsStatus.Ok;
        var body = new XdrWriter();
        try
        {
            await RunAsync(call.Procedure, arguments, body, caller, cancellationToken).ConfigureAwait(false);
        }
        catch (NfsException ex)
        {
            status = ex.Status;
            body = new XdrWriter();
            WriteFailure(call.Procedure, body);
        }

        if (call.Procedure != 0)
        {
            reply.WriteUInt32((uint)status);
        }

        reply.WriteFixedOpaque(body.Written.Span);
        observe?.Invoke(new NfsCallRecord(ProcedureName(call.Procedure), status, context.Remote, System.Diagnostics.Stopwatch.GetElapsedTime(started)));
        return RpcAcceptStatus.Success;
    }

    private async ValueTask RunAsync(uint procedure, XdrReader args, XdrWriter res, NfsCaller caller, CancellationToken ct)
    {
        switch (procedure)
        {
            case 0: // NULL
                return;
            case 1: // GETATTR
            {
                var (fs, handle) = Resolve(args, caller);
                WriteAttributes(res, await fs.GetAttributesAsync(caller, handle, ct).ConfigureAwait(false));
                return;
            }

            case 2: // SETATTR
            {
                var (fs, handle) = Resolve(args, caller);
                var attributes = ReadSetAttributes(args);
                NfsTime? guard = args.ReadBool() ? ReadTime(args) : null;
                WriteWcc(res, await fs.SetAttributesAsync(caller, handle, attributes, guard, ct).ConfigureAwait(false));
                return;
            }

            case 3: // LOOKUP
            {
                var (fs, directory) = Resolve(args, caller);
                var name = ReadName(args);
                var (handle, attributes) = await fs.LookupAsync(caller, directory, name, ct).ConfigureAwait(false);
                res.WriteOpaque(handle.Span);
                WritePostOp(res, attributes);
                WritePostOp(res, await TryAttributesAsync(fs, caller, directory, ct).ConfigureAwait(false));
                return;
            }

            case 4: // ACCESS: AUTH_SYS root on an export it may use gets what the object's type allows
            {
                var (fs, handle) = Resolve(args, caller);
                var asked = args.ReadUInt32();
                var attributes = await fs.GetAttributesAsync(caller, handle, ct).ConfigureAwait(false);
                const uint read = 0x01, lookup = 0x02, modify = 0x04, extend = 0x08, delete = 0x10, execute = 0x20;
                var possible = attributes.Type == NfsFileType.Directory ? read | lookup | modify | extend | delete : read | modify | extend | delete | execute;
                WritePostOp(res, attributes);
                res.WriteUInt32(asked & possible);
                return;
            }

            case 5: // READLINK
                Resolve(args, caller);
                throw new NfsException(NfsStatus.NotSupported, "Symbolic links are not supported.");
            case 6: // READ
            {
                var (fs, handle) = Resolve(args, caller);
                var offset = args.ReadUInt64();
                var count = (int)Math.Min(args.ReadUInt32(), (uint)limits.MaxTransfer);
                var result = await fs.ReadAsync(caller, handle, checked((long)offset), count, ct).ConfigureAwait(false);
                WritePostOp(res, result.Attributes);
                res.WriteUInt32((uint)result.Data.Length).WriteBool(result.Eof).WriteOpaque(result.Data.Span);
                return;
            }

            case 7: // WRITE
            {
                var (fs, handle) = Resolve(args, caller);
                var offset = args.ReadUInt64();
                var count = args.ReadUInt32();
                var stable = (NfsStable)args.ReadUInt32();
                var data = args.ReadOpaque(limits.MaxTransfer);
                if (count != data.Length || stable > NfsStable.FileSync)
                {
                    throw new NfsException(NfsStatus.Inval, $"WRITE names {count} bytes and stability {(uint)stable} but carries {data.Length} bytes.");
                }

                var result = await fs.WriteAsync(caller, handle, checked((long)offset), data, stable, ct).ConfigureAwait(false);
                WriteWcc(res, result.Wcc);
                res.WriteUInt32((uint)result.Count).WriteUInt32((uint)result.Committed).WriteUInt64(fs.WriteVerifier);
                return;
            }

            case 8: // CREATE
            {
                var (fs, directory) = Resolve(args, caller);
                var name = ReadName(args);
                var mode = (NfsCreateMode)args.ReadUInt32();
                var attributes = NfsSetAttributes.None;
                ulong verifier = 0;
                switch (mode)
                {
                    case NfsCreateMode.Unchecked or NfsCreateMode.Guarded:
                        attributes = ReadSetAttributes(args);
                        break;
                    case NfsCreateMode.Exclusive:
                        verifier = args.ReadUInt64();
                        break;
                    default:
                        throw new XdrException($"CREATE mode {(uint)mode} is unknown.");
                }

                WriteCreated(res, await fs.CreateAsync(caller, directory, name, mode, attributes, verifier, ct).ConfigureAwait(false));
                return;
            }

            case 9: // MKDIR
            {
                var (fs, directory) = Resolve(args, caller);
                var name = ReadName(args);
                WriteCreated(res, await fs.MakeDirectoryAsync(caller, directory, name, ReadSetAttributes(args), ct).ConfigureAwait(false));
                return;
            }

            case 10: // SYMLINK
            case 11: // MKNOD
            case 15: // LINK
                Resolve(args, caller);
                throw new NfsException(NfsStatus.NotSupported, $"{ProcedureName(procedure)} is not supported.");
            case 12: // REMOVE
            case 13: // RMDIR
            {
                var (fs, directory) = Resolve(args, caller);
                var name = ReadName(args);
                WriteWcc(res, procedure == 12
                    ? await fs.RemoveAsync(caller, directory, name, ct).ConfigureAwait(false)
                    : await fs.RemoveDirectoryAsync(caller, directory, name, ct).ConfigureAwait(false));
                return;
            }

            case 14: // RENAME
            {
                var (fs, from) = Resolve(args, caller);
                var fromName = ReadName(args);
                var (toFs, to) = Resolve(args, caller);
                var toName = ReadName(args);
                if (!ReferenceEquals(fs, toFs))
                {
                    throw new NfsException(NfsStatus.XDev, "RENAME across exports.");
                }

                var (fromWcc, toWcc) = await fs.RenameAsync(caller, from, fromName, to, toName, ct).ConfigureAwait(false);
                WriteWcc(res, fromWcc);
                WriteWcc(res, toWcc);
                return;
            }

            case 16: // READDIR
            case 17: // READDIRPLUS
            {
                var plus = procedure == 17;
                var (fs, directory) = Resolve(args, caller);
                var cookie = args.ReadUInt64();
                var verifier = args.ReadUInt64();
                var dirCount = (int)Math.Min(args.ReadUInt32(), 1u << 20);
                var maxCount = plus ? (int)Math.Min(args.ReadUInt32(), 1u << 20) : dirCount;
                var listing = await fs.ReadDirectoryAsync(caller, directory, cookie, verifier, plus, ct).ConfigureAwait(false);
                WriteDirectory(res, listing, plus, dirCount, maxCount);
                return;
            }

            case 18: // FSSTAT
            {
                var (fs, handle) = Resolve(args, caller);
                var stat = await fs.FsStatAsync(caller, handle, ct).ConfigureAwait(false);
                WritePostOp(res, await TryAttributesAsync(fs, caller, handle, ct).ConfigureAwait(false));
                res.WriteUInt64(stat.TotalBytes).WriteUInt64(stat.FreeBytes).WriteUInt64(stat.AvailableBytes)
                    .WriteUInt64(stat.TotalFiles).WriteUInt64(stat.FreeFiles).WriteUInt64(stat.AvailableFiles).WriteUInt32(0);
                return;
            }

            case 19: // FSINFO
            {
                var (fs, handle) = Resolve(args, caller);
                WritePostOp(res, await TryAttributesAsync(fs, caller, handle, ct).ConfigureAwait(false));
                const uint homogeneous = 0x08, canSetTime = 0x10;
                res.WriteUInt32((uint)limits.MaxTransfer).WriteUInt32((uint)limits.PreferredTransfer).WriteUInt32(4096)
                    .WriteUInt32((uint)limits.MaxTransfer).WriteUInt32((uint)limits.PreferredTransfer).WriteUInt32(4096)
                    .WriteUInt32((uint)limits.PreferredDirectoryRead).WriteUInt64(limits.MaxFileSize)
                    .WriteUInt32(0).WriteUInt32(1) // time_delta: 1 ns
                    .WriteUInt32(homogeneous | canSetTime);
                return;
            }

            case 20: // PATHCONF
            {
                var (fs, handle) = Resolve(args, caller);
                WritePostOp(res, await TryAttributesAsync(fs, caller, handle, ct).ConfigureAwait(false));
                res.WriteUInt32(1).WriteUInt32(limits.MaxName).WriteBool(true).WriteBool(true).WriteBool(false).WriteBool(true);
                return;
            }

            case 21: // COMMIT
            {
                var (fs, handle) = Resolve(args, caller);
                var offset = args.ReadUInt64();
                var count = args.ReadUInt32();
                WriteWcc(res, await fs.CommitAsync(caller, handle, checked((long)offset), (int)Math.Min(count, int.MaxValue), ct).ConfigureAwait(false));
                res.WriteUInt64(fs.WriteVerifier);
                return;
            }
        }
    }

    /// <summary>The handle's export, which must exist and allow the caller.</summary>
    private (INfsFileSystem FileSystem, ReadOnlyMemory<byte> Handle) Resolve(XdrReader args, NfsCaller caller)
    {
        var handle = args.ReadOpaque(NfsHandles.MaxSize);
        if (!NfsHandles.TryGetExportId(handle.Span, out _))
        {
            throw new NfsException(NfsStatus.BadHandle, "The handle is not one of this server.");
        }

        var export = exports.FindByHandle(handle.Span) ?? throw new NfsException(NfsStatus.Stale, "The handle's export is gone.");
        if (!export.Allows(caller.Remote.Address))
        {
            exports.Refused?.Invoke(caller.Remote.Address, export.Path);
            throw new NfsException(NfsStatus.Access, $"{caller.Remote.Address} may not use export {export.Path}.");
        }

        return (export.FileSystem, handle);
    }

    private string ReadName(XdrReader args)
    {
        var name = args.ReadString(MaxNameBytes);
        if (System.Text.Encoding.UTF8.GetByteCount(name) > limits.MaxName)
        {
            throw new NfsException(NfsStatus.NameTooLong, $"The name has more than {limits.MaxName} bytes.");
        }

        if (name.Length == 0 || name.Contains('/', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new NfsException(name.Length == 0 ? NfsStatus.Inval : NfsStatus.Access, $"The name '{name}' is not allowed.");
        }

        return name;
    }

    private static async ValueTask<NfsAttributes?> TryAttributesAsync(INfsFileSystem fs, NfsCaller caller, ReadOnlyMemory<byte> handle, CancellationToken ct)
    {
        try
        {
            return await fs.GetAttributesAsync(caller, handle, ct).ConfigureAwait(false);
        }
        catch (NfsException)
        {
            return null;
        }
    }

    /// <summary>The failure body of a procedure: its post-op attributes or wcc data, all absent.</summary>
    private static void WriteFailure(uint procedure, XdrWriter res)
    {
        switch (procedure)
        {
            case 1: // GETATTR: nothing
                break;
            case 2 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 21: // wcc_data
                res.WriteBool(false).WriteBool(false);
                break;
            case 14: // RENAME: two wcc_data
                res.WriteBool(false).WriteBool(false).WriteBool(false).WriteBool(false);
                break;
            case 15: // LINK: post_op_attr and wcc_data
                res.WriteBool(false).WriteBool(false).WriteBool(false);
                break;
            default: // LOOKUP, ACCESS, READLINK, READ, READDIR(PLUS), FSSTAT, FSINFO, PATHCONF: post_op_attr
                res.WriteBool(false);
                break;
        }
    }

    private static void WriteCreated(XdrWriter res, NfsCreateResult created)
    {
        res.WriteBool(true).WriteOpaque(created.Handle.Span);
        WritePostOp(res, created.Attributes);
        WriteWcc(res, created.DirectoryWcc);
    }

    /// <summary>
    /// Entries until the reply would exceed the client's limits (READDIR: count bytes; READDIRPLUS: dircount bytes of
    /// names and cookies, maxcount bytes in all). eof only when every entry fitted.
    /// </summary>
    private static void WriteDirectory(XdrWriter res, NfsDirectoryListing listing, bool plus, int dirCount, int maxCount)
    {
        WritePostOp(res, listing.Attributes);
        res.WriteUInt64(listing.Verifier);
        var start = res.Length;
        var names = 0;
        var written = 0;
        foreach (var entry in listing.Entries)
        {
            var entryWriter = new XdrWriter();
            entryWriter.WriteBool(true).WriteUInt64(entry.FileId).WriteString(entry.Name).WriteUInt64(entry.Cookie);
            var nameBytes = entryWriter.Length;
            if (plus)
            {
                WritePostOp(entryWriter, entry.Attributes);
                if (entry.Handle is { } handle)
                {
                    entryWriter.WriteBool(true).WriteOpaque(handle.Span);
                }
                else
                {
                    entryWriter.WriteBool(false);
                }
            }

            // Header (post_op_attr ≤ 88, verifier 8, status 4) and the trailing value_follows and eof (8) count too.
            if ((plus && names + nameBytes > dirCount) || (res.Length - start) + entryWriter.Length + 8 + 100 > maxCount)
            {
                if (written == 0)
                {
                    throw new NfsException(NfsStatus.TooSmall, "Not even one directory entry fits into the reply size the client allows.");
                }

                res.WriteBool(false).WriteBool(false);
                return;
            }

            res.WriteFixedOpaque(entryWriter.Written.Span);
            names += nameBytes;
            written++;
        }

        res.WriteBool(false).WriteBool(true);
    }

    private static NfsSetAttributes ReadSetAttributes(XdrReader args)
    {
        uint? mode = args.ReadBool() ? args.ReadUInt32() : null;
        uint? uid = args.ReadBool() ? args.ReadUInt32() : null;
        uint? gid = args.ReadBool() ? args.ReadUInt32() : null;
        ulong? size = args.ReadBool() ? args.ReadUInt64() : null;
        var atimeHow = (NfsTimeHow)args.ReadUInt32();
        var atime = atimeHow == NfsTimeHow.ClientTime ? ReadTime(args) : default;
        var mtimeHow = (NfsTimeHow)args.ReadUInt32();
        var mtime = mtimeHow == NfsTimeHow.ClientTime ? ReadTime(args) : default;
        if (atimeHow > NfsTimeHow.ClientTime || mtimeHow > NfsTimeHow.ClientTime)
        {
            throw new XdrException("A time_how is unknown.");
        }

        return new NfsSetAttributes(mode, uid, gid, size, atimeHow, atime, mtimeHow, mtime);
    }

    private static NfsTime ReadTime(XdrReader args) => new(args.ReadUInt32(), args.ReadUInt32());

    private static void WriteTime(XdrWriter res, NfsTime time) => res.WriteUInt32(time.Seconds).WriteUInt32(time.Nanoseconds);

    public static void WriteAttributes(XdrWriter res, NfsAttributes a)
    {
        ArgumentNullException.ThrowIfNull(res);
        ArgumentNullException.ThrowIfNull(a);
        res.WriteUInt32((uint)a.Type).WriteUInt32(a.Mode).WriteUInt32(a.Links).WriteUInt32(a.Uid).WriteUInt32(a.Gid)
            .WriteUInt64(a.Size).WriteUInt64(a.Used).WriteUInt32(0).WriteUInt32(0).WriteUInt64(a.FsId).WriteUInt64(a.FileId);
        WriteTime(res, a.Atime);
        WriteTime(res, a.Mtime);
        WriteTime(res, a.Ctime);
    }

    private static void WritePostOp(XdrWriter res, NfsAttributes? attributes)
    {
        res.WriteBool(attributes is not null);
        if (attributes is not null)
        {
            WriteAttributes(res, attributes);
        }
    }

    private static void WriteWcc(XdrWriter res, NfsWcc wcc)
    {
        res.WriteBool(wcc.Before is not null);
        if (wcc.Before is { } before)
        {
            res.WriteUInt64(before.Size);
            WriteTime(res, before.Mtime);
            WriteTime(res, before.Ctime);
        }

        WritePostOp(res, wcc.After);
    }
}

/// <summary>One NFS call as the server answered it (for metrics and diagnostics).</summary>
public sealed record NfsCallRecord(string Procedure, NfsStatus Status, System.Net.IPEndPoint Remote, TimeSpan Duration);
