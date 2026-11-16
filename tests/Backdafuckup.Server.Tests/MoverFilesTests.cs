using System.Runtime.Versioning;
using System.Text;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Mover;

namespace Backdafuckup.Server.Tests;

/// <summary>The mover's side of a file restore into a live volume (<see cref="VolumeFiles"/>), on a folder as the volume.</summary>
[SupportedOSPlatform("linux")]
public sealed class MoverFilesTests : IDisposable
{
    private static readonly DateTime Modified = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-mover-files-" + Guid.NewGuid().ToString("N"));
    private readonly string _volume;
    private readonly string _outside;

    public MoverFilesTests()
    {
        _volume = Path.Combine(_root, "volume");
        _outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(_volume);
        Directory.CreateDirectory(_outside);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Files_folders_and_links_get_their_content_mode_and_times_and_folder_times_come_last()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The mover puts files into volumes on Linux only (statx, lchown).");

        var report = await ApplyAsync(keep: false, owners: false, w =>
        {
            w.Directory(0, "folder");
            w.File(1, "folder/a.txt", Text("abc"), 3, Ct);
            w.Meta(1, "folder/a.txt", folder: false, new MoverFileMeta(0b110_100_000, null, null, Modified, Modified));
            w.Link(2, "folder/link", "a.txt", null, null);
            w.Meta(3, "folder", folder: true, new MoverFileMeta(0b111_101_000, null, null, Modified, null));
            w.File(4, "b.txt", Text(string.Empty), 0, Ct);
        });

        Assert.Equal((2L, 3L, 0L, 0L, 0L), (report.Files, report.Bytes, report.Failed, report.Renamed, report.Partial));
        Assert.Equal("abc", await File.ReadAllTextAsync(Path.Combine(_volume, "folder/a.txt"), Ct));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(Path.Combine(_volume, "folder/a.txt")));
        Assert.Equal(Modified, File.GetLastWriteTimeUtc(Path.Combine(_volume, "folder/a.txt")));
        Assert.Equal("a.txt", new FileInfo(Path.Combine(_volume, "folder/link")).LinkTarget);
        Assert.Equal(Modified, Directory.GetLastWriteTimeUtc(Path.Combine(_volume, "folder")));
        Assert.Equal((UnixFileMode)0b111_101_000, File.GetUnixFileMode(Path.Combine(_volume, "folder")));
        Assert.Equal((UnixFileMode)0b110_100_100, File.GetUnixFileMode(Path.Combine(_volume, "b.txt"))); // no meta: 0644
        Assert.Empty(Directory.EnumerateFiles(_volume, ".bdfu-restore-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Replace_replaces_existing_files_and_keep_writes_restored_copies_with_free_names()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The mover puts files into volumes on Linux only (statx, lchown).");

        await File.WriteAllTextAsync(Path.Combine(_volume, "a.txt"), "old", Ct);
        await File.WriteAllTextAsync(Path.Combine(_volume, "RESTORED-a.txt"), "older copy", Ct);

        var kept = await ApplyAsync(keep: true, owners: false, w => w.File(0, "a.txt", Text("new"), 3, Ct));
        Assert.Equal((1L, 1L), (kept.Files, kept.Renamed));
        Assert.Equal(new MoverFileIssue(0, MoverFileIssueKinds.Renamed, "RESTORED-a (2).txt"), Assert.Single(kept.Issues));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(_volume, "a.txt"), Ct));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(_volume, "RESTORED-a (2).txt"), Ct));

        var replaced = await ApplyAsync(keep: false, owners: false, w => w.File(0, "a.txt", Text("newer"), 5, Ct));
        Assert.Equal((1L, 0L), (replaced.Files, replaced.Renamed));
        Assert.Equal("newer", await File.ReadAllTextAsync(Path.Combine(_volume, "a.txt"), Ct));
    }

    [Fact]
    public async Task Nothing_is_written_through_a_link_or_outside_the_volume_and_conflicts_with_folders_fail()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The mover puts files into volumes on Linux only (statx, lchown).");

        File.CreateSymbolicLink(Path.Combine(_volume, "evil"), _outside);
        Directory.CreateDirectory(Path.Combine(_volume, "dir"));

        var report = await ApplyAsync(keep: false, owners: false, w =>
        {
            w.File(0, "evil/x.txt", Text("x"), 1, Ct);
            w.File(1, "../x.txt", Text("x"), 1, Ct);
            w.File(2, "dir", Text("x"), 1, Ct);
            w.Directory(3, "evil");
            w.Link(4, "dir", "/etc", null, null);
            w.File(5, "ok.txt", Text("ok"), 2, Ct);
        });

        Assert.Equal((1L, 5L), (report.Files, report.Failed));
        var reasons = report.Issues.ToDictionary(i => i.Index, i => i.Text);
        Assert.Equal("evil is a symbolic link; nothing is written through it", reasons[0]);
        Assert.Equal("the path ../x.txt is not a plain path below the volume's root", reasons[1]);
        Assert.Equal("a folder with this name exists", reasons[2]);
        Assert.Equal("a symbolic link with this name exists; nothing is written through it", reasons[3]);
        Assert.Equal("a folder with this name exists", reasons[4]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
        Assert.False(File.Exists(Path.Combine(_root, "x.txt")));
        Assert.Equal("ok", await File.ReadAllTextAsync(Path.Combine(_volume, "ok.txt"), Ct));
    }

    [Fact]
    public async Task A_file_the_server_cannot_read_leaves_nothing_behind_and_a_stream_that_breaks_off_fails()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The mover puts files into volumes on Linux only (statx, lchown).");

        using var stream = new MemoryStream();
        var writer = new MoverFileWriter(stream);
        writer.WriteHeader();
        var damaged = Assert.Throws<IOException>(() => writer.File(0, "broken.bin", new FailingStream(Encoding.UTF8.GetBytes("abc")), 10, Ct));
        Assert.Equal("chunk 42 is damaged", damaged.Message);
        var shortened = Assert.Throws<IOException>(() => writer.File(1, "short.bin", Text("abc"), 5, Ct));
        Assert.Contains("ended after 3 of its 5 bytes", shortened.Message, StringComparison.Ordinal);
        writer.End(2);
        stream.Position = 0;
        var report = await new VolumeFiles(_volume, false, false, TextWriter.Null).ApplyAsync(stream, Ct);
        Assert.Equal((0L, 0L), (report.Files, report.Failed)); // the server reports these failures itself
        Assert.Empty(Directory.EnumerateFileSystemEntries(_volume));

        // Cut after a complete file: the stream has no end, the file waiting for its times is not put in place.
        using var cut = new MemoryStream();
        var cutWriter = new MoverFileWriter(cut);
        cutWriter.WriteHeader();
        cutWriter.File(0, "pending.txt", Text("abc"), 3, Ct);
        cut.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => new VolumeFiles(_volume, false, false, TextWriter.Null).ApplyAsync(cut, Ct));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_volume));
    }

    [Fact]
    public async Task A_volume_that_keeps_no_owners_is_reported_once_and_the_files_are_still_restored()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The mover puts files into volumes on Linux only (statx, lchown).");
        if (Environment.IsPrivilegedProcess)
        {
            return; // root may give files to anyone
        }

        // Like an NFS export with root_squash: the mover cannot give files to other users (here: it is no root).
        var report = await ApplyAsync(keep: false, owners: true, w =>
        {
            w.Directory(0, "data");
            w.File(1, "data/owned.txt", Text("x"), 1, Ct);
            w.Meta(1, "data/owned.txt", folder: false, new MoverFileMeta(0b110_100_100, 0, 0, Modified, null));
            w.File(2, "data/other.txt", Text("y"), 1, Ct);
            w.Meta(2, "data/other.txt", folder: false, new MoverFileMeta(0b110_000_000, 999, 999, Modified, null));
            w.Meta(0, "data", folder: true, new MoverFileMeta(0b111_000_000, 999, 999, Modified, null));
        });

        Assert.Equal((2L, 0L, 0L), (report.Files, report.Partial, report.Failed));
        var issue = Assert.Single(report.Issues);
        Assert.Equal((-1, MoverFileIssueKinds.Partial), (issue.Index, issue.Kind));
        Assert.StartsWith("owners are not kept: the volume does not let the mover give files to other users", issue.Text, StringComparison.Ordinal);
        Assert.Equal(Modified, File.GetLastWriteTimeUtc(Path.Combine(_volume, "data", "owned.txt")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_volume, "data", "other.txt")));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_volume), e => Path.GetFileName(e).StartsWith(".bdfu-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_volume_root_gets_the_mode_and_times_of_the_backed_up_root()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The mover puts files into volumes on Linux only (statx, lchown).");

        // A whole volume into a new one: the root's own meta comes last, under the empty path.
        var report = await ApplyAsync(keep: false, owners: false, w =>
        {
            w.File(0, "PG_VERSION", Text("17"), 2, Ct);
            w.Meta(1, string.Empty, folder: true, new MoverFileMeta(0b111_000_000, null, null, Modified, null));
        });

        Assert.Equal((1L, 0L, 0L), (report.Files, report.Partial, report.Failed));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_volume));
        Assert.Equal(Modified, Directory.GetLastWriteTimeUtc(_volume));
    }

    /// <summary>Writes a stream with its end and applies it to the volume.</summary>
    private async Task<MoverFileReport> ApplyAsync(bool keep, bool owners, Action<MoverFileWriter> write)
    {
        using var stream = new MemoryStream();
        var writer = new MoverFileWriter(stream);
        writer.WriteHeader();
        write(writer);
        writer.End(0);
        stream.Position = 0;
        return await new VolumeFiles(_volume, keep, owners, TextWriter.Null).ApplyAsync(stream, Ct);
    }

    private static MemoryStream Text(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>Gives its bytes, then fails like a damaged chunk of the repository.</summary>
    private sealed class FailingStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            return read > 0 ? read : throw new IOException("chunk 42 is damaged");
        }
    }
}
