using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>A lost database: keys added again, the catalog rebuilt from the tapes, a copy brought back.</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task After_a_lost_database_the_catalog_is_rebuilt_from_the_tapes_and_their_copies_come_back()
    {
        var client = await SignedInClientAsync();
        var fake = _tapeHost.Libraries[0];
        fake.AddCartridge("BDF000L9", 64L << 20);
        fake.AddCartridge("BDF001L9", 64L << 20);
        var point = await BackUpImageAsync(client, "db02", RandomNumberGenerator.GetBytes(4 << 20));
        var library = await Read<TapeLibraryDto>(client.PostAsJsonAsync("/api/tape/libraries", new CreateTapeLibraryDto("Lab", "local", "LIB0001", [new("DRV0001", 256, null)]), Ct));
        var key = await Read<TapeKeyDto>(client.PostAsJsonAsync("/api/tape/keys", new CreateTapeKeyDto("Vault", "correct horse battery staple"), Ct));
        var keyFile = await (await client.PostAsync($"/api/tape/keys/{key.Id}/file", null, Ct)).Content.ReadAsStringAsync(Ct);
        // Encrypted by the drive: reading needs the set's key in the drive after every load.
        var pool = await Read<MediaPoolDto>(client.PostAsJsonAsync("/api/tape/pools", new MediaPoolRequestDto("Vault weekly", library.Id, Encryption: "Hardware", KeyId: key.Id), Ct));
        var job = await Read<TapeJobDto>(client.PostAsJsonAsync("/api/tape/jobs", new TapeJobRequestDto("To the vault", pool.Id, [point.JobId!.Value]), Ct));
        var written = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsync($"/api/tape/jobs/{job.Id}/run", null, Ct))).SessionId);
        Assert.True(written.Session.State == "Succeeded", written.Session.Summary);
        var before = Assert.Single((await client.GetFromJsonAsync<List<TapeCopyDto>>("/api/tape/copies", Ct))!);

        // The database loses everything it knew about tape; the library and its cartridges are found again.
        await using (var db = await _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct))
        {
            await db.TapeCopies.ExecuteDeleteAsync(Ct);
            await db.TapeFiles.ExecuteDeleteAsync(Ct);
            await db.TapeJobs.ExecuteDeleteAsync(Ct);
            await db.MediaSets.ExecuteDeleteAsync(Ct);
            await db.MediaPools.ExecuteDeleteAsync(Ct);
            await db.TapeKeys.ExecuteDeleteAsync(Ct);
            await db.TapeMedia.ExecuteUpdateAsync(s => s
                .SetProperty(m => m.State, TapeMediumState.Unknown).SetProperty(m => m.SetId, (Guid?)null).SetProperty(m => m.PoolId, (Guid?)null)
                .SetProperty(m => m.Sequence, (int?)null).SetProperty(m => m.MediumId, (Guid?)null).SetProperty(m => m.LabelName, (string?)null)
                .SetProperty(m => m.FileCount, 0).SetProperty(m => m.BytesWritten, 0L).SetProperty(m => m.LabelEncryption, (string?)null), Ct);
        }

        async Task<(SessionSummaryDto Session, List<string> Log)> RebuildAsync()
        {
            var id = (await Read<StartedDto>(client.PostAsJsonAsync($"/api/tape/libraries/{library.Id}/actions", new TapeActionDto(TapeActions.Rebuild), Ct))).SessionId;
            var done = await WaitForSessionAsync(client, id);
            return (done.Session, (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{id}/log", Ct))!.Select(l => l.Message).ToList());
        }

        // Without the key: both tapes read, the set named with the key it needs, nothing else.
        var (locked, lockedLog) = await RebuildAsync();
        Assert.True(locked.State == "Warning", locked.Summary);
        Assert.Equal("0 set(s) rebuilt from 2 tape(s) with 0 copie(s); 1 set(s) could not be read (see the log).", locked.Summary);
        Assert.Contains(lockedLog, m => m.Contains($"is encrypted with key {key.Fingerprint}, which this server does not have", StringComparison.Ordinal));

        // The key again: a wrong passphrase matches none; the right one finds how the tape's key was made; so does the key file.
        var wrong = await client.PostAsJsonAsync("/api/tape/keys", new CreateTapeKeyDto("Vault", "not the passphrase", Existing: true), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains($"matches none of the keys the tapes here name ({key.Fingerprint})", await wrong.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var byPassphrase = await Read<TapeKeyDto>(client.PostAsJsonAsync("/api/tape/keys", new CreateTapeKeyDto("Vault", "correct horse battery staple", Existing: true), Ct));
        Assert.Equal((key.Fingerprint, false), (byPassphrase.Fingerprint, byPassphrase.KeyFileDownloaded));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/tape/keys/{byPassphrase.Id}", Ct)).StatusCode);
        var byFile = await Read<TapeKeyDto>(client.PostAsJsonAsync("/api/tape/keys", new CreateTapeKeyDto("Vault", null, keyFile, Existing: true), Ct));
        Assert.Equal((key.Fingerprint, true), (byFile.Fingerprint, byFile.KeyFileDownloaded));
        var twice = await client.PostAsJsonAsync("/api/tape/keys", new CreateTapeKeyDto("Vault again", null, keyFile, Existing: true), Ct);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);

        // Rebuilt: the pool under its id with its key, the set closed, the tape full, the copy as it was.
        var (rebuilt, _) = await RebuildAsync();
        Assert.True(rebuilt.State == "Succeeded", rebuilt.Summary);
        Assert.Equal("1 set(s) rebuilt from 1 tape(s) with 1 copie(s).", rebuilt.Summary);
        var pools = (await client.GetFromJsonAsync<List<MediaPoolDto>>("/api/tape/pools", Ct))!;
        Assert.Equal([(pool.Id, "Vault weekly", "Hardware", (Guid?)byFile.Id)], pools.Select(p => (p.Id, p.Name, p.Encryption, p.KeyId)).ToList());
        var set = Assert.Single((await client.GetFromJsonAsync<List<MediaSetDto>>("/api/tape/sets", Ct))!);
        Assert.Equal(("Closed", 1, 1, key.Fingerprint), (set.State, set.Media, set.Copies, set.KeyFingerprint));
        var tape = (await client.GetFromJsonAsync<List<TapeMediumDto>>("/api/tape/media", Ct))!.Single(m => m.SetId == set.Id);
        Assert.Equal(("BDF000L9", "Full", 1), (tape.Barcode, tape.State, tape.Sequence));
        var after = Assert.Single((await client.GetFromJsonAsync<List<TapeCopyDto>>("/api/tape/copies", Ct))!);
        Assert.Equal((before.Id, before.OriginalId, before.VmName, before.StoredBytes, before.CreatedUtc), (after.Id, after.OriginalId, after.VmName, after.StoredBytes, after.CreatedUtc));
        Assert.Equal("BDF000L9", Assert.Single(after.Tapes).Barcode);

        // Nothing left to read; and the copy comes back from the drive-encrypted tape.
        Assert.Equal("Every tape in Lab is known: nothing to rebuild. Tapes put in since the last inventory are found by an inventory first.", (await RebuildAsync()).Session.Summary);
        var target = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "After the loss", Path = Path.Combine(_root, "after-loss") }, Ct));
        var back = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync($"/api/tape/copies/{after.Id}/restore", new TapeRestoreRequestDto(target.Id), Ct))).SessionId);
        Assert.True(back.Session.State == "Succeeded", back.Session.Summary);
        var imported = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>("/api/restore-points", Ct))!, r => r.RepositoryId == target.Id);
        Assert.True((await Read<VerifyResultDto>(client.PostAsync($"/api/restore-points/{imported.Id}/verify?full=true", null, Ct))).Ok);
    }
}
