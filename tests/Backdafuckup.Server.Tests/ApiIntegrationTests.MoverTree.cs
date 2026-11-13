using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;
using Backdafuckup.DataMover;
using Backdafuckup.Mover;
using Backdafuckup.Storage;

namespace Backdafuckup.Server.Tests;

/// <summary>A mover reading a volume file by file through the server's tree channel into a repository (spec 2026-11-12-1952).</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task A_mover_sends_a_volume_file_by_file_and_the_server_builds_its_tree_in_the_repository()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64, "The mover reads volumes on linux-x64.");
        const int ChunkSize = 64 * 1024;
        var channels = await MoverChannelsAsync();
        var volume = Path.Combine(_root, "tree-volume");
        Directory.CreateDirectory(Path.Combine(volume, "data", "inner"));
        var big = RandomNumberGenerator.GetBytes((ChunkSize * 5) + 123);
        Array.Clear(big, ChunkSize, ChunkSize * 2); // two pieces of zeros: not stored
        await File.WriteAllBytesAsync(Path.Combine(volume, "data", "big.bin"), big, Ct);
        await File.WriteAllTextAsync(Path.Combine(volume, "data", "inner", "small.txt"), "small", Ct);
        await File.WriteAllBytesAsync(Path.Combine(volume, "empty"), [], Ct);
        File.CreateSymbolicLink(Path.Combine(volume, "link"), "data/inner/small.txt");

        await using var repository = await LocalDirectoryRepository.CreateAsync(Path.Combine(_root, "tree-repo"), Ct, ChunkSize, 3);
        var id = Guid.CreateVersion7();
        var writer = repository.BeginWrite(id, "bdfu-spike", null);
        await using (writer)
        {
            var builder = new FileTreeBuilder(writer, ChunkSize);
            FileTreeBuilder.Result? result = null;
            await using var channel = await channels.OpenTreeBackupAsync("bdfu-spike/data-redis-test-0", Ct);
            var consuming = channel.ConsumeAsync(async (stream, ct) => result = await builder.BuildAsync(stream, ct), Ct);

            var log = new StringWriter();
            var options = new MoverOptions(MoverMode.Tree, channel.Endpoint.DataUrl, channel.Endpoint.ResultUrl, channel.Endpoint.Token, channel.Endpoint.ServerPins, "/dev/null", Root: volume);
            Assert.Equal(0, await MoverRunner.RunAsync(options, _factory!.Server.CreateHandler(), log, Ct));
            await consuming;
            Assert.Contains("file by file", log.ToString(), StringComparison.Ordinal);

            Assert.NotNull(result);
            Assert.Equal((3L, 2L, (long)big.Length + 5, 0L), (result.Stats.Files, result.Stats.Directories, result.Stats.Bytes, result.Stats.Issues));
            var index = await writer.WriteFileTreeIndexAsync("data-redis-test-0", result.Root, Ct);
            await writer.CommitAsync(
                new RestorePointManifest
                {
                    RestorePointId = id, RepositoryId = repository.Info.RepositoryId, CreatedUtc = DateTimeOffset.UtcNow, Kind = RestorePointKind.Full,
                    Source = new ManifestSource(HypervisorKind.Kubernetes, "cluster", "bdfu-spike", "bdfu-spike", null), Vm = new VmDescriptor { VmId = "bdfu-spike", Name = "bdfu-spike" },
                    Chunking = new ManifestChunking(ChunkSize, "sha256", "zstd"), Disks = [],
                    FileTrees = [new ManifestFileTree { Key = "data-redis-test-0", Name = "data-redis-test-0", Index = index, RootLength = result.Root.DiskSize, Root = result.RootMeta, Stats = result.Stats }],
                },
                Ct);
        }

        // Read back: the same tree, the same bytes, the link as a link; zero pieces not stored.
        await using var reader = await repository.OpenRestorePointAsync(id, Ct);
        var root = await reader.ReadFileTreeIndexAsync("data-redis-test-0", Ct);
        var entries = FileTreeWalker.Enumerate(ChunkStreamRef.OfIds(root.DiskSize, root.Entries), reader.Chunks, ChunkSize).ToDictionary(e => e.Path, e => e.Entry);
        Assert.Equal(["data", "data/big.bin", "data/inner", "data/inner/small.txt", "empty", "link"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(big, ChunkStreams.ReadAll(entries["data/big.bin"].Content!, reader.Chunks, ChunkSize));
        Assert.Equal(2, entries["data/big.bin"].Content!.Ids!.Count(c => c.IsZero));
        Assert.Equal("small"u8.ToArray(), entries["data/inner/small.txt"].Content!.Inline);
        Assert.Equal(0, entries["empty"].Size);
        Assert.Equal("data/inner/small.txt", System.Text.Encoding.UTF8.GetString(entries["link"].LinkTarget!));
        Assert.Equal((1L, 5L), (entries["data/inner"].SubtreeFiles, entries["data/inner"].SubtreeBytes));
        Assert.True((await repository.VerifyAsync(id, VerifyDepth.Full, null, Ct)).Ok);
    }

    [Fact]
    public async Task A_mover_that_cannot_read_its_volume_reports_why_not_that_its_upload_broke()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64, "The mover reads volumes on linux-x64.");
        const int ChunkSize = 64 * 1024;
        var channels = await MoverChannelsAsync();
        await using var repository = await LocalDirectoryRepository.CreateAsync(Path.Combine(_root, "tree-repo-missing"), Ct, ChunkSize, 3);
        await using var writer = repository.BeginWrite(Guid.CreateVersion7(), "bdfu-spike", null);
        await using var channel = await channels.OpenTreeBackupAsync("bdfu-spike/data-missing", Ct);
        var consuming = channel.ConsumeAsync(async (stream, ct) => await new FileTreeBuilder(writer, ChunkSize).BuildAsync(stream, ct), Ct);

        var missing = Path.Combine(_root, "not-mounted");
        var options = new MoverOptions(MoverMode.Tree, channel.Endpoint.DataUrl, channel.Endpoint.ResultUrl, channel.Endpoint.Token, channel.Endpoint.ServerPins, "/dev/null", Root: missing);
        var log = new StringWriter();
        Assert.Equal(1, await MoverRunner.RunAsync(options, _factory!.Server.CreateHandler(), log, Ct));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => consuming);
        Assert.Contains($"Reading {missing} failed: The volume at {missing} cannot be opened", error.Message, StringComparison.Ordinal);
    }
}
