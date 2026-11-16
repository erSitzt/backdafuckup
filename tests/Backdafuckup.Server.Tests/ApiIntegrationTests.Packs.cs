using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Engine;
using Backdafuckup.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>Repository pack files (spec 2026-11-16-1804-repository-pack-files): new repositories, and converting old ones.</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task New_repositories_use_pack_files_and_an_old_one_converts_in_a_maintenance_session()
    {
        var client = await SignedInClientAsync();

        var created = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "New", Path = Path.Combine(_root, "new"), PackSizeMiB = 256 }, Ct));
        Assert.Equal((2, 256L * 1024 * 1024), (created.Format, created.PackSize));
        var tooSmall = await client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Small", Path = Path.Combine(_root, "small"), PackSizeMiB = 8 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooSmall.StatusCode);

        // A repository written by an older version: one file per chunk.
        var oldPath = Path.Combine(_root, "old");
        var disk = RandomNumberGenerator.GetBytes(64 * 1024 * 5);
        Guid pointId;
        await using (var old = await LocalDirectoryRepository.CreateAsync(oldPath, Ct, 64 * 1024, 3, null, packTargetSize: 0))
        {
            pointId = await WriteRestorePointAsync(old, "vm-old", disk);
        }

        var imported = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Old", Path = oldPath }, Ct));
        Assert.Equal(1, imported.Format);

        var started = await Read<StartedDto>(client.PostAsJsonAsync($"/api/repositories/{imported.Id}/packs", new ConvertRepositoryDto(), Ct));
        var session = await WaitForSessionAsync(client, started.SessionId);
        Assert.Equal("Succeeded", session.Session.State);
        Assert.Contains("converted to pack files", session.Session.Summary, StringComparison.Ordinal);
        Assert.Contains("5 loose chunk(s) packed", session.Session.Summary, StringComparison.Ordinal);

        var listed = (await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repositories", Ct))!.Single(r => r.Id == imported.Id);
        Assert.Equal((2, 64L * 1024 * 1024, false), (listed.Format, listed.PackSize, listed.PackingUnfinished));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(oldPath, "chunks"), "*", SearchOption.AllDirectories));
        var again = await client.PostAsJsonAsync($"/api/repositories/{imported.Id}/packs", new ConvertRepositoryDto(), Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var repository = await _factory!.Services.GetRequiredService<RepositoryManager>().GetAsync(imported.Id, Ct);
        var verify = await repository.VerifyAsync(pointId, VerifyDepth.Full, null, Ct);
        Assert.True(verify.Ok, string.Join("; ", verify.Problems));
    }

    private static async Task<Guid> WriteRestorePointAsync(LocalDirectoryRepository repository, string vm, byte[] disk)
    {
        const int chunkSize = 64 * 1024;
        var id = Guid.CreateVersion7();
        await using var writer = repository.BeginWrite(id, vm, null);
        var index = ChunkIndex.CreateEmpty(chunkSize, disk.Length);
        for (var i = 0; i < index.Entries.Length; i++)
        {
            var data = disk.AsMemory(i * chunkSize, chunkSize);
            index.Entries[i] = ChunkId.Compute(data.Span);
            await writer.PutChunkAsync(index.Entries[i], data, Ct);
        }

        var indexRef = await writer.WriteDiskIndexAsync("0", index, Ct);
        await writer.CommitAsync(
            new RestorePointManifest
            {
                RestorePointId = id,
                RepositoryId = repository.Info.RepositoryId,
                CreatedUtc = DateTimeOffset.UtcNow,
                Kind = RestorePointKind.Full,
                Source = new ManifestSource(HypervisorKind.XenServer, "pool", vm, vm, null),
                Vm = new VmDescriptor { VmId = vm, Name = vm },
                Chunking = new ManifestChunking(chunkSize, "sha256", "zstd"),
                Disks =
                [
                    new ManifestDisk
                    {
                        Key = "0",
                        SourceDiskId = "disk",
                        Name = "disk",
                        CapacityBytes = disk.Length,
                        Index = indexRef,
                        Stats = new DiskBackupStats(),
                    },
                ],
            },
            Ct);
        return id;
    }
}
