using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Mover;

namespace Backdafuckup.Server.Tests;

/// <summary>The mover reading a volume file by file (<see cref="VolumeTree"/>), on a folder as the volume.</summary>
[SupportedOSPlatform("linux")]
public sealed class MoverTreeTests : IDisposable
{
    private readonly string _volume = Path.Combine(Path.GetTempPath(), "bdfu-mover-tree-" + Guid.NewGuid().ToString("N"));

    public MoverTreeTests() => Directory.CreateDirectory(_volume);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        // A name that is no UTF-8 cannot be deleted through .NET's string paths.
        _ = Unlink(Z(Encoding.UTF8.GetBytes(_volume + "/odd-").Concat(new byte[] { 0xFF, 0xFE }).ToArray()));
        foreach (var path in Directory.EnumerateFileSystemEntries(_volume, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (IOException)
            {
                // A FIFO or a link: nothing to do.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Directory.Delete(_volume, recursive: true);
    }

    [Fact]
    public async Task A_volume_is_read_depth_first_by_name_bytes_with_raw_names_links_specials_and_issues()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64, "The mover reads volumes on linux-x64.");

        var big = RandomNumberGenerator.GetBytes((3 * 1024 * 1024) + 17);
        Directory.CreateDirectory(Path.Combine(_volume, "lost+found"));
        Directory.CreateDirectory(Path.Combine(_volume, "data", "inner"));
        File.WriteAllBytes(Path.Combine(_volume, "data", "big.bin"), big);
        File.WriteAllText(Path.Combine(_volume, "data", "inner", "a.txt"), "abc");
        File.WriteAllText(Path.Combine(_volume, "B-upper.txt"), "B");
        File.CreateSymbolicLink(Path.Combine(_volume, "link"), "data/inner/a.txt");
        Assert.Equal(0, Link(Z(Path.Combine(_volume, "data", "big.bin")), Z(Path.Combine(_volume, "same-as-big"))));
        Assert.Equal(0, MkFifo(Z(Path.Combine(_volume, "pipe")), 0b110_100_100));
        var oddName = Encoding.UTF8.GetBytes(_volume + "/odd-").Concat(new byte[] { 0xFF, 0xFE }).ToArray();
        var fd = OpenCreate(Z(oddName), 0x41 /* O_WRONLY | O_CREAT */, 0b110_000_000);
        Assert.True(fd >= 0);
        _ = Close(fd);
        using (var sparse = File.Create(Path.Combine(_volume, "sparse")))
        {
            sparse.SetLength(8 * 1024 * 1024);
        }

        var secret = Path.Combine(_volume, "secret");
        File.WriteAllText(secret, "not for everyone");
        File.SetUnixFileMode(secret, UnixFileMode.None);
        var asRoot = Environment.UserName == "root";

        using var stream = new MemoryStream();
        var summary = VolumeTree.Read(_volume, stream, TimeSpan.FromHours(1), Ct);
        stream.Position = 0;
        var (records, contents) = await ParseAsync(stream);

        // Depth first, entries sorted by their bytes ("B-upper" before "data", "data" before "link"), lost+found left out.
        Assert.Equal(
            ["F B-upper.txt", "D data", "F data/big.bin", "D data/inner", "F data/inner/a.txt", "U data/inner", "U data", "L link", "F odd-��", "P pipe", "H same-as-big",
             asRoot ? "F secret" : "I secret", "F sparse"],
            records.Select(r => r.Text));
        Assert.Equal(big, contents["data/big.bin"]);
        Assert.Equal("abc"u8.ToArray(), contents["data/inner/a.txt"]);
        Assert.Equal(new byte[8 * 1024 * 1024], contents["sparse"]);

        // A file without data blocks is one hole: sent as such, not read.
        var hole = records.Single(r => r.Text == "F sparse");
        Assert.True(hole.Holes);
        Assert.Equal(0, hole.SentBytes);
        Assert.Equal(big.Length, records.Single(r => r.Text == "F data/big.bin").SentBytes);
        Assert.Equal(new byte[] { (byte)'o', (byte)'d', (byte)'d', (byte)'-', 0xFF, 0xFE }, records.Single(r => r.Text.StartsWith("F odd-", StringComparison.Ordinal)).Name);
        Assert.Equal("data/inner/a.txt", Encoding.UTF8.GetString(records.Single(r => r.Text == "L link").Target!));
        Assert.Equal(0b110_000_000, records.Single(r => r.Text.StartsWith("F odd-", StringComparison.Ordinal)).Stat!.Value.Mode);
        Assert.Equal(2, records.Single(r => r.Text == "H same-as-big").Stat!.Value.Links);
        if (!asRoot)
        {
            Assert.Contains("Permission denied", records.Single(r => r.Text == "I secret").Reason, StringComparison.Ordinal);
            Assert.Equal(1, summary.Issues);
        }

        Assert.All(records.Where(r => r.Text.StartsWith('F')), r => Assert.Equal(TreeFileEnd.Complete, r.End));
    }

    [Fact]
    public async Task An_incremental_read_sends_unchanged_files_as_such_and_reads_what_changed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64, "The mover reads volumes on linux-x64.");

        Directory.CreateDirectory(Path.Combine(_volume, "dir", "sub"));
        Directory.CreateDirectory(Path.Combine(_volume, "turns-file"));
        File.WriteAllBytes(Path.Combine(_volume, "big.bin"), RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
        Assert.Equal(0, Link(Z(Path.Combine(_volume, "big.bin")), Z(Path.Combine(_volume, "same-as-big"))));
        File.WriteAllText(Path.Combine(_volume, "change.txt"), "before");
        File.WriteAllText(Path.Combine(_volume, "gone.txt"), "gone");
        File.WriteAllText(Path.Combine(_volume, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(_volume, "dir", "inner.txt"), "inner");
        File.WriteAllText(Path.Combine(_volume, "dir", "sub", "deep.txt"), "deep");
        File.WriteAllText(Path.Combine(_volume, "turns-dir"), "a file first");
        File.WriteAllText(Path.Combine(_volume, "turns-file", "f"), "in a folder first");

        // Just written: racy, read again next time whatever its stat says.
        using (var first = new MemoryStream())
        {
            VolumeTree.Read(_volume, first, TimeSpan.FromHours(1), Ct);
            first.Position = 0;
            Assert.All((await ParseAsync(first)).Records.Where(r => r.Text.StartsWith("F ", StringComparison.Ordinal)), r => Assert.True(r.Racy));
        }

        // Without the racy window (as if read later), the previous run's tree is what it sent.
        using var stream = new MemoryStream();
        VolumeTree.Read(_volume, stream, TimeSpan.FromHours(1), Ct, racy: TimeSpan.Zero);
        stream.Position = 0;
        var previous = Previous((await ParseAsync(stream)).Records);

        File.WriteAllText(Path.Combine(_volume, "change.txt"), "after!");
        File.Delete(Path.Combine(_volume, "gone.txt"));
        File.WriteAllText(Path.Combine(_volume, "new.txt"), "new");
        Directory.CreateDirectory(Path.Combine(_volume, "dir", "new-sub"));
        File.WriteAllText(Path.Combine(_volume, "dir", "new-sub", "x.txt"), "x");
        File.Delete(Path.Combine(_volume, "turns-dir"));
        Directory.CreateDirectory(Path.Combine(_volume, "turns-dir"));
        File.WriteAllText(Path.Combine(_volume, "turns-dir", "f"), "now a folder");
        Directory.Delete(Path.Combine(_volume, "turns-file"), recursive: true);
        File.WriteAllText(Path.Combine(_volume, "turns-file"), "now a file");

        using var second = new MemoryStream();
        var summary = VolumeTree.Read(_volume, second, TimeSpan.FromHours(1), Ct, previous, TimeSpan.Zero);
        second.Position = 0;
        var (records, contents) = await ParseAsync(second);
        Assert.Equal(
            ["S big.bin", "F change.txt", "D dir", "S dir/inner.txt", "D dir/new-sub", "F dir/new-sub/x.txt", "U dir/new-sub", "D dir/sub", "S dir/sub/deep.txt", "U dir/sub",
             "U dir", "S keep.txt", "F new.txt", "H same-as-big", "D turns-dir", "F turns-dir/f", "U turns-dir", "F turns-file"],
            records.Select(r => r.Text));
        Assert.Equal("after!"u8.ToArray(), contents["change.txt"]);
        Assert.Equal((4L, 0L), (summary.Unchanged, summary.Issues));
        Assert.Equal(6 + 1 + 3 + 12 + 10, summary.Bytes);
    }

    [Fact]
    public async Task Files_root_may_not_read_are_read_as_their_owner_as_on_an_nfs_export_with_root_squash()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64, "The mover reads volumes on linux-x64.");
        Assert.SkipUnless(Environment.IsPrivilegedProcess, "Acting as another owner needs root (CAP_SETUID, CAP_SETGID).");

        // A folder and a file only their owner may read; root without CAP_DAC_OVERRIDE is refused like a squashed root.
        var folder = Path.Combine(_volume, "private");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "secret.txt");
        File.WriteAllText(file, "for 1000 only");
        File.WriteAllText(Path.Combine(_volume, "mine.txt"), "1001's");

        // And one not even its owner may read: an issue that says so, not a squashed root.
        var locked = Path.Combine(_volume, "locked");
        File.WriteAllText(locked, "nobody's");
        Assert.Equal(0, Chown(Z(locked), 1000, 1000));
        File.SetUnixFileMode(locked, UnixFileMode.None);
        Assert.Equal(0, Chown(Z(file), 1000, 1000));
        Assert.Equal(0, Chown(Z(folder), 1000, 1000));
        Assert.Equal(0, Chown(Z(Path.Combine(_volume, "mine.txt")), 1001, 1001));
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(Path.Combine(_volume, "mine.txt"), UnixFileMode.UserRead);
        try
        {
            using (File.OpenRead(Path.Combine(_volume, "mine.txt")))
            {
            }

            Assert.Skip("Root may read everything here (CAP_DAC_OVERRIDE); run in a container without it (docker --cap-drop DAC_OVERRIDE --cap-drop DAC_READ_SEARCH).");
        }
        catch (UnauthorizedAccessException)
        {
            // Refused, as on the export.
        }

        using var stream = new MemoryStream();
        VolumeTree.Summary summary;
        try
        {
            summary = VolumeTree.Read(_volume, stream, TimeSpan.FromHours(1), Ct);

            // The walk's thread (this one) is root again afterwards: refused the same way.
            Assert.Throws<UnauthorizedAccessException>(() => File.OpenRead(file).Dispose());
        }
        finally
        {
            // Back to root, so the folder can be removed without CAP_DAC_OVERRIDE.
            foreach (var path in new[] { file, folder, Path.Combine(_volume, "mine.txt"), locked })
            {
                _ = Chown(Z(path), 0, 0);
            }
        }

        stream.Position = 0;
        var (records, contents) = await ParseAsync(stream);
        Assert.Equal(["I locked", "F mine.txt", "D private", "F private/secret.txt", "U private"], records.Select(r => r.Text));
        Assert.Equal("cannot open the file (also tried as its owner 1000:1000): Permission denied", records.Single(r => r.Text == "I locked").Reason);
        Assert.Equal("for 1000 only"u8.ToArray(), contents["private/secret.txt"]);
        Assert.Equal("1001's"u8.ToArray(), contents["mine.txt"]);
        Assert.Equal(1, summary.Issues);
        Assert.True(summary.AsOwner >= 2, $"{summary.AsOwner} read as their owner");
    }

    /// <summary>The previous run's tree as the server would send it, from the records of that run.</summary>
    private static MemoryStream Previous(List<Parsed> records)
    {
        var stream = new MemoryStream();
        var writer = new MoverPreviousWriter(stream);
        writer.Header();
        foreach (var record in records)
        {
            switch (record.Text[0])
            {
                case 'D':
                    writer.Enter(record.Name!);
                    break;
                case 'U':
                    writer.Leave();
                    break;
                case 'F' when record is { End: TreeFileEnd.Complete, Racy: false, Stat: { } stat }:
                    writer.File(record.Name!, new PreviousFile(stat.Size, stat.MtimeNs, stat.CtimeNs, stat.Inode));
                    break;
            }
        }

        writer.End();
        stream.Position = 0;
        return stream;
    }

    private sealed record Parsed(string Text, byte[]? Name = null, TreeStat? Stat = null, byte[]? Target = null, string? Reason = null)
    {
        public TreeFileEnd? End { get; set; }

        public bool Racy { get; set; }

        public bool Holes { get; set; }

        public long SentBytes { get; set; }
    }

    private static async Task<(List<Parsed> Records, Dictionary<string, byte[]> Contents)> ParseAsync(Stream stream)
    {
        var reader = new MoverTreeReader(stream);
        await reader.ReadHeaderAsync(Ct);
        var records = new List<Parsed>();
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var path = new Stack<string>();
        MemoryStream? current = null;
        Parsed? file = null;
        string Full(byte[] name) => string.Join('/', path.Reverse().Append(Encoding.UTF8.GetString(name)));
        while (true)
        {
            switch (await reader.ReadAsync(Ct))
            {
                case TreeEnter e:
                    records.Add(new Parsed($"D {Full(e.Name)}", e.Name, e.Stat));
                    path.Push(Encoding.UTF8.GetString(e.Name));
                    break;
                case TreeLeave:
                    records.Add(new Parsed($"U {string.Join('/', path.Reverse())}"));
                    path.Pop();
                    break;
                case TreeFileStart f:
                    file = new Parsed($"F {Full(f.Name)}", f.Name, f.Stat);
                    records.Add(file);
                    current = new MemoryStream();
                    contents[Full(f.Name)] = [];
                    break;
                case TreePiece p:
                    current!.Write(p.Data.Span);
                    file!.SentBytes += p.Data.Length;
                    break;
                case TreeHole h:
                    current!.Write(new byte[h.Length]);
                    break;
                case TreeFileEndRecord z:
                    file!.End = z.End;
                    file.Racy = z.Racy;
                    file.Holes = z.Holes;
                    contents[file.Text[2..]] = current!.ToArray();
                    break;
                case TreeUnchanged u:
                    records.Add(new Parsed($"S {Full(u.Name)}", u.Name, u.Stat));
                    break;
                case TreeHardLink h:
                    records.Add(new Parsed($"H {Full(h.Name)}", h.Name, h.Stat));
                    break;
                case TreeSymlink l:
                    records.Add(new Parsed($"L {Full(l.Name)}", l.Name, l.Stat, l.Target));
                    break;
                case TreeSpecial s:
                    records.Add(new Parsed($"P {Full(s.Name)}", s.Name, s.Stat));
                    break;
                case TreeIssue i:
                    records.Add(new Parsed($"I {Full(i.Name)}", i.Name, Reason: i.Reason));
                    break;
                case TreeEnd:
                    return (records, contents);
            }
        }
    }

    private static byte[] Z(string path) => Z(Encoding.UTF8.GetBytes(path));

    private static byte[] Z(byte[] path) => [.. path, 0];

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(byte[] from, byte[] to);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(byte[] path, uint mode);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenCreate(byte[] path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "unlink", SetLastError = true)]
    private static extern int Unlink(byte[] path);

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown(byte[] path, uint owner, uint group);
}
