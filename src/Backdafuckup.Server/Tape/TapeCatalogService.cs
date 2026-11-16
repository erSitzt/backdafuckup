using Backdafuckup.Contracts;
using Backdafuckup.Server.Api;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Tape.Crypto;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Tape;

/// <summary>Keys, media pools, tape jobs, sets and copies: what the console edits and lists.</summary>
public sealed class TapeCatalogService(IDbContextFactory<AppDbContext> dbFactory, CredentialProtector protector, IOptions<TapeOptions> options, TimeProvider time)
{
    public async Task<IReadOnlyList<TapeKeyDto>> KeysAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var keys = await db.TapeKeys.AsNoTracking().OrderBy(k => k.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        var pools = await db.MediaPools.AsNoTracking().Where(p => p.KeyId != null).Select(p => new { p.KeyId, p.Name }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return keys.Select(k => new TapeKeyDto(k.Id, k.Name, k.Fingerprint, k.CreatedUtc, k.KeyFileDownloaded, pools.Where(p => p.KeyId == k.Id).Select(p => p.Name).ToList())).ToList();
    }

    /// <summary>Derives the key from the passphrase (Argon2id; seconds and 256 MiB) and keeps only the key, protected.</summary>
    public async Task<TapeKeyDto> CreateKeyAsync(CreateTapeKeyDto request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100)
        {
            throw new TapeRequestException("The name must have 1 to 100 characters.");
        }

        if (request.Passphrase is not { Length: >= 12 })
        {
            throw new TapeRequestException("The passphrase needs at least 12 characters. Without it (or the key file) nothing on the tapes can be read.");
        }

        var parameters = new TapeKdfParameters(options.Value.KdfMemoryKiB, options.Value.KdfIterations, options.Value.KdfParallelism, System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var kek = await Task.Run(() => TapeKeys.DeriveKek(request.Passphrase, parameters), cancellationToken).ConfigureAwait(false);
        var fingerprint = TapeKeys.Fingerprint(kek);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.TapeKeys.AnyAsync(k => k.Name == name, cancellationToken).ConfigureAwait(false))
        {
            throw new TapeRequestException($"A tape key named '{name}' exists already.", StatusCodes.Status409Conflict);
        }

        var key = new TapeKeyEntity
        {
            Name = name,
            Fingerprint = fingerprint,
            ProtectedKek = protector.Protect(Convert.ToBase64String(kek)),
            MemoryKiB = parameters.MemoryKiB,
            Iterations = parameters.Iterations,
            Parallelism = parameters.Parallelism,
            Salt = Convert.ToBase64String(parameters.Salt),
            CreatedUtc = time.GetUtcNow(),
        };
        db.TapeKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(kek);
        return new TapeKeyDto(key.Id, key.Name, key.Fingerprint, key.CreatedUtc, false, []);
    }

    /// <summary>The key file, once: afterwards only the passphrase can bring the key to another server.</summary>
    public async Task<TapeKeyFile> TakeKeyFileAsync(Guid keyId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.TapeKeys.FirstOrDefaultAsync(k => k.Id == keyId, cancellationToken).ConfigureAwait(false)
            ?? throw new TapeRequestException($"Tape key {keyId} does not exist.", StatusCodes.Status404NotFound);
        if (key.KeyFileDownloaded)
        {
            throw new TapeRequestException($"The key file of {key.Name} was downloaded already; it is offered once.", StatusCodes.Status409Conflict);
        }

        key.KeyFileDownloaded = true;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var kek = Convert.FromBase64String(protector.Unprotect(key.ProtectedKek));
        return TapeKeyFile.Create(kek, new TapeKdfParameters(key.MemoryKiB, key.Iterations, key.Parallelism, Convert.FromBase64String(key.Salt)));
    }

    public async Task DeleteKeyAsync(Guid keyId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.TapeKeys.FirstOrDefaultAsync(k => k.Id == keyId, cancellationToken).ConfigureAwait(false)
            ?? throw new TapeRequestException($"Tape key {keyId} does not exist.", StatusCodes.Status404NotFound);
        if (await db.MediaPools.AnyAsync(p => p.KeyId == keyId, cancellationToken).ConfigureAwait(false)
            || await db.MediaSets.AnyAsync(s => s.KeyFingerprint == key.Fingerprint && s.State != MediaSetState.Expired && s.State != MediaSetState.Overwritten, cancellationToken).ConfigureAwait(false))
        {
            throw new TapeRequestException($"Key {key.Name} ({key.Fingerprint}) is in use by a pool or by sets that are kept; without it they cannot be read.", StatusCodes.Status409Conflict);
        }

        db.TapeKeys.Remove(key);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MediaPoolDto>> PoolsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pools = await db.MediaPools.AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        var libraries = await db.TapeLibraries.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, cancellationToken).ConfigureAwait(false);
        var keys = await db.TapeKeys.AsNoTracking().ToDictionaryAsync(k => k.Id, k => k.Fingerprint, cancellationToken).ConfigureAwait(false);
        var sets = await SetsAsync(db, null, cancellationToken).ConfigureAwait(false);
        var media = await db.TapeMedia.AsNoTracking().Where(m => m.PoolId != null).GroupBy(m => new { m.PoolId, m.State }).Select(g => new { g.Key.PoolId, g.Key.State, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return pools.Select(p => new MediaPoolDto(
            p.Id, p.Name, p.LibraryId, libraries.GetValueOrDefault(p.LibraryId) ?? "?", p.NewSet.ToString(), p.TimeZoneId, p.RetentionDays, p.WormOnly, p.Encryption.ToString(),
            p.KeyId, p.KeyId is { } k ? keys.GetValueOrDefault(k) : null, p.Export.ToString(), p.BlockSize / 1024,
            sets.FirstOrDefault(s => s.PoolId == p.Id && s.State == nameof(MediaSetState.Open)),
            media.Where(m => m.PoolId == p.Id).ToDictionary(m => m.State.ToString(), m => m.Count))).ToList();
    }

    public async Task<MediaPoolDto> SavePoolAsync(Guid? id, MediaPoolRequestDto request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100)
        {
            throw new TapeRequestException("The name must have 1 to 100 characters.");
        }

        var newSet = Parse<TapeNewSet>(request.NewSet, "new set");
        var encryption = Parse<TapeEncryption>(request.Encryption, "encryption");
        var export = Parse<TapeExport>(request.Export, "export");
        if (request.BlockSizeKiB is not (64 or 128 or 256 or 512 or 1024))
        {
            throw new TapeRequestException("The block size is 64, 128, 256 (recommended), 512 or 1024 KiB.");
        }

        if (request.RetentionDays is < 0)
        {
            throw new TapeRequestException("Retention is days from 0, or empty for ever.");
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new TapeRequestException($"Unknown time zone '{request.TimeZoneId}'.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.TapeLibraries.AnyAsync(l => l.Id == request.LibraryId, cancellationToken).ConfigureAwait(false))
        {
            throw new TapeRequestException($"Tape library {request.LibraryId} does not exist.");
        }

        if (encryption != TapeEncryption.None && (request.KeyId is not { } keyId || !await db.TapeKeys.AnyAsync(k => k.Id == keyId, cancellationToken).ConfigureAwait(false)))
        {
            throw new TapeRequestException("An encrypting pool needs a key: create one from a passphrase first.");
        }

        if (await db.MediaPools.AnyAsync(p => p.Name == name && p.Id != id, cancellationToken).ConfigureAwait(false))
        {
            throw new TapeRequestException($"A media pool named '{name}' exists already.", StatusCodes.Status409Conflict);
        }

        var pool = id is { } existing
            ? await db.MediaPools.FirstOrDefaultAsync(p => p.Id == existing, cancellationToken).ConfigureAwait(false) ?? throw new TapeRequestException($"Media pool {existing} does not exist.", StatusCodes.Status404NotFound)
            : db.MediaPools.Add(new MediaPoolEntity { Name = name, CreatedUtc = time.GetUtcNow() }).Entity;
        if (id is not null && pool.LibraryId != request.LibraryId && await db.MediaSets.AnyAsync(s => s.PoolId == pool.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new TapeRequestException($"Pool {pool.Name} has sets on tapes of its library; it cannot move to another library.");
        }

        pool.Name = name;
        pool.LibraryId = request.LibraryId;
        pool.NewSet = newSet;
        pool.TimeZoneId = request.TimeZoneId;
        pool.RetentionDays = request.RetentionDays;
        pool.WormOnly = request.WormOnly;
        pool.Encryption = encryption;
        pool.KeyId = encryption == TapeEncryption.None ? null : request.KeyId;
        pool.Export = export;
        pool.BlockSize = request.BlockSizeKiB * 1024;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (await PoolsAsync(cancellationToken).ConfigureAwait(false)).First(p => p.Id == pool.Id);
    }

    public async Task DeletePoolAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pool = await db.MediaPools.FirstOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false)
            ?? throw new TapeRequestException($"Media pool {id} does not exist.", StatusCodes.Status404NotFound);
        if (await db.TapeJobs.AnyAsync(j => j.PoolId == id, cancellationToken).ConfigureAwait(false) || await db.MediaSets.AnyAsync(s => s.PoolId == id, cancellationToken).ConfigureAwait(false))
        {
            throw new TapeRequestException($"Pool {pool.Name} has tape jobs or sets; remove the jobs, and keep the pool while its sets are on tape.", StatusCodes.Status409Conflict);
        }

        db.MediaPools.Remove(pool);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MediaSetDto>> SetsAsync(Guid? poolId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await SetsAsync(db, poolId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TapeJobDto>> JobsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var jobs = await db.TapeJobs.AsNoTracking().OrderBy(j => j.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        var pools = await db.MediaPools.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken).ConfigureAwait(false);
        var sources = await db.Jobs.AsNoTracking().ToDictionaryAsync(j => j.Id, j => j.Name, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        return jobs.Select(j => new TapeJobDto(
            j.Id, j.Name, j.Enabled, j.PoolId, pools.GetValueOrDefault(j.PoolId) ?? "?", j.SourceJobIds, j.SourceJobIds.Select(s => sources.GetValueOrDefault(s) ?? "(removed)").ToList(),
            j.Mode.ToString(), j.IncludeSqlBackups, j.Schedule.ToDto(), j.AfterJobId, j.LastRunUtc, j.LastResult?.ToString(),
            j.Enabled ? ScheduleCalculator.Next(j.Schedule, now) : null)).ToList();
    }

    public async Task<TapeJobDto> SaveJobAsync(Guid? id, TapeJobRequestDto request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100)
        {
            throw new TapeRequestException("The name must have 1 to 100 characters.");
        }

        var mode = Parse<TapeJobMode>(request.Mode, "mode");
        if (request.SourceJobIds is not { Count: > 0 })
        {
            throw new TapeRequestException("Choose at least one backup job whose restore points go to tape.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.MediaPools.AnyAsync(p => p.Id == request.PoolId, cancellationToken).ConfigureAwait(false))
        {
            throw new TapeRequestException($"Media pool {request.PoolId} does not exist.");
        }

        var known = await db.Jobs.Where(j => request.SourceJobIds.Contains(j.Id)).Select(j => j.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (known.Count != request.SourceJobIds.Distinct().Count())
        {
            throw new TapeRequestException("A source job does not exist.");
        }

        if (request.AfterJobId is { } after && !await db.Jobs.AnyAsync(j => j.Id == after, cancellationToken).ConfigureAwait(false))
        {
            throw new TapeRequestException($"Backup job {after} does not exist.");
        }

        if (await db.TapeJobs.AnyAsync(j => j.Name == name && j.Id != id, cancellationToken).ConfigureAwait(false))
        {
            throw new TapeRequestException($"A tape job named '{name}' exists already.", StatusCodes.Status409Conflict);
        }

        var job = id is { } existing
            ? await db.TapeJobs.FirstOrDefaultAsync(j => j.Id == existing, cancellationToken).ConfigureAwait(false) ?? throw new TapeRequestException($"Tape job {existing} does not exist.", StatusCodes.Status404NotFound)
            : db.TapeJobs.Add(new TapeJobEntity { Name = name, CreatedUtc = time.GetUtcNow() }).Entity;
        job.Name = name;
        job.PoolId = request.PoolId;
        job.SourceJobIds = [.. request.SourceJobIds.Distinct()];
        job.Mode = mode;
        job.IncludeSqlBackups = request.IncludeSqlBackups;
        job.Schedule = request.Schedule?.FromDto() ?? new JobSchedule();
        job.AfterJobId = request.AfterJobId;
        job.Enabled = request.Enabled;
        job.NextRunUtc = null; // the scheduler computes it from the (new) schedule
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (await JobsAsync(cancellationToken).ConfigureAwait(false)).First(j => j.Id == job.Id);
    }

    public async Task DeleteJobAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await db.TapeJobs.Where(j => j.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (deleted == 0)
        {
            throw new TapeRequestException($"Tape job {id} does not exist.", StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Marks a cartridge free (it is written over on its next use) or damaged (never used). Both need its barcode typed;
    /// free is refused for WORM cartridges and for tapes of a set that is kept.
    /// </summary>
    public async Task SetMediumStateAsync(Guid mediumId, TapeMediumStateDto request, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var medium = await db.TapeMedia.FirstOrDefaultAsync(m => m.Id == mediumId, cancellationToken).ConfigureAwait(false)
            ?? throw new TapeRequestException($"Cartridge {mediumId} does not exist.", StatusCodes.Status404NotFound);
        if (!string.Equals(request.Barcode?.Trim(), medium.Barcode, StringComparison.Ordinal))
        {
            throw new TapeRequestException($"Type the barcode {medium.Barcode} to confirm.");
        }

        switch (request.State)
        {
            case "Free":
                if (medium.Kind == TapeMediumKind.Worm)
                {
                    throw new TapeRequestException($"{medium.Barcode} is a WORM cartridge: what is on it cannot be written over.");
                }

                if (medium.SetId is { } setId && await db.MediaSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == setId, cancellationToken).ConfigureAwait(false) is { State: MediaSetState.Open or MediaSetState.Closed } set)
                {
                    throw new TapeRequestException(
                        $"{medium.Barcode} is tape #{medium.Sequence} of a set that is kept{(set.ExpiresUtc is { } until ? $" until {until:yyyy-MM-dd}" : string.Empty)}; it becomes reusable when its set expires.",
                        StatusCodes.Status409Conflict);
                }

                medium.State = TapeMediumState.Free;
                medium.SetId = null;
                medium.PoolId = null;
                medium.Sequence = null;
                medium.FileCount = 0;
                break;
            case "Damaged":
                medium.State = TapeMediumState.Damaged;
                break;
            default:
                throw new TapeRequestException($"A cartridge is marked Free or Damaged, not '{request.State}'.");
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TapeCopyDto>> CopiesAsync(string? vmId, Guid? originalId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.TapeCopies.AsNoTracking();
        if (vmId is not null)
        {
            query = query.Where(c => c.VmId == vmId);
        }

        if (originalId is not null)
        {
            query = query.Where(c => c.OriginalId == originalId);
        }

        var copies = await query.OrderByDescending(c => c.CreatedUtc).Take(1000).ToListAsync(cancellationToken).ConfigureAwait(false);
        var setIds = copies.Select(c => c.SetId).Distinct().ToList();
        var sets = await db.MediaSets.AsNoTracking().Where(s => setIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken).ConfigureAwait(false);
        var pools = await db.MediaPools.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken).ConfigureAwait(false);
        var media = await db.TapeMedia.AsNoTracking().Where(m => m.SetId != null && setIds.Contains(m.SetId.Value)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var libraries = await db.TapeLibraries.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, cancellationToken).ConfigureAwait(false);
        return copies.Select(c => new TapeCopyDto(
            c.Id, c.OriginalId, c.VmId, c.VmName, c.JobName, c.ObjectKind, c.CreatedUtc, c.CopiedUtc, c.SetId, sets[c.SetId].State.ToString(), pools.GetValueOrDefault(c.PoolId) ?? "?", c.StoredBytes,
            sets[c.SetId].KeyFingerprint is not null,
            c.NeededSequences.Select(s => media.FirstOrDefault(m => m.SetId == c.SetId && m.Sequence == s) is { } m
                ? new TapeNeededDto(s, m.Barcode, m.Location.ToString(), m.LibraryId is { } l ? libraries.GetValueOrDefault(l) : null)
                : new TapeNeededDto(s, $"#{s}", nameof(TapeLocation.Offline), null)).ToList(),
            c.Kind)).ToList();
    }

    private static async Task<List<MediaSetDto>> SetsAsync(AppDbContext db, Guid? poolId, CancellationToken cancellationToken)
    {
        var query = db.MediaSets.AsNoTracking();
        if (poolId is not null)
        {
            query = query.Where(s => s.PoolId == poolId);
        }

        var sets = await query.OrderByDescending(s => s.CreatedUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
        var pools = await db.MediaPools.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken).ConfigureAwait(false);
        var copies = await db.TapeCopies.AsNoTracking().GroupBy(c => c.SetId).Select(g => new { SetId = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.SetId, g => g.Count, cancellationToken).ConfigureAwait(false);
        return sets.Select(s => new MediaSetDto(
            s.Id, s.PoolId, pools.GetValueOrDefault(s.PoolId) ?? "?", s.State.ToString(), s.CreatedUtc, s.ClosedUtc, s.ExpiresUtc, s.Media, s.Bytes, copies.GetValueOrDefault(s.Id), s.KeyFingerprint)).ToList();
    }

    private static T Parse<T>(string? value, string what)
        where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new TapeRequestException($"Unknown {what} '{value}'; it is one of {string.Join(", ", Enum.GetNames<T>())}.");
}
