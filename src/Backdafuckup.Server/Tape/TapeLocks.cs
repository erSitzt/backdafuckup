using System.Collections.Concurrent;
using Backdafuckup.Server.Engine;

namespace Backdafuckup.Server.Tape;

/// <summary>One lease per drive (whoever reads or writes a tape, or loads one) and one per library's robot.</summary>
public sealed class TapeLocks(TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, FifoLease> _drives = new();
    private readonly ConcurrentDictionary<Guid, FifoLease> _robots = new();

    public FifoLease Drive(Guid driveId) => _drives.GetOrAdd(driveId, _ => new FifoLease(time));

    public FifoLease Robot(Guid libraryId) => _robots.GetOrAdd(libraryId, _ => new FifoLease(time));

    public bool AnyHeld(IEnumerable<Guid> driveIds, Guid libraryId) =>
        driveIds.Any(d => _drives.TryGetValue(d, out var lease) && lease.Snapshot().Holder is not null) ||
        (_robots.TryGetValue(libraryId, out var robot) && robot.Snapshot().Holder is not null);
}
