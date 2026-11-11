using System.Buffers.Binary;
using System.Text;

namespace Backdafuckup.Core.DataMover;

/// <summary>What a mover reports at the end of a file restore into a live volume.</summary>
/// <param name="Files">Files put in place (under their name or a RESTORED- name).</param>
/// <param name="Bytes">Their bytes.</param>
/// <param name="Failed">Files, folders and links that could not be put in place.</param>
/// <param name="Renamed">Files and links written under a RESTORED- name next to an existing one.</param>
/// <param name="Partial">Items put in place without all of their owner, mode or times.</param>
/// <param name="Issues">The failed, renamed and partial items one by one (at most <see cref="MoverFileReport.MaxIssues"/>).</param>
public sealed record MoverFileReport(long Files, long Bytes, long Failed, long Renamed, long Partial, IReadOnlyList<MoverFileIssue> Issues)
{
    public const int MaxIssues = 2000;
}

/// <param name="Index">The item's index in the stream (<see cref="MoverFileWriter"/>).</param>
/// <param name="Kind">One of <see cref="MoverFileIssueKinds"/>.</param>
/// <param name="Text">Failed and partial: the reason; renamed: the path it was written to, below the volume's root.</param>
public sealed record MoverFileIssue(int Index, string Kind, string Text);

public static class MoverFileIssueKinds
{
    public const string Failed = "failed";
    public const string Renamed = "renamed";
    public const string Partial = "partial";
}

/// <summary>Owner, mode and times of a restored item; null where the backup does not know them.</summary>
public sealed record MoverFileMeta(int? Mode, long? UserId, long? GroupId, DateTime? ModifiedUtc, DateTime? AccessedUtc);

public enum MoverFileRecordKind : byte
{
    Directory = (byte)'D',
    File = (byte)'F',
    Content = (byte)'C',
    FileEnd = (byte)'Z',
    FileAborted = (byte)'X',
    Meta = (byte)'M',
    Link = (byte)'L',
    End = (byte)'E',
}

/// <summary>One record of the stream; <see cref="Data"/> is only valid until the next read.</summary>
public readonly record struct MoverFileRecord(
    MoverFileRecordKind Kind, int Index, string? Path, long Length, string? Text, MoverFileMeta? Meta, bool Folder, ReadOnlyMemory<byte> Data);

/// <summary>
/// Writes the stream of a file restore into a live volume, from the server to its mover. Used from one thread.
/// <code>
/// "BDFUFIL1"
/// 'D' int32 index, path                          a folder (an existing one is fine)
/// 'F' int32 index, path, int64 length            a file; its content follows as 'C' records, then 'Z' or 'X'
/// 'C' int32 n, data[n]                           content of the current file, 1 to MaxChunkBytes bytes
/// 'Z'                                            the current file is complete
/// 'X' text                                       the current file broke off (the reason); the mover discards it
/// 'M' int32 index, path, byte folder, int32 mode, int64 uid, int64 gid, int64 mtime, int64 atime
///                                                owner, mode and times: of a file right after its 'Z' (same index),
///                                                of a folder after its content; -1 (mode, ids) or 0 (times) unknown
/// 'L' int32 index, path, text target, int64 uid, int64 gid    a symbolic link
/// 'E' int32 items                                the end; without it the stream is truncated
/// </code>
/// A path is the item's name components below the volume's root, joined by "/". Text is an int32 byte length and
/// UTF-8; times are UTC ticks. All integers little-endian.
/// </summary>
public sealed class MoverFileWriter(Stream stream)
{
    public const int MaxChunkBytes = 1024 * 1024;
    public const int MaxTextBytes = 64 * 1024;

    internal static ReadOnlySpan<byte> Magic => "BDFUFIL1"u8;

    private readonly byte[] _chunk = new byte[MaxChunkBytes];
    private readonly byte[] _scratch = new byte[64];

    public void WriteHeader() => stream.Write(Magic);

    public void Directory(int index, string path)
    {
        Kind(MoverFileRecordKind.Directory);
        Int32(index);
        Text(path);
    }

    /// <summary>
    /// Sends a file with exactly <paramref name="length"/> bytes of <paramref name="content"/>. When reading the
    /// content fails or it has another length, the mover is told to discard the file and the error is thrown; errors of
    /// the stream to the mover are thrown as they are.
    /// </summary>
    public void File(int index, string path, Stream content, long length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        Kind(MoverFileRecordKind.File);
        Int32(index);
        Text(path);
        Int64(length);
        long sent = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read;
            try
            {
                read = content.Read(_chunk, 0, (int)Math.Min(_chunk.Length, Math.Max(1, length - sent + 1)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Abort(ex.Message);
                throw;
            }

            if (read == 0)
            {
                break;
            }

            if (sent + read > length)
            {
                Abort($"the content is longer than its size of {length} bytes");
                throw new IOException($"The content of {path} is longer than its size of {length} bytes in the restore point.");
            }

            Kind(MoverFileRecordKind.Content);
            Int32(read);
            stream.Write(_chunk, 0, read);
            sent += read;
        }

        if (sent != length)
        {
            Abort($"the content ended after {sent} of {length} bytes");
            throw new IOException($"The content of {path} ended after {sent} of its {length} bytes in the restore point.");
        }

        Kind(MoverFileRecordKind.FileEnd);
    }

    public void Meta(int index, string path, bool folder, MoverFileMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        Kind(MoverFileRecordKind.Meta);
        Int32(index);
        Text(path);
        stream.WriteByte(folder ? (byte)1 : (byte)0);
        Int32(meta.Mode ?? -1);
        Int64(meta.UserId ?? -1);
        Int64(meta.GroupId ?? -1);
        Int64(meta.ModifiedUtc?.ToUniversalTime().Ticks ?? 0);
        Int64(meta.AccessedUtc?.ToUniversalTime().Ticks ?? 0);
    }

    public void Link(int index, string path, string target, long? userId, long? groupId)
    {
        Kind(MoverFileRecordKind.Link);
        Int32(index);
        Text(path);
        Text(target);
        Int64(userId ?? -1);
        Int64(groupId ?? -1);
    }

    public void End(int items)
    {
        Kind(MoverFileRecordKind.End);
        Int32(items);
        stream.Flush();
    }

    private void Abort(string reason)
    {
        Kind(MoverFileRecordKind.FileAborted);
        Text(reason.Length > 1000 ? reason[..1000] : reason);
    }

    private void Kind(MoverFileRecordKind kind) => stream.WriteByte((byte)kind);

    private void Int32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_scratch, value);
        stream.Write(_scratch, 0, 4);
    }

    private void Int64(long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(_scratch, value);
        stream.Write(_scratch, 0, 8);
    }

    private void Text(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxTextBytes)
        {
            throw new IOException($"The name {text[..Math.Min(text.Length, 200)]}… is longer than {MaxTextBytes} bytes.");
        }

        Int32(bytes.Length);
        stream.Write(bytes);
    }
}

/// <summary>Reads the stream of <see cref="MoverFileWriter"/> (the mover's side).</summary>
public sealed class MoverFileReader(Stream stream)
{
    private readonly byte[] _chunk = new byte[MoverFileWriter.MaxChunkBytes];
    private readonly byte[] _scratch = new byte[64];

    public async ValueTask ReadHeaderAsync(CancellationToken cancellationToken)
    {
        var magic = new byte[8];
        await stream.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(MoverFileWriter.Magic))
        {
            throw new InvalidDataException("Not a file stream of the backup server (the header is missing).");
        }
    }

    /// <summary>The next record; the end of the stream before <see cref="MoverFileRecordKind.End"/> is an error.</summary>
    public async ValueTask<MoverFileRecord> ReadAsync(CancellationToken cancellationToken)
    {
        var kind = (MoverFileRecordKind)await ByteAsync(cancellationToken).ConfigureAwait(false);
        switch (kind)
        {
            case MoverFileRecordKind.Directory:
                return new MoverFileRecord(kind, await Int32Async(cancellationToken).ConfigureAwait(false), await TextAsync(cancellationToken).ConfigureAwait(false), 0, null, null, true, default);
            case MoverFileRecordKind.File:
            {
                var index = await Int32Async(cancellationToken).ConfigureAwait(false);
                var path = await TextAsync(cancellationToken).ConfigureAwait(false);
                var length = await Int64Async(cancellationToken).ConfigureAwait(false);
                return length >= 0 ? new MoverFileRecord(kind, index, path, length, null, null, false, default) : throw new InvalidDataException($"The file {path} has a negative size.");
            }

            case MoverFileRecordKind.Content:
            {
                var length = await Int32Async(cancellationToken).ConfigureAwait(false);
                if (length is <= 0 or > MoverFileWriter.MaxChunkBytes)
                {
                    throw new InvalidDataException($"A content record of {length} bytes (1 to {MoverFileWriter.MaxChunkBytes} allowed).");
                }

                await stream.ReadExactlyAsync(_chunk.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                return new MoverFileRecord(kind, 0, null, length, null, null, false, _chunk.AsMemory(0, length));
            }

            case MoverFileRecordKind.FileEnd:
                return new MoverFileRecord(kind, 0, null, 0, null, null, false, default);
            case MoverFileRecordKind.FileAborted:
                return new MoverFileRecord(kind, 0, null, 0, await TextAsync(cancellationToken).ConfigureAwait(false), null, false, default);
            case MoverFileRecordKind.Meta:
            {
                var index = await Int32Async(cancellationToken).ConfigureAwait(false);
                var path = await TextAsync(cancellationToken).ConfigureAwait(false);
                var folder = await ByteAsync(cancellationToken).ConfigureAwait(false) != 0;
                var mode = await Int32Async(cancellationToken).ConfigureAwait(false);
                var uid = await Int64Async(cancellationToken).ConfigureAwait(false);
                var gid = await Int64Async(cancellationToken).ConfigureAwait(false);
                var mtime = await Int64Async(cancellationToken).ConfigureAwait(false);
                var atime = await Int64Async(cancellationToken).ConfigureAwait(false);
                var meta = new MoverFileMeta(mode < 0 ? null : mode, uid < 0 ? null : uid, gid < 0 ? null : gid, Time(mtime), Time(atime));
                return new MoverFileRecord(kind, index, path, 0, null, meta, folder, default);
            }

            case MoverFileRecordKind.Link:
            {
                var index = await Int32Async(cancellationToken).ConfigureAwait(false);
                var path = await TextAsync(cancellationToken).ConfigureAwait(false);
                var target = await TextAsync(cancellationToken).ConfigureAwait(false);
                var uid = await Int64Async(cancellationToken).ConfigureAwait(false);
                var gid = await Int64Async(cancellationToken).ConfigureAwait(false);
                return new MoverFileRecord(kind, index, path, 0, target, new MoverFileMeta(null, uid < 0 ? null : uid, gid < 0 ? null : gid, null, null), false, default);
            }

            case MoverFileRecordKind.End:
                return new MoverFileRecord(kind, await Int32Async(cancellationToken).ConfigureAwait(false), null, 0, null, null, false, default);
            default:
                throw new InvalidDataException($"Unknown record '{(char)kind}' in the file stream.");
        }
    }

    private static DateTime? Time(long ticks) =>
        ticks > 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;

    private async ValueTask<byte> ByteAsync(CancellationToken cancellationToken)
    {
        await stream.ReadExactlyAsync(_scratch.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        return _scratch[0];
    }

    private async ValueTask<int> Int32Async(CancellationToken cancellationToken)
    {
        await stream.ReadExactlyAsync(_scratch.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(_scratch);
    }

    private async ValueTask<long> Int64Async(CancellationToken cancellationToken)
    {
        await stream.ReadExactlyAsync(_scratch.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt64LittleEndian(_scratch);
    }

    private async ValueTask<string> TextAsync(CancellationToken cancellationToken)
    {
        var length = await Int32Async(cancellationToken).ConfigureAwait(false);
        if (length is < 0 or > MoverFileWriter.MaxTextBytes)
        {
            throw new InvalidDataException($"A name of {length} bytes in the file stream (at most {MoverFileWriter.MaxTextBytes}).");
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }
}
