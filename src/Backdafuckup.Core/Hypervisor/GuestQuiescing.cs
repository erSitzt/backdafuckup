using Backdafuckup.Core.Operations;

namespace Backdafuckup.Core.Hypervisor;

/// <summary>
/// Makes the guest application-consistent for the moment of the VM snapshot (Windows: VSS through the backdafuckup
/// agent). The hypervisor provider calls <see cref="FreezeAsync"/> right before the snapshot and thaws right after it,
/// so that the freeze window contains nothing but the snapshot itself.
/// </summary>
public interface IGuestQuiescer
{
    /// <summary>
    /// Freezes the guest. Throws <see cref="GuestQuiesceException"/> if that is not possible; the provider then takes
    /// a crash-consistent snapshot and reports the reason.
    /// </summary>
    Task<IGuestFreeze> FreezeAsync(IOperationLog log, CancellationToken cancellationToken);
}

/// <summary>A frozen guest. Disposing without <see cref="ThawAsync"/> aborts the freeze.</summary>
public interface IGuestFreeze : IAsyncDisposable
{
    /// <summary>Releases the guest and reports whether the snapshot taken meanwhile is application-consistent.</summary>
    /// <param name="snapshotTaken">
    /// False if the snapshot failed: nothing may be recorded for it in the guest (e.g. no SQL Server backup history).
    /// </param>
    Task<GuestFreezeResult> ThawAsync(bool snapshotTaken, CancellationToken cancellationToken);
}

public sealed class GuestQuiesceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Outcome of the guest freeze around the VM snapshot.</summary>
/// <param name="ApplicationConsistent">True if every writer held its data consistent for the snapshot.</param>
/// <param name="Reason">Why the snapshot is only crash-consistent (null when application-consistent).</param>
/// <param name="FreezeTime">From the freeze request until writes were held.</param>
/// <param name="HeldTime">How long writes were held (contains the snapshot).</param>
/// <param name="Writers">The application writers that took part, with their final state.</param>
public sealed record GuestFreezeResult(
    bool ApplicationConsistent,
    string? Reason,
    TimeSpan FreezeTime,
    TimeSpan HeldTime,
    IReadOnlyList<GuestWriterResult> Writers)
{
    public static GuestFreezeResult NotConsistent(string reason) => new(false, reason, TimeSpan.Zero, TimeSpan.Zero, []);

    /// <summary>
    /// Set by a SQL Server snapshot (no VSS): the SQL Server databases were suspended for the snapshot, the rest of the
    /// guest is crash-consistent. <see cref="ApplicationConsistent"/> is then false.
    /// </summary>
    public SqlSnapshotOutcome? Sql { get; init; }
}

/// <param name="AllDatabasesConsistent">Every user database of the guest's SQL Server instances was recorded.</param>
/// <param name="Summary">For the session log: instances, recorded, skipped and failed databases, the write pause.</param>
/// <param name="Warnings">Warnings for the session log that do not change the consistency (e.g. about the transaction logs).</param>
public sealed record SqlSnapshotOutcome(bool AllDatabasesConsistent, string Summary, IReadOnlyList<string>? Warnings = null);

public sealed record GuestWriterResult(string Name, string State, string? Failure);
