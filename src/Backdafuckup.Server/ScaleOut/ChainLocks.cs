using System.Collections.Concurrent;
using Backdafuckup.Server.Engine;

namespace Backdafuckup.Server.ScaleOut;

/// <summary>
/// One lease per chain (a job's restore points of one object). An image backup holds it while it places and writes a
/// restore point; a move holds it for its final pass and the switch, so a backup never writes to an extent that the
/// chain just left. SQL Server log backups never take it.
/// </summary>
public sealed class ChainLocks(TimeProvider time)
{
    private readonly ConcurrentDictionary<(Guid JobId, string ObjectNativeId), FifoLease> _leases = new();

    public FifoLease For(Guid jobId, string objectNativeId) => _leases.GetOrAdd((jobId, objectNativeId), _ => new FifoLease(time));

    /// <summary>Who holds the chain's lease now, if anyone (for the console and the planner).</summary>
    public string? Holder(Guid jobId, string objectNativeId) =>
        _leases.TryGetValue((jobId, objectNativeId), out var lease) ? lease.Snapshot().Holder : null;
}
