using System.Collections.Concurrent;
using System.Diagnostics;

namespace Backdafuckup.Xen;

/// <summary>
/// One VM snapshot at a time per storage repository (see <see cref="XenOptions.OneSnapshotPerStorage"/>). A VM with
/// disks on several storages takes their gates in a fixed order, so two such VMs never wait for each other in a circle.
/// </summary>
internal sealed class XenSnapshotGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>Waits for the gates of <paramref name="storageUuids"/>; dispose the result right after the snapshot.</summary>
    public async Task<Lease> EnterAsync(IEnumerable<string> storageUuids, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var taken = new List<SemaphoreSlim>();
        var waited = false;
        try
        {
            foreach (var uuid in storageUuids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var gate = _gates.GetOrAdd(uuid, _ => new SemaphoreSlim(1, 1));
                if (!gate.Wait(0, CancellationToken.None))
                {
                    waited = true;
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                taken.Add(gate);
            }
        }
        catch
        {
            Lease.Release(taken);
            throw;
        }

        return new Lease(taken, waited ? watch.Elapsed : null);
    }

    internal sealed class Lease(List<SemaphoreSlim> gates, TimeSpan? waited) : IDisposable
    {
        private List<SemaphoreSlim>? _gates = gates;

        /// <summary>How long the snapshot waited for snapshots of other VMs on the same storage; null: it did not wait.</summary>
        public TimeSpan? Waited { get; } = waited;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _gates, null) is { } gates)
            {
                Release(gates);
            }
        }

        internal static void Release(List<SemaphoreSlim> gates)
        {
            for (var i = gates.Count - 1; i >= 0; i--)
            {
                gates[i].Release();
            }
        }
    }
}
