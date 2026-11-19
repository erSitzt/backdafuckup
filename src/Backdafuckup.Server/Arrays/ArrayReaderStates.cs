using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Arrays;

/// <summary>
/// The experimental readers' state per storage system: verify runs passed, the format version last seen, and the trip (the
/// reader switched off after a mismatch until an administrator turns it back on). Updates are read-modify-write on the
/// storage system's row and retried when another VM of the same job run changed it meanwhile.
/// </summary>
public sealed class ArrayReaderStates(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, ILogger<ArrayReaderStates> logger)
{
    /// <summary>VHDs in LVM on a LUN (XCP-ng LVM over iSCSI or FC).</summary>
    public const string Lvhd = "lvhd";

    /// <summary>VMDKs on a VMFS6 datastore, read without ESXi.</summary>
    public const string Vmfs6 = "vmfs6";

    public static readonly IReadOnlyList<string> All = [Lvhd, Vmfs6];

    public static string DisplayName(string reader) => reader switch
    {
        Lvhd => "LVHD (XCP-ng LVM storage)",
        Vmfs6 => "VMFS6 (vSphere datastores)",
        _ => reader,
    };

    public static StorageReaderDto ToDto(StorageReaderState state, StorageSystemOptions options) =>
        new(state.Reader, DisplayName(state.Reader), state.VerifiedRuns, options.ProbationRuns, state.FormatVersion, state.TrippedUtc, state.TripReason);

    public static StorageReaderState Find(StorageSystemEntity system, string reader)
    {
        ArgumentNullException.ThrowIfNull(system);
        return system.Readers?.FirstOrDefault(r => r.Reader == reader) ?? new StorageReaderState(reader);
    }

    /// <summary>One more backup passed verify mode.</summary>
    public async Task<StorageReaderState> RecordVerifiedAsync(Guid systemId, string reader, CancellationToken cancellationToken) =>
        (await UpdateAsync(systemId, reader, r => r with { VerifiedRuns = r.VerifiedRuns + 1 }, cancellationToken).ConfigureAwait(false)).State;

    /// <summary>
    /// Records the on-disk format version the reader found (e.g. "VMFS 6.82"). A version other than the one seen before starts
    /// verify mode again; returns the previous version when it changed.
    /// </summary>
    public async Task<string?> ObserveFormatAsync(Guid systemId, string reader, string? version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(version))
        {
            return null;
        }

        string? previous = null;
        await UpdateAsync(systemId, reader, r =>
        {
            if (r.FormatVersion == version)
            {
                return r;
            }

            previous = r.FormatVersion;
            return r with { FormatVersion = version, VerifiedRuns = r.FormatVersion is null ? r.VerifiedRuns : 0 };
        }, cancellationToken).ConfigureAwait(false);
        return previous;
    }

    /// <summary>Switches the reader off on this storage system after a mismatch (the first reason is kept).</summary>
    public async Task TripAsync(Guid systemId, string reader, string reason, CancellationToken cancellationToken)
    {
        await UpdateAsync(systemId, reader, r => r.Tripped ? r : r with { TrippedUtc = time.GetUtcNow(), TripReason = reason }, cancellationToken).ConfigureAwait(false);
        logger.LogError("The {Reader} reader is switched off for storage system {SystemId}: {Reason}", reader, systemId, reason);
    }

    /// <summary>Turns the reader back on after a trip; verify mode starts again.</summary>
    public async Task<StorageReaderDto> ResetAsync(Guid systemId, string reader, CancellationToken cancellationToken)
    {
        var (state, options) = await UpdateAsync(systemId, reader, r => r with { TrippedUtc = null, TripReason = null, VerifiedRuns = 0 }, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("The {Reader} reader is turned back on for storage system {SystemId}; verify mode starts again", reader, systemId);
        return ToDto(state, options);
    }

    private async Task<(StorageReaderState State, StorageSystemOptions Options)> UpdateAsync(Guid systemId, string reader, Func<StorageReaderState, StorageReaderState> change, CancellationToken cancellationToken)
    {
        if (!All.Contains(reader))
        {
            throw new StorageSystemRequestException($"There is no reader \"{reader}\" (known: {string.Join(", ", All)}).");
        }

        for (var attempt = 1; ; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var system = await db.StorageSystems.FirstOrDefaultAsync(s => s.Id == systemId, cancellationToken).ConfigureAwait(false)
                ?? throw new StorageSystemRequestException($"There is no storage system {systemId}.", StatusCodes.Status404NotFound);
            var current = Find(system, reader);
            var updated = change(current);
            if (updated == current)
            {
                return (current, system.Options);
            }

            system.Readers = [.. (system.Readers ?? []).Where(r => r.Reader != reader), updated];
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return (updated, system.Options);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 5)
            {
                // Another VM of the run updated the system meanwhile; read it again.
            }
        }
    }
}
