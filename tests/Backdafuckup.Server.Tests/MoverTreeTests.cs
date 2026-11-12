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

    private sealed record Parsed(string Text, byte[]? Name = null, TreeStat? Stat = null, byte[]? Target = null, string? Reason = null)
    {
        public TreeFileEnd? End { get; set; }
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
                    break;
                case TreeHole h:
                    current!.Write(new byte[h.Length]);
                    break;
                case TreeFileEndRecord z:
                    file!.End = z.End;
                    contents[file.Text[2..]] = current!.ToArray();
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
}
