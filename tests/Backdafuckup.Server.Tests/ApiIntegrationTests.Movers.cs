using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Mover;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Movers;
using Backdafuckup.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>The mover program against the server's channels, with a file as the volume's block device.</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task A_mover_sends_a_volume_without_its_zero_blocks_and_writes_it_back_to_a_new_volume()
    {
        var channels = await MoverChannelsAsync();
        var volume = TestVolume(capacity: (9 * 1024 * 1024) + 4096 + 17);
        var device = Path.Combine(_root, "volume.img");
        await File.WriteAllBytesAsync(device, volume, Ct);

        // Backup: the channel hands the volume to its reader (the backup pipeline) as a sequential disk.
        var received = new byte[volume.Length];
        var records = new List<(long Offset, int Length)>();
        var backup = await channels.OpenBackupAsync("bdfu-spike/data-redis-test-0", volume.Length, Ct);
        await using (backup)
        {
            Assert.Equal(("mover", volume.LongLength), (backup.TransportName, backup.Capacity));
            Assert.StartsWith("https://localhost/api/agent/movers/", backup.Endpoint.DataUrl.ToString(), StringComparison.Ordinal);
            var streaming = backup.StreamAsync(
                (offset, data, _) =>
                {
                    Assert.True(records.Count == 0 || offset >= records[^1].Offset + records[^1].Length);
                    data.Span.CopyTo(received.AsSpan((int)offset));
                    records.Add((offset, data.Length));
                    return ValueTask.CompletedTask;
                },
                Ct);

            var log = new StringWriter();
            Assert.Equal(0, await MoverRunner.RunAsync(Options(MoverMode.Backup, backup.Endpoint, device), _factory!.Server.CreateHandler(), log, Ct));
            await streaming;
            Assert.Equal(volume, received);
            Assert.Contains("Backup of", log.ToString(), StringComparison.Ordinal);

            // Only the non-zero blocks travelled: the three data regions, each rounded to the 4 KiB blocks of the mover.
            Assert.Equal(3, records.Count);
            Assert.Equal(4096 + 8192 + 4096 + 17, records.Sum(r => r.Length));

            // The token is used: a second connection is refused, as is a wrong token.
            using var again = Mover(backup.Endpoint.Token);
            Assert.Equal(HttpStatusCode.Conflict, (await again.PutAsync(backup.Endpoint.DataUrl, new ByteArrayContent([]), Ct)).StatusCode);
            using var wrong = Mover("not-the-token");
            Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.PutAsync(backup.Endpoint.DataUrl, new ByteArrayContent([]), Ct)).StatusCode);
        }

        // Closed with its run: the channel is gone.
        using (var late = Mover(backup.Endpoint.Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await late.PostAsJsonAsync(backup.Endpoint.ResultUrl, new MoverResult(0), Ct)).StatusCode);
        }

        // Restore: the mover writes the data regions to a new (zeroed, here larger) volume, flushes it and reports.
        var target = Path.Combine(_root, "new-volume.img");
        await File.WriteAllBytesAsync(target, new byte[volume.Length + 65536], Ct);
        await using (var restore = await channels.OpenRestoreAsync("bdfu-spike/data-redis-test-0", new ByteDiskImage(received, chunkSize: 1024 * 1024), Ct))
        {
            var log = new StringWriter();
            Assert.Equal(0, await MoverRunner.RunAsync(Options(MoverMode.Restore, restore.Endpoint, target), _factory!.Server.CreateHandler(), log, Ct));
            Assert.Equal((2L * 1024 * 1024) + 4096 + 17, await restore.Completion); // the three chunks with data (the last one short)
            var written = await File.ReadAllBytesAsync(target, Ct);
            Assert.Equal(volume, written[..volume.Length]);
            Assert.All(written[volume.Length..], b => Assert.Equal(0, b));
        }

        // Too small a volume: the mover refuses and reports it; the restore fails with its message.
        var small = Path.Combine(_root, "small-volume.img");
        await File.WriteAllBytesAsync(small, new byte[4096], Ct);
        await using (var restore = await channels.OpenRestoreAsync("bdfu-spike/data-redis-test-0", new ByteDiskImage(received), Ct))
        {
            Assert.Equal(1, await MoverRunner.RunAsync(Options(MoverMode.Restore, restore.Endpoint, small), _factory!.Server.CreateHandler(), new StringWriter(), Ct));
            var error = await Assert.ThrowsAsync<IOException>(() => restore.Completion);
            Assert.Contains("bdfu-spike/data-redis-test-0", error.Message, StringComparison.Ordinal);
            Assert.Contains($"The device {small} has 4096 bytes", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_backup_channel_fails_with_the_movers_error_or_when_no_mover_connects()
    {
        var channels = await MoverChannelsAsync();

        // The device cannot be opened: the mover reports it, and the backup fails with the path in the message.
        await using (var backup = await channels.OpenBackupAsync("bdfu-spike/data-a", 1024 * 1024, Ct))
        {
            var streaming = backup.StreamAsync((_, _, _) => ValueTask.CompletedTask, Ct);
            var missing = Path.Combine(_root, "no-such-device");
            Assert.Equal(1, await MoverRunner.RunAsync(Options(MoverMode.Backup, backup.Endpoint, missing), _factory!.Server.CreateHandler(), new StringWriter(), Ct));
            var error = await Assert.ThrowsAsync<IOException>(() => streaming);
            Assert.Contains($"The volume's device {missing} cannot be opened", error.Message, StringComparison.Ordinal);
        }

        // A device of another size than the volume: refused.
        var device = Path.Combine(_root, "other-size.img");
        await File.WriteAllBytesAsync(device, new byte[8192], Ct);
        await using (var backup = await channels.OpenBackupAsync("bdfu-spike/data-b", 4096, Ct))
        {
            var streaming = backup.StreamAsync((_, _, _) => ValueTask.CompletedTask, Ct);
            Assert.Equal(1, await MoverRunner.RunAsync(Options(MoverMode.Backup, backup.Endpoint, device), _factory!.Server.CreateHandler(), new StringWriter(), Ct));
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => streaming);
            Assert.Equal("The mover's device for bdfu-spike/data-b has 8192 bytes, the volume 4096.", error.Message);
        }

        // No mover within the connect timeout: the backup fails and names the channel's address.
        channels.ConnectTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            await using var backup = await channels.OpenBackupAsync("bdfu-spike/data-c", 4096, Ct);
            var error = await Assert.ThrowsAsync<TimeoutException>(() => backup.StreamAsync((_, _, _) => ValueTask.CompletedTask, Ct));
            Assert.Contains($"The mover for bdfu-spike/data-c did not connect to {backup.Endpoint.DataUrl}", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            channels.ConnectTimeout = TimeSpan.FromMinutes(15);
        }
    }

    [Fact]
    public async Task A_files_channel_holds_its_writer_back_and_releases_it_with_the_reason_when_the_mover_fails()
    {
        var channels = await MoverChannelsAsync();

        // Nobody reads: the writer stops at the pipe's limit, and the pod watcher's abort releases it with the reason.
        await using (var files = await channels.OpenFilesAsync("PVC bdfu-spike/data-a", Ct))
        {
            var written = 0L;
            var writer = Task.Factory.StartNew(
                () =>
                {
                    var chunk = new byte[256 * 1024];
                    while (true)
                    {
                        files.Content.Write(chunk);
                        Interlocked.Add(ref written, chunk.Length);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            await Task.Delay(500, Ct);
            Assert.False(writer.IsCompleted);
            Assert.InRange(Interlocked.Read(ref written), 1, 16L * 1024 * 1024);
            files.Abort(new InvalidOperationException("The mover pod bdfu-spike/bdfu-x-mover cannot start (ImagePullBackOff)"));
            var error = await Assert.ThrowsAsync<IOException>(() => writer);
            Assert.Contains("ImagePullBackOff", error.Message, StringComparison.Ordinal);
            Assert.False(files.IsUsable);
            await Assert.ThrowsAsync<InvalidOperationException>(() => files.Completion);
        }

        // The mover reports an error while the server still sends: writes fail with the mover's reason, as does the restore.
        await using (var files = await channels.OpenFilesAsync("PVC bdfu-spike/data-b", Ct))
        {
            using var mover = Mover(files.Endpoint.Token);
            var get = mover.GetAsync(files.Endpoint.DataUrl, HttpCompletionOption.ResponseHeadersRead, Ct);
            await files.Connected.WaitAsync(Ct);
            await Task.Run(() => files.Content.Write("BDFUFIL1"u8), Ct);
            using var response = await get;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using (var again = Mover(files.Endpoint.Token))
            {
                Assert.Equal(HttpStatusCode.Conflict, (await again.GetAsync(files.Endpoint.DataUrl, Ct)).StatusCode);
            }

            Assert.Equal(HttpStatusCode.NoContent, (await mover.PostAsJsonAsync(files.Endpoint.ResultUrl, new MoverResult(0, "No space left on device (/bdfu-volume/dump.rdb)"), Ct)).StatusCode);
            var failed = await Assert.ThrowsAsync<IOException>(() => files.Completion);
            Assert.Equal("The mover for PVC bdfu-spike/data-b failed: No space left on device (/bdfu-volume/dump.rdb)", failed.Message);
            var refused = Assert.Throws<IOException>(() => files.Content.Write(new byte[16]));
            Assert.Equal(failed.Message, refused.Message);
        }
    }

    [Fact]
    public async Task The_mover_image_goes_to_the_registry_set_in_the_console_and_a_backup_pushes_only_what_is_missing()
    {
        var console = await SignedInClientAsync();
        var initial = (await console.GetFromJsonAsync<MoverImageDto>("/api/kubernetes/mover", Ct))!;
        Assert.Equal((string.Empty, "backdafuckup/mover", MoverPullSecretModes.Create, false, false), (initial.Registry, initial.Repository, initial.PullSecret, initial.HasPassword, initial.UpToDate));
        Assert.Equal("0.3.0", initial.Shipped!.Version);
        Assert.Null(initial.Pushed);

        // Pushing needs a registry; registry and repository are checked.
        var none = await console.PostAsync("/api/kubernetes/mover/push", null, Ct);
        Assert.Equal(HttpStatusCode.BadGateway, none.StatusCode);
        Assert.Contains("No registry is set for the mover image", await none.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var withScheme = await console.PutAsJsonAsync("/api/kubernetes/mover", new SaveMoverImageDto("https://registry.test:8443", "backdafuckup/mover", "bdfu-push", "secret-1", "Create", null, null, null), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, withScheme.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await console.PutAsJsonAsync("/api/kubernetes/mover", new SaveMoverImageDto("registry.test:8443", "Backdafuckup/Mover", "bdfu-push", "secret-1", "Create", null, null, null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await console.PutAsJsonAsync("/api/kubernetes/mover", new SaveMoverImageDto("registry.test:8443", "backdafuckup/mover", null, null, "Existing", null, null, "Not_A_Name"), Ct)).StatusCode);

        // Saved: the password is stored encrypted and never returned.
        var saved = await Read<MoverImageDto>(console.PutAsJsonAsync("/api/kubernetes/mover", new SaveMoverImageDto("registry.test:8443", "/backdafuckup/mover/", " bdfu-push ", "secret-1", "Create", null, null, null), Ct));
        Assert.Equal(("registry.test:8443", "backdafuckup/mover", "bdfu-push", true, false), (saved.Registry, saved.Repository, saved.UserName, saved.HasPassword, saved.UpToDate));
        Assert.DoesNotContain("secret-1", await console.GetStringAsync("/api/kubernetes/mover", Ct), StringComparison.Ordinal);
        await using (var db = await _factory!.Services.GetRequiredService<IDbContextFactory<Backdafuckup.Server.Data.AppDbContext>>().CreateDbContextAsync(Ct))
        {
            Assert.DoesNotContain(await db.Settings.Select(s => s.Value).ToListAsync(Ct), v => v.Contains("secret-1", StringComparison.Ordinal));
        }

        // Pushed: the image is in the registry, referenced by its digest.
        var pushed = await Read<MoverPushResultDto>(console.PostAsync("/api/kubernetes/mover/push", null, Ct));
        Assert.True(pushed.Image.UpToDate);
        Assert.Equal(pushed.Image.Shipped!.Digest, pushed.Image.Pushed!.Digest);
        Assert.Equal($"registry.test:8443/backdafuckup/mover@{pushed.Image.Shipped.Digest}", pushed.Image.Pushed.Reference);
        Assert.Equal(pushed.Image.Shipped.Tag, pushed.Image.Pushed.Tag);
        Assert.True(_registry.Manifests.ContainsKey($"backdafuckup/mover:{pushed.Image.Shipped.Tag}"));
        Assert.Contains(pushed.Log, l => l.Contains("stored", StringComparison.Ordinal));

        // A backup finds it there and pushes nothing; the pull secret comes from the push user.
        var registry = _factory.Services.GetRequiredService<MoverRegistry>();
        _registry.Requests.Clear();
        var deployment = await registry.EnsurePushedAsync(null, Ct);
        Assert.Empty(_registry.Requests);
        Assert.Equal((pushed.Image.Pushed.Reference, MoverDeployment.CreatedSecretName), (deployment.Image, deployment.PullSecretName));

        // Another repository: not current any more; a backup pushes there (the password kept from before).
        await Ok(console.PutAsJsonAsync("/api/kubernetes/mover", new SaveMoverImageDto("registry.test:8443", "tools/mover", "bdfu-push", null, "Existing", null, null, "registry-pull"), Ct));
        Assert.False((await console.GetFromJsonAsync<MoverImageDto>("/api/kubernetes/mover", Ct))!.UpToDate);
        deployment = await registry.EnsurePushedAsync(null, Ct);
        Assert.Equal(($"registry.test:8443/tools/mover@{pushed.Image.Shipped.Digest}", "registry-pull", null), (deployment.Image, deployment.PullSecretName, deployment.DockerConfigJson));

        // A wrong password: the push fails with the registry's answer.
        await Ok(console.PutAsJsonAsync("/api/kubernetes/mover", new SaveMoverImageDto("registry.test:8443", "other/mover", "bdfu-push", "wrong", "None", null, null, null), Ct));
        var refused = await console.PostAsync("/api/kubernetes/mover/push", null, Ct);
        Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        Assert.Contains("The registry registry.test:8443 refused getting a token for other/mover", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private async Task<MoverChannels> MoverChannelsAsync()
    {
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        return _factory.Services.GetRequiredService<MoverChannels>();
    }

    private static MoverOptions Options(MoverMode mode, MoverEndpoint endpoint, string device) =>
        new(mode, endpoint.DataUrl, endpoint.ResultUrl, endpoint.Token, endpoint.ServerPins, device, BlockBytes: 4096);

    private HttpClient Mover(string token)
    {
        var client = _factory!.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>A volume of zeros with data in three places: 4 KiB at 1 MiB, 8 KiB (two blocks) at 5 MiB, and the last 4 KiB + 17 bytes.</summary>
    private static byte[] TestVolume(int capacity)
    {
        var volume = new byte[capacity];
        var random = new Random(4711);
        random.NextBytes(volume.AsSpan(1024 * 1024, 4096));
        random.NextBytes(volume.AsSpan(5 * 1024 * 1024, 8192));
        random.NextBytes(volume.AsSpan(capacity - 4096 - 17));
        return volume;
    }
}
