using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>Backup to tape (spec 2026-11-16-1804-backup-to-tape): a tape job into an encrypted set across tapes, and back into a repository.</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task A_tape_job_copies_restore_points_across_tapes_and_an_import_brings_them_back_byte_for_byte()
    {
        var client = await SignedInClientAsync();
        var fake = _tapeHost.Libraries[0];
        fake.BecomingReadyPolls = 1;
        for (var i = 0; i < 6; i++)
        {
            fake.AddCartridge($"BDF00{i}L9", 12L << 20);
        }

        // A VM of 24 MiB that does not compress, with a hole of zeros; backed up once.
        var image = RandomNumberGenerator.GetBytes(24 << 20);
        Array.Clear(image, 4 << 20, 2 << 20);
        var point = await BackUpImageAsync(client, "web01", image);

        var library = await Read<TapeLibraryDto>(client.PostAsJsonAsync("/api/tape/libraries", new CreateTapeLibraryDto("Lab", "local", "LIB0001", [new("DRV0001", 256, null), new("DRV0002", 257, null)]), Ct));
        var key = await Read<TapeKeyDto>(client.PostAsJsonAsync("/api/tape/keys", new CreateTapeKeyDto("Lab key", "correct horse battery staple"), Ct));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/tape/keys", new CreateTapeKeyDto("Short", "too short"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/tape/pools", new MediaPoolRequestDto("Weekly", library.Id, Encryption: "Software"), Ct)).StatusCode);
        var pool = await Read<MediaPoolDto>(client.PostAsJsonAsync("/api/tape/pools", new MediaPoolRequestDto("Weekly", library.Id, NewSet: "Never", RetentionDays: 7, Encryption: "Software", KeyId: key.Id), Ct));
        Assert.Equal(key.Fingerprint, pool.KeyFingerprint);
        var job = await Read<TapeJobDto>(client.PostAsJsonAsync("/api/tape/jobs", new TapeJobRequestDto("To tape", pool.Id, [point.JobId!.Value]), Ct));

        async Task<SessionDetailDto> RunAsync()
        {
            var started = await Read<StartedDto>(client.PostAsync($"/api/tape/jobs/{job.Id}/run", null, Ct));
            return await WaitForSessionAsync(client, started.SessionId);
        }

        // The first run: one restore point, spread over the 12 MiB tapes, each labelled for the set.
        var first = await RunAsync();
        Assert.True(first.Session.State == "Succeeded", first.Session.Summary + string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{first.Session.Id}/log", Ct))!.Select(l => l.Message)));
        Assert.Contains("1 restore point(s) copied", first.Session.Summary, StringComparison.Ordinal);
        var copy = Assert.Single((await client.GetFromJsonAsync<List<TapeCopyDto>>("/api/tape/copies", Ct))!);
        Assert.Equal(("web01", point.Id, true, "Open"), (copy.VmName, copy.OriginalId, copy.Encrypted, copy.SetState));
        Assert.True(copy.Tapes.Count >= 2, $"tapes: {string.Join(", ", copy.Tapes.Select(t => t.Barcode))}");
        var media = (await client.GetFromJsonAsync<List<TapeMediumDto>>("/api/tape/media", Ct))!;
        Assert.All(media.Where(m => m.SetId is not null), m => Assert.Equal("Weekly", m.Pool));
        Assert.Contains(media, m => m.State == "Full");
        Assert.Contains(media, m => m.State == "Appendable");
        Assert.All(media.Where(m => m.State is "Full" or "Appendable"), m => Assert.Equal(1024 + int.Parse(m.Barcode[3..6], System.Globalization.CultureInfo.InvariantCulture), m.Address));

        // Encrypted: nothing of the VM's name or its manifest on the tapes; the label and the file headers stay readable.
        var raw = fake.Cartridges.SelectMany(c => c.Records).Where(r => r.Data is not null).SelectMany(r => r.Data!).ToArray();
        Assert.Equal(-1, raw.AsSpan().IndexOf("web01"u8));
        Assert.NotEqual(-1, raw.AsSpan().IndexOf("BDFU-TAPE"u8));

        // Nothing new: the second run copies nothing.
        Assert.Equal("Nothing to copy: the set has every restore point the job asks for.", (await RunAsync()).Session.Summary);

        // A second backup changes 1 MiB: only that chunk is new on tape, appended to the set's last tape.
        var disk0 = _pool.Records("VDI").Single(r => r["name_label"]!.GetValue<string>() == "web01 disk 0" && r["is_a_snapshot"]?.GetValue<bool>() != true)["uuid"]!.GetValue<string>();
        _pool.WriteDisk(disk0, 10 << 20, RandomNumberGenerator.GetBytes(1 << 20));
        var backup = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{point.JobId}/run", new RunJobDto(), Ct));
        Assert.Equal("Succeeded", (await WaitForSessionAsync(client, backup.SessionId)).Session.State);
        var second = await RunAsync();
        Assert.True(second.Session.State == "Succeeded", second.Session.Summary);
        Assert.Contains("1 restore point(s) copied", second.Session.Summary, StringComparison.Ordinal);
        Assert.Contains("chunk(s) the set had already", second.Session.Summary, StringComparison.Ordinal);
        var copies = (await client.GetFromJsonAsync<List<TapeCopyDto>>("/api/tape/copies", Ct))!;
        Assert.Equal(2, copies.Count);

        // Back into another repository with the same block size: byte for byte, belonging to no job.
        var target = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "From tape", Path = Path.Combine(_root, "from-tape") }, Ct));
        var restore = await Read<StartedDto>(client.PostAsJsonAsync($"/api/tape/copies/{copy.Id}/restore", new TapeRestoreRequestDto(target.Id), Ct));
        var restored = await WaitForSessionAsync(client, restore.SessionId);
        Assert.True(restored.Session.State == "Succeeded", restored.Session.Summary + string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{restore.SessionId}/log", Ct))!.Select(l => l.Message)));
        var imported = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>("/api/restore-points", Ct))!, r => r.RepositoryId == target.Id);
        Assert.Null(imported.JobId);
        Assert.StartsWith("Imported from tape", imported.JobName, StringComparison.Ordinal);
        Assert.Equal(("web01", point.CreatedUtc), (imported.VmName, imported.CreatedUtc));

        var repository = await _factory!.Services.GetRequiredService<RepositoryManager>().GetAsync(target.Id, Ct);
        await using (var reader = await repository.OpenRestorePointAsync(imported.Id, Ct))
        {
            var index = await reader.ReadDiskIndexAsync(reader.Manifest.Disks[0].Key, Ct);
            var disk = new byte[image.Length];
            var buffer = new byte[index.ChunkSize];
            for (var i = 0; i < index.Entries.Length; i++)
            {
                if (!index.Entries[i].IsZero)
                {
                    var n = await reader.Chunks.ReadAsync(index.Entries[i], buffer, Ct);
                    buffer.AsSpan(0, n).CopyTo(disk.AsSpan(i * index.ChunkSize));
                }
            }

            Assert.True(image.AsSpan().SequenceEqual(disk), "the disk read back from tape differs");
        }

        var verify = await repository.VerifyAsync(imported.Id, VerifyDepth.Full, null, Ct);
        Assert.True(verify.Ok, string.Join("; ", verify.Problems));
        Assert.All(fake.Drives, d => Assert.False(d.Loaded));
    }
}
