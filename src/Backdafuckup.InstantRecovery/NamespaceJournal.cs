using System.Buffers.Binary;
using System.Text;

namespace Backdafuckup.InstantRecovery;

/// <summary>A change of the namespace, as written to and read from the journal.</summary>
internal abstract record JournalRecord
{
    public sealed record Create(
        ulong FileId, ulong Parent, string Name, Backdafuckup.Nfs.NfsFileType Type, uint Mode, uint Uid, uint Gid, long TimeTicks, string? BaseKey, long Size, ulong? Verifier)
        : JournalRecord;

    public sealed record Remove(ulong Parent, string Name) : JournalRecord;

    public sealed record Rename(ulong FromParent, string FromName, ulong ToParent, string ToName) : JournalRecord;

    /// <summary>Attributes set by SETATTR (null: unchanged).</summary>
    public sealed record SetAttributes(ulong FileId, uint? Mode, uint? Uid, uint? Gid, long? Size, long TimeTicks) : JournalRecord;

    /// <summary>A checkpoint of a file's modification time (written data does not journal each write).</summary>
    public sealed record Times(ulong FileId, long MtimeTicks) : JournalRecord;
}

/// <summary>
/// The namespace of an instant recovery as an append-only log: each record is <c>length | CRC-32 | payload</c> and is on
/// disk (flushed through the OS) before the operation is answered. Reading stops at the first record that is short or
/// whose checksum fails (a write cut by a crash), and the file is truncated there.
/// </summary>
internal sealed class NamespaceJournal : IDisposable
{
    private readonly FileStream _file;

    private NamespaceJournal(FileStream file) => _file = file;

    public static (NamespaceJournal Journal, List<JournalRecord> Records) Open(string path)
    {
        var records = new List<JournalRecord>();
        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.None);
        var header = new byte[8];
        long good = 0;
        while (true)
        {
            file.Position = good;
            if (file.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8)
            {
                break;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (length <= 0 || length > 1 << 20)
            {
                break;
            }

            var payload = new byte[length];
            if (file.ReadAtLeast(payload, length, throwOnEndOfStream: false) < length || Crc32.Compute(payload) != crc)
            {
                break;
            }

            records.Add(Decode(payload));
            good += 8 + length;
        }

        file.SetLength(good);
        file.Position = good;
        return (new NamespaceJournal(file), records);
    }

    /// <summary>Appends a record and flushes it to stable storage before returning.</summary>
    public void Append(JournalRecord record)
    {
        var payload = Encode(record);
        var buffer = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), Crc32.Compute(payload));
        payload.CopyTo(buffer, 8);
        _file.Write(buffer);
        _file.Flush(flushToDisk: true);
    }

    public long Length => _file.Length;

    public void Dispose() => _file.Dispose();

    private static byte[] Encode(JournalRecord record)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            switch (record)
            {
                case JournalRecord.Create c:
                    w.Write((byte)1);
                    w.Write(c.FileId);
                    w.Write(c.Parent);
                    w.Write(c.Name);
                    w.Write((uint)c.Type);
                    w.Write(c.Mode);
                    w.Write(c.Uid);
                    w.Write(c.Gid);
                    w.Write(c.TimeTicks);
                    w.Write(c.BaseKey ?? string.Empty);
                    w.Write(c.Size);
                    w.Write(c.Verifier.HasValue);
                    w.Write(c.Verifier ?? 0);
                    break;
                case JournalRecord.Remove r:
                    w.Write((byte)2);
                    w.Write(r.Parent);
                    w.Write(r.Name);
                    break;
                case JournalRecord.Rename r:
                    w.Write((byte)3);
                    w.Write(r.FromParent);
                    w.Write(r.FromName);
                    w.Write(r.ToParent);
                    w.Write(r.ToName);
                    break;
                case JournalRecord.SetAttributes s:
                    w.Write((byte)4);
                    w.Write(s.FileId);
                    WriteOptional(w, s.Mode);
                    WriteOptional(w, s.Uid);
                    WriteOptional(w, s.Gid);
                    w.Write(s.Size.HasValue);
                    w.Write(s.Size ?? 0);
                    w.Write(s.TimeTicks);
                    break;
                case JournalRecord.Times t:
                    w.Write((byte)5);
                    w.Write(t.FileId);
                    w.Write(t.MtimeTicks);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(record));
            }
        }

        return stream.ToArray();
    }

    private static void WriteOptional(BinaryWriter w, uint? value)
    {
        w.Write(value.HasValue);
        w.Write(value ?? 0);
    }

    private static uint? ReadOptional(BinaryReader r)
    {
        var has = r.ReadBoolean();
        var value = r.ReadUInt32();
        return has ? value : null;
    }

    private static JournalRecord Decode(byte[] payload)
    {
        using var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
        return r.ReadByte() switch
        {
            1 => ReadCreate(r),
            2 => new JournalRecord.Remove(r.ReadUInt64(), r.ReadString()),
            3 => new JournalRecord.Rename(r.ReadUInt64(), r.ReadString(), r.ReadUInt64(), r.ReadString()),
            4 => ReadSetAttributes(r),
            5 => new JournalRecord.Times(r.ReadUInt64(), r.ReadInt64()),
            var kind => throw new InvalidDataException($"Unknown journal record {kind}."),
        };
    }

    private static JournalRecord.Create ReadCreate(BinaryReader r)
    {
        var fileId = r.ReadUInt64();
        var parent = r.ReadUInt64();
        var name = r.ReadString();
        var type = (Backdafuckup.Nfs.NfsFileType)r.ReadUInt32();
        var mode = r.ReadUInt32();
        var uid = r.ReadUInt32();
        var gid = r.ReadUInt32();
        var time = r.ReadInt64();
        var baseKey = r.ReadString();
        var size = r.ReadInt64();
        var hasVerifier = r.ReadBoolean();
        var verifier = r.ReadUInt64();
        return new JournalRecord.Create(fileId, parent, name, type, mode, uid, gid, time, baseKey.Length == 0 ? null : baseKey, size, hasVerifier ? verifier : null);
    }

    private static JournalRecord.SetAttributes ReadSetAttributes(BinaryReader r)
    {
        var fileId = r.ReadUInt64();
        var mode = ReadOptional(r);
        var uid = ReadOptional(r);
        var gid = ReadOptional(r);
        var hasSize = r.ReadBoolean();
        var size = r.ReadInt64();
        return new JournalRecord.SetAttributes(fileId, mode, uid, gid, hasSize ? size : null, r.ReadInt64());
    }
}

/// <summary>CRC-32 (IEEE 802.3, reflected) for the journal's records.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        var c = (uint)i;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        return c;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }
}
