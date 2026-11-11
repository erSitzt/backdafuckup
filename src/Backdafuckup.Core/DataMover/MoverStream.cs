using System.Buffers;
using System.Buffers.Binary;

namespace Backdafuckup.Core.DataMover;

/// <summary>
/// The stream between a mover pod and the server: the volume's data regions, zero regions left out. Backups send it from
/// the mover to the server, restores from the server to the mover.
/// <code>
/// "BDFUMOV1"  int64 capacity
/// { int64 offset, int32 length, data[length] }   strictly increasing, non-overlapping, inside the capacity
/// int64 -2, int32 0                              a heartbeat while the sender reads zeros (keeps the connection alive)
/// int64 -1, int32 0, int64 total data bytes      the end; without it the stream is truncated
/// </code>
/// All integers little-endian.
/// </summary>
public static class MoverStream
{
    /// <summary>The largest region in one record (also the mover's read size limit).</summary>
    public const int MaxRecordBytes = 16 * 1024 * 1024;

    private const long EndOffset = -1;
    private const long HeartbeatOffset = -2;

    private static ReadOnlySpan<byte> Magic => "BDFUMOV1"u8;

    public static async ValueTask WriteHeaderAsync(Stream stream, long capacity, CancellationToken cancellationToken)
    {
        var header = new byte[16];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), capacity);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask WriteRecordAsync(Stream stream, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.Length is 0 or > MaxRecordBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(data), data.Length, $"A record holds 1 to {MaxRecordBytes} bytes.");
        }

        var head = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(head, offset);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(8), data.Length);
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tells the reader the sender is still there although it has no data to send (a long zero region).</summary>
    public static async ValueTask WriteHeartbeatAsync(Stream stream, CancellationToken cancellationToken)
    {
        var heartbeat = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(heartbeat, HeartbeatOffset);
        await stream.WriteAsync(heartbeat, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask WriteEndAsync(Stream stream, long dataBytes, CancellationToken cancellationToken)
    {
        var end = new byte[20];
        BinaryPrimitives.WriteInt64LittleEndian(end, EndOffset);
        BinaryPrimitives.WriteInt64LittleEndian(end.AsSpan(12), dataBytes);
        await stream.WriteAsync(end, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the header; returns the capacity.</summary>
    public static async ValueTask<long> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 8).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a mover stream (the header is missing).");
        }

        var capacity = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
        return capacity >= 0 ? capacity : throw new InvalidDataException($"The mover stream gives a negative capacity ({capacity}).");
    }

    /// <summary>
    /// Reads the records after the header into <paramref name="sink"/> and checks them: in order, inside the capacity, and
    /// the end with the right total. Returns the data bytes.
    /// </summary>
    public static async Task<long> ReadRecordsAsync(Stream stream, long capacity, DiskDataSink sink, CancellationToken cancellationToken)
    {
        var head = new byte[12];
        var buffer = ArrayPool<byte>.Shared.Rent(MaxRecordBytes);
        try
        {
            long next = 0;
            long total = 0;
            while (true)
            {
                try
                {
                    await stream.ReadExactlyAsync(head, cancellationToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    throw new InvalidDataException($"The mover stream ended without its end after {total} bytes of data (truncated: the mover or the connection stopped).");
                }

                var offset = BinaryPrimitives.ReadInt64LittleEndian(head);
                var length = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8));
                if (offset == HeartbeatOffset && length == 0)
                {
                    continue;
                }

                if (offset == EndOffset)
                {
                    var tail = new byte[8];
                    await stream.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);
                    var announced = BinaryPrimitives.ReadInt64LittleEndian(tail);
                    return announced == total
                        ? total
                        : throw new InvalidDataException($"The mover stream announces {announced} bytes of data at its end, but carried {total}.");
                }

                if (offset < next || length is <= 0 or > MaxRecordBytes || offset + length > capacity)
                {
                    throw new InvalidDataException($"The mover stream has a record at {offset} of {length} bytes after {next}, in a volume of {capacity} bytes.");
                }

                var data = buffer.AsMemory(0, length);
                await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
                await sink(offset, data, cancellationToken).ConfigureAwait(false);
                next = offset + length;
                total += length;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
