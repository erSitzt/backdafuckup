using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Backdafuckup.Nbd;

/// <summary>What an NBD server serves: one block device or image of a fixed size.</summary>
public interface INbdExport
{
    long Size { get; }

    bool ReadOnly { get; }

    ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken);

    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    ValueTask FlushAsync(CancellationToken cancellationToken);

    /// <summary>Writes zeros (a device may deallocate the range).</summary>
    ValueTask WriteZeroesAsync(long offset, long length, CancellationToken cancellationToken);
}

/// <summary>Settings of one served connection.</summary>
public sealed record NbdServerOptions
{
    /// <summary>The export name clients must ask for; null accepts any name (one export per connection).</summary>
    public string? ExportName { get; init; }

    /// <summary>Largest request the server takes (announced as the maximum block size).</summary>
    public int MaxRequest { get; init; } = 32 * 1024 * 1024;

    /// <summary>Requests processed at the same time on this connection; replies may come out of order.</summary>
    public int MaxInFlight { get; init; } = 8;
}

/// <summary>
/// An NBD server for one connection over any stream (a TCP connection, or a WebSocket tunnel of the proxy agent),
/// implemented from the public-domain protocol specification: fixed newstyle negotiation with GO, INFO and EXPORT_NAME,
/// simple replies, and READ, WRITE, FLUSH, TRIM, WRITE_ZEROES and DISC. Requests run concurrently and reply out of
/// order; FLUSH and DISC wait for the requests before them.
/// </summary>
public static class NbdServer
{
    private const int HandshakeLimit = 64 * 1024;

    /// <summary>Serves <paramref name="export"/> on <paramref name="stream"/> until the client disconnects; the stream is not disposed.</summary>
    public static async Task ServeAsync(Stream stream, INbdExport export, NbdServerOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(options);
        if (!await NegotiateAsync(stream, export, options, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await TransmitAsync(stream, export, options, cancellationToken).ConfigureAwait(false);
    }

    private static ushort TransmissionFlags(INbdExport export) => (ushort)(NbdProtocol.TransHasFlags | NbdProtocol.TransSendFlush | NbdProtocol.TransCanMultiConn
        | (export.ReadOnly ? NbdProtocol.TransReadOnly : NbdProtocol.TransSendTrim | NbdProtocol.TransSendWriteZeroes | NbdProtocol.TransSendFua));

    /// <summary>Returns true when the client entered transmission, false when it aborted.</summary>
    private static async Task<bool> NegotiateAsync(Stream stream, INbdExport export, NbdServerOptions options, CancellationToken cancellationToken)
    {
        var hello = new byte[18];
        BinaryPrimitives.WriteUInt64BigEndian(hello, NbdProtocol.NbdMagic);
        BinaryPrimitives.WriteUInt64BigEndian(hello.AsSpan(8), NbdProtocol.IHaveOpt);
        BinaryPrimitives.WriteUInt16BigEndian(hello.AsSpan(16), NbdProtocol.FlagFixedNewstyle | NbdProtocol.FlagNoZeroes);
        await stream.WriteAsync(hello, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var clientFlags = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false));
        var noZeroes = (clientFlags & NbdProtocol.ClientFlagNoZeroes) != 0;
        while (true)
        {
            var header = await ReadExactAsync(stream, 16, cancellationToken).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt64BigEndian(header) != NbdProtocol.IHaveOpt)
            {
                throw new NbdException("NBD client sent an option without IHAVEOPT.");
            }

            var option = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
            if (length > HandshakeLimit)
            {
                throw new NbdException($"NBD client sent option {option} with {length} bytes of data.");
            }

            var data = await ReadExactAsync(stream, (int)length, cancellationToken).ConfigureAwait(false);
            switch (option)
            {
                case NbdProtocol.OptExportName:
                    if (!NameMatches(options, Encoding.UTF8.GetString(data)))
                    {
                        // EXPORT_NAME has no error reply: the server closes.
                        return false;
                    }

                    var answer = new byte[noZeroes ? 10 : 134];
                    BinaryPrimitives.WriteInt64BigEndian(answer, export.Size);
                    BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(8), TransmissionFlags(export));
                    await stream.WriteAsync(answer, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                case NbdProtocol.OptGo or NbdProtocol.OptInfo:
                    if (data.Length < 6 || BinaryPrimitives.ReadUInt32BigEndian(data) is var nameLength && 4 + nameLength + 2 > data.Length)
                    {
                        await ReplyAsync(stream, option, NbdProtocol.RepErrInvalid, [], cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (!NameMatches(options, Encoding.UTF8.GetString(data, 4, (int)nameLength)))
                    {
                        await ReplyAsync(stream, option, NbdProtocol.RepErrUnknown, Encoding.UTF8.GetBytes("no such export"), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var info = new byte[12];
                    BinaryPrimitives.WriteUInt16BigEndian(info, NbdProtocol.InfoExport);
                    BinaryPrimitives.WriteInt64BigEndian(info.AsSpan(2), export.Size);
                    BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(10), TransmissionFlags(export));
                    await ReplyAsync(stream, option, NbdProtocol.RepInfo, info, cancellationToken).ConfigureAwait(false);
                    var sizes = new byte[14];
                    BinaryPrimitives.WriteUInt16BigEndian(sizes, NbdProtocol.InfoBlockSize);
                    BinaryPrimitives.WriteUInt32BigEndian(sizes.AsSpan(2), 1);
                    BinaryPrimitives.WriteUInt32BigEndian(sizes.AsSpan(6), 4096);
                    BinaryPrimitives.WriteUInt32BigEndian(sizes.AsSpan(10), (uint)options.MaxRequest);
                    await ReplyAsync(stream, option, NbdProtocol.RepInfo, sizes, cancellationToken).ConfigureAwait(false);
                    await ReplyAsync(stream, option, NbdProtocol.RepAck, [], cancellationToken).ConfigureAwait(false);
                    if (option == NbdProtocol.OptGo)
                    {
                        return true;
                    }

                    break;
                case NbdProtocol.OptAbort:
                    await ReplyAsync(stream, option, NbdProtocol.RepAck, [], cancellationToken).ConfigureAwait(false);
                    return false;
                default:
                    await ReplyAsync(stream, option, NbdProtocol.RepErrUnsup, [], cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static async Task TransmitAsync(Stream stream, INbdExport export, NbdServerOptions options, CancellationToken cancellationToken)
    {
        using var sendLock = new SemaphoreSlim(1, 1);
        using var slots = new SemaphoreSlim(options.MaxInFlight, options.MaxInFlight);
        var running = new List<Task>();
        try
        {
            var header = new byte[NbdProtocol.RequestHeaderSize];
            while (true)
            {
                if (!await TryReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                if (BinaryPrimitives.ReadUInt32BigEndian(header) != NbdProtocol.RequestMagic)
                {
                    throw new NbdException("NBD client sent a request with a wrong magic.");
                }

                var flags = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                var type = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6));
                var cookie = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                var offset = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(16));
                var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24));
                running.RemoveAll(t => t.IsCompleted);

                switch (type)
                {
                    case NbdProtocol.CmdDisc:
                        await Task.WhenAll(running).ConfigureAwait(false);
                        return;
                    case NbdProtocol.CmdFlush:
                        await Task.WhenAll(running).ConfigureAwait(false);
                        running.Clear();
                        await ReplyAsync(stream, sendLock, cookie, await RunAsync(() => export.FlushAsync(cancellationToken)).ConfigureAwait(false), default, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                }

                // A WRITE's data follows its header and must be read before the next request, even when it is refused.
                byte[]? payload = null;
                if (type == NbdProtocol.CmdWrite)
                {
                    if (length > options.MaxRequest)
                    {
                        throw new NbdException($"NBD client sent a write of {length} bytes (at most {options.MaxRequest}).");
                    }

                    payload = ArrayPool<byte>.Shared.Rent((int)length);
                    await ReadExactAsync(stream, payload.AsMemory(0, (int)length), cancellationToken).ConfigureAwait(false);
                }

                var error = Check(export, options, type, offset, length);
                if (error != 0)
                {
                    if (payload is not null)
                    {
                        ArrayPool<byte>.Shared.Return(payload);
                    }

                    await ReplyAsync(stream, sendLock, cookie, error, default, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                running.Add(HandleAsync(stream, export, sendLock, slots, type, flags, cookie, offset, (int)length, payload, cancellationToken));
            }
        }
        finally
        {
            try
            {
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The connection ended; its replies cannot be sent any more.
            }
        }
    }

    private static uint Check(INbdExport export, NbdServerOptions options, ushort type, long offset, uint length)
    {
        if (type is not (NbdProtocol.CmdRead or NbdProtocol.CmdWrite or NbdProtocol.CmdTrim or NbdProtocol.CmdWriteZeroes))
        {
            return NbdProtocol.ErrInvalid;
        }

        if (type != NbdProtocol.CmdRead && export.ReadOnly)
        {
            return NbdProtocol.ErrPerm;
        }

        if (type is NbdProtocol.CmdRead && length > options.MaxRequest)
        {
            return NbdProtocol.ErrOverflow;
        }

        return offset < 0 || offset + length > export.Size ? NbdProtocol.ErrNoSpace : 0;
    }

    private static async Task HandleAsync(
        Stream stream, INbdExport export, SemaphoreSlim sendLock, SemaphoreSlim slots, ushort type, ushort flags, ulong cookie, long offset, int length, byte[]? payload,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        byte[]? buffer = null;
        try
        {
            uint error;
            switch (type)
            {
                case NbdProtocol.CmdRead:
                    buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
                    error = await RunAsync(() => export.ReadAsync(offset, buffer.AsMemory(0, length), cancellationToken)).ConfigureAwait(false);
                    await ReplyAsync(stream, sendLock, cookie, error, error == 0 ? buffer.AsMemory(0, length) : default, cancellationToken).ConfigureAwait(false);
                    return;
                case NbdProtocol.CmdWrite:
                    error = await RunAsync(() => export.WriteAsync(offset, payload.AsMemory(0, length), cancellationToken)).ConfigureAwait(false);
                    break;
                default:
                    // TRIM and WRITE_ZEROES both leave zeros (a trimmed range of a device may read as anything; zeros are safe).
                    error = await RunAsync(() => export.WriteZeroesAsync(offset, length, cancellationToken)).ConfigureAwait(false);
                    break;
            }

            if (error == 0 && (flags & NbdProtocol.CmdFlagFua) != 0)
            {
                error = await RunAsync(() => export.FlushAsync(cancellationToken)).ConfigureAwait(false);
            }

            await ReplyAsync(stream, sendLock, cookie, error, default, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (payload is not null)
            {
                ArrayPool<byte>.Shared.Return(payload);
            }

            slots.Release();
        }
    }

    /// <summary>Runs an export call; an I/O error becomes EIO for the client instead of ending the connection.</summary>
    private static async Task<uint> RunAsync(Func<ValueTask> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return 0;
        }
        catch (IOException)
        {
            return NbdProtocol.ErrIo;
        }
        catch (UnauthorizedAccessException)
        {
            return NbdProtocol.ErrPerm;
        }
    }

    private static async Task ReplyAsync(Stream stream, SemaphoreSlim sendLock, ulong cookie, uint error, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var header = new byte[NbdProtocol.SimpleReplyHeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, NbdProtocol.SimpleReplyMagic);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), error);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), cookie);
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            if (!data.IsEmpty)
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task ReplyAsync(Stream stream, uint option, uint type, byte[] data, CancellationToken cancellationToken)
    {
        var reply = new byte[20 + data.Length];
        BinaryPrimitives.WriteUInt64BigEndian(reply, NbdProtocol.OptionReplyMagic);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(8), option);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(12), type);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(16), (uint)data.Length);
        data.CopyTo(reply, 20);
        await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool NameMatches(NbdServerOptions options, string name) => options.ExportName is null || options.ExportName == name;

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        await ReadExactAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!await TryReadExactAsync(stream, buffer, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("The NBD client closed the connection.");
        }
    }

    /// <summary>False when the stream ended before the first byte (a clean close between requests).</summary>
    private static async Task<bool> TryReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return read == 0 ? false : throw new EndOfStreamException("The NBD client closed the connection in the middle of a message.");
            }

            read += n;
        }

        return true;
    }
}
