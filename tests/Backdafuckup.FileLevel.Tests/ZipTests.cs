using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Backdafuckup.FileLevel;
using Backdafuckup.FileLevel.Zip;

namespace Backdafuckup.FileLevel.Tests;

/// <summary>
/// ZIP downloads: written front to back to a non-seekable stream and read back with .NET's reader and, where
/// installed, Info-ZIP unzip and Python's zipfile. Set BDFU_ZIP_SAMPLES to a folder to keep the archives for manual
/// checks with 7-Zip, Windows Explorer or macOS.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ZipTests
{
    private static readonly DateTime Modified = new(2026, 10, 30, 14, 15, 16, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("..\\..\\evil.txt", ".._.._evil.txt")]
    [InlineData("C:evil", "C_evil")]
    [InlineData("c:", "c_")]
    [InlineData(".. ", "___")]
    [InlineData("...", "___")]
    [InlineData("a\u0001b\nc", "a_b_c")]
    [InlineData("normal name: with * and ?.txt", "normal name: with * and ?.txt")]
    [InlineData("Grüße 日本.txt", "Grüße 日本.txt")]
    [InlineData("x:y", "x_y")]
    [InlineData("ab:c", "ab:c")]
    public void Names_that_could_leave_the_target_folder_are_made_safe(string name, string expected) =>
        Assert.Equal(expected, ZipNames.Sanitize(name));

    [Fact]
    public void Colliding_names_get_a_number_and_unchanged_names_keep_theirs()
    {
        Assert.Equal(["a_b (2)", "a_b", "x.txt"], ZipNames.Unique(["a\\b", "a_b", "x.txt"]));
        Assert.Equal(["x.txt", "x (2).txt", "x (3).txt"], ZipNames.Unique(["x.txt", "x.txt", "x.txt"]));
        Assert.Equal(["___", "___ (2)", "____"], ZipNames.Unique(["...", "...", "____"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Small_archive_reads_back_with_names_times_attributes_and_content(bool forceZip64)
    {
        var random = new byte[3 << 20];
        new Random(42).NextBytes(random);
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compressible line\n", 10_000)));
        var zip = Write(
            w =>
            {
                w.AddDirectory(new ZipEntryMeta("dir", Modified, ZipHost.Unix, (0x41EDu << 16) | 0x10));
                Add(w, new ZipEntryMeta("dir/text.txt", Modified, ZipHost.Unix, 0x81A4u << 16), text);
                Add(w, new ZipEntryMeta("dir/empty", Modified, ZipHost.Unix, 0x8180u << 16), []);
                Add(w, new ZipEntryMeta("random.bin", null, ZipHost.Dos, (uint)(FileAttributes.Hidden | FileAttributes.Archive)), random);
                Add(w, new ZipEntryMeta("Grüße/日本.txt", Modified, ZipHost.Dos, 0x20), "umlaut"u8.ToArray());
            },
            new ZipWriterOptions { ForceZip64 = forceZip64, DosTimeZone = TimeZoneInfo.Utc });

        using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        {
            Assert.Equal(["dir/", "dir/text.txt", "dir/empty", "random.bin", "Grüße/日本.txt"], archive.Entries.Select(e => e.FullName));
            Assert.Equal(text, ReadAll(archive.GetEntry("dir/text.txt")!));
            Assert.Empty(ReadAll(archive.GetEntry("dir/empty")!));
            Assert.Equal(random, ReadAll(archive.GetEntry("random.bin")!));
            Assert.Equal("umlaut"u8.ToArray(), ReadAll(archive.GetEntry("Grüße/日本.txt")!));
            Assert.True(archive.GetEntry("dir/text.txt")!.CompressedLength < text.Length / 10, "text is deflated");
            Assert.Equal(new DateTime(2026, 10, 30, 14, 15, 16), archive.GetEntry("dir/text.txt")!.LastWriteTime.DateTime);
            Assert.Equal(0x81A4, archive.GetEntry("dir/text.txt")!.ExternalAttributes >>> 16);
            Assert.Equal(0x8180, archive.GetEntry("dir/empty")!.ExternalAttributes >>> 16);
            Assert.Equal((int)(FileAttributes.Hidden | FileAttributes.Archive), archive.GetEntry("random.bin")!.ExternalAttributes);
        }

        CheckWithTools(zip, forceZip64 ? "small-zip64.zip" : "small.zip");
    }

    [Fact]
    public void More_than_65535_entries_use_the_zip64_end_record()
    {
        const int count = 70_000;
        var zip = Write(
            w =>
            {
                for (var i = 0; i < count; i++)
                {
                    Add(w, new ZipEntryMeta($"d{i / 1000:000}/f{i:00000}.txt", Modified, ZipHost.Dos, 0x20), Encoding.ASCII.GetBytes(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            });

        Assert.Equal(0x06064b50u, BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 - 20 - 56)));
        using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        {
            Assert.Equal(count, archive.Entries.Count);
            Assert.Equal("69999"u8.ToArray(), ReadAll(archive.Entries[^1]));
        }

        CheckWithTools(zip, "many-entries.zip");
    }

    [Fact]
    public void A_file_larger_than_4_GiB_gets_zip64_in_its_local_header_and_reads_back()
    {
        const long size = (4L << 30) + (300L << 20) + 12345;
        var path = Path.Combine(Path.GetTempPath(), $"bdfu-zip-large-{Guid.NewGuid():N}.zip");
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            using (var writer = new ZipStreamWriter(new NonSeekableStream(file)))
            {
                Add(writer, new ZipEntryMeta("small-before.txt", Modified, ZipHost.Dos, 0x20), "before"u8.ToArray());
                Assert.Equal(size, writer.AddFile(new ZipEntryMeta("large.bin", Modified, ZipHost.Dos, 0x20), size, new ZeroStream(size), Ct));
                Add(writer, new ZipEntryMeta("small-after.txt", Modified, ZipHost.Dos, 0x20), "after"u8.ToArray());
                writer.Finish();
            }

            var bytes = File.ReadAllBytes(path);
            var local = bytes.AsSpan(LocalHeaderOffset(bytes, "large.bin"));
            Assert.Equal(45, BinaryPrimitives.ReadUInt16LittleEndian(local[4..]));
            Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(local[22..]));
            Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16LittleEndian(local[(30 + "large.bin".Length)..]));

            using (var archive = ZipFile.OpenRead(path))
            {
                var large = archive.GetEntry("large.bin")!;
                Assert.Equal(size, large.Length);
                using var content = large.Open();
                var buffer = new byte[1 << 20];
                long total = 0;
                int n;
                while ((n = content.Read(buffer)) > 0)
                {
                    Assert.True(buffer.AsSpan(0, n).IndexOfAnyExcept((byte)0) < 0);
                    total += n;
                }

                Assert.Equal(size, total);
                Assert.Equal("after"u8.ToArray(), ReadAll(archive.GetEntry("small-after.txt")!));
            }

            CheckWithTools(path, "large.zip");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Linux_folders_export_with_content_and_modes_and_links_are_listed_as_skipped()
    {
        using var image = GuestImage.Open([Fixtures.Disk("ext4-gpt")]);
        var fs = image.OpenFileSystem("0:2048");
        var selection = new[] { "folder", "hello.txt", "link-to-hello", "pattern-3m.bin", "sparse.bin", "secret.txt", "Ümlaut äöü ß.txt" }.Select(GuestPath.Parse).ToList();
        using var output = new MemoryStream();
        var result = GuestZipExport.Write(fs, selection, new NonSeekableStream(output), new GuestZipOptions(), Ct);

        Assert.Equal(6, result.Files);
        Assert.Equal(3, result.Directories);
        Assert.Equal(1, result.SkippedCount);
        var manifest = Fixtures.Manifest("ext4-gpt.p1").ToDictionary(e => e.Path);
        using var archive = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
        Assert.Equal(
            ["folder/", "folder/sub/", "folder/sub/deep/", "folder/sub/deep/nested.txt", "hello.txt", "pattern-3m.bin", "sparse.bin", "secret.txt", "Ümlaut äöü ß.txt", GuestZipExport.SkippedListName],
            archive.Entries.Select(e => e.FullName));
        foreach (var name in new[] { "folder/sub/deep/nested.txt", "hello.txt", "pattern-3m.bin", "sparse.bin", "Ümlaut äöü ß.txt" })
        {
            Assert.Equal(manifest[name].Hash, Convert.ToHexStringLower(SHA256.HashData(ReadAll(archive.GetEntry(name)!))));
        }

        Assert.Equal(Convert.ToInt32("100640", 8), archive.GetEntry("secret.txt")!.ExternalAttributes >>> 16);
        Assert.Equal(Convert.ToInt32("40755", 8), archive.GetEntry("folder/")!.ExternalAttributes >>> 16);
        var skipped = Encoding.UTF8.GetString(ReadAll(archive.GetEntry(GuestZipExport.SkippedListName)!));
        Assert.Contains("/link-to-hello\tsymbolic link to hello.txt", skipped, StringComparison.Ordinal);
        CheckWithTools(output.ToArray(), "ext4-selection.zip");
    }

    [Fact]
    public void Windows_folders_export_with_attributes_and_compressed_files()
    {
        using var image = GuestImage.Open([Fixtures.Disk("ntfs-gpt")]);
        var fs = image.OpenFileSystem("0:2048");
        using var output = new MemoryStream();
        var result = GuestZipExport.Write(fs, [GuestPath.Parse("compressed"), GuestPath.Parse("hidden.txt"), GuestPath.Parse("medium")], new NonSeekableStream(output), new GuestZipOptions(), Ct);

        Assert.Equal(0, result.SkippedCount);
        Assert.Null(result.SkippedListName);
        Assert.Equal(52, result.Files);
        var manifest = Fixtures.Manifest("ntfs-gpt.p1").ToDictionary(e => e.Path);
        using var archive = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
        Assert.Equal(manifest["compressed/packed.bin"].Hash, Convert.ToHexStringLower(SHA256.HashData(ReadAll(archive.GetEntry("compressed/packed.bin")!))));
        Assert.Equal((int)FileAttributes.Hidden, archive.GetEntry("hidden.txt")!.ExternalAttributes & (int)FileAttributes.Hidden);
        Assert.Equal(0x10, archive.GetEntry("medium/")!.ExternalAttributes & 0x10);
        Assert.Equal(51, archive.Entries.Count(e => e.FullName.StartsWith("medium/", StringComparison.Ordinal)));
    }

    [Fact]
    public void Too_many_entries_stop_the_archive()
    {
        using var image = GuestImage.Open([Fixtures.Disk("ntfs-gpt")]);
        var fs = image.OpenFileSystem("0:2048");
        var ex = Assert.Throws<GuestWalkLimitException>(() =>
            GuestZipExport.Write(fs, [GuestPath.Parse("medium")], new NonSeekableStream(new MemoryStream()), new GuestZipOptions { MaxEntries = 10 }, Ct));
        Assert.Contains("more than 10", ex.Message, StringComparison.Ordinal);
    }

    private static byte[] Write(Action<ZipStreamWriter> add, ZipWriterOptions? options = null)
    {
        using var output = new MemoryStream();
        using (var writer = new ZipStreamWriter(new NonSeekableStream(output), options))
        {
            add(writer);
            writer.Finish();
        }

        return output.ToArray();
    }

    private static void Add(ZipStreamWriter writer, ZipEntryMeta meta, byte[] content) =>
        Assert.Equal(content.Length, writer.AddFile(meta, content.Length, new MemoryStream(content), Ct));

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Offset of an entry's local header, from its central directory record.</summary>
    private static int LocalHeaderOffset(byte[] zip, string name)
    {
        var needle = Encoding.UTF8.GetBytes(name);
        for (var i = zip.Length - 46; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) == 0x02014b50 && zip.AsSpan(i + 46).StartsWith(needle))
            {
                return (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i + 42));
            }
        }

        throw new InvalidDataException($"{name} has no central directory record.");
    }

    private static void CheckWithTools(byte[] zip, string sampleName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bdfu-zip-{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(path, zip);
        try
        {
            CheckWithTools(path, sampleName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Info-ZIP and Python check every CRC; skipped where the tool is not installed.</summary>
    private static void CheckWithTools(string path, string sampleName)
    {
        Run("unzip", "-tqq", path);
        Run("python3", "-I", "-c", "import sys, zipfile\nwith zipfile.ZipFile(sys.argv[1]) as z:\n    bad = z.testzip()\n    sys.exit('bad entry: ' + bad if bad else 0)", path);
        if (Environment.GetEnvironmentVariable("BDFU_ZIP_SAMPLES") is { Length: > 0 } samples)
        {
            Directory.CreateDirectory(samples);
            File.Copy(path, Path.Combine(samples, sampleName), overwrite: true);
        }
    }

    private static void Run(string tool, params string[] args)
    {
        var exe = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, tool))
            .FirstOrDefault(File.Exists);
        if (exe is null)
        {
            return;
        }

        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{tool} failed with exit code {process.ExitCode}: {stdout.Result} {stderr}");
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>A long run of zeros without allocating it.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length, length - _position);
            buffer[..n].Clear();
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
