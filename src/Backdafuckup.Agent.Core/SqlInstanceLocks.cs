using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// One backup at a time per SQL Server instance in this agent: log and full backups and the snapshot suspend exclude
/// each other. While a database is suspended for the snapshot, SQL Server makes BACKUP wait; while a native full runs,
/// the suspend fails at once with error 3081 (spec 2026-11-05-1932, spike 2d).
/// </summary>
public sealed class SqlInstanceLocks
{
    private readonly ConcurrentDictionary<string, Holder> _locks = new ConcurrentDictionary<string, Holder>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Takes the instance's lock, waiting up to <paramref name="wait"/>; null if another backup still holds it.</summary>
    public IDisposable? TryAcquire(string instance, TimeSpan wait, string owner, CancellationToken cancellationToken = default)
    {
        var holder = _locks.GetOrAdd(instance, _ => new Holder());
        if (!holder.Gate.Wait(wait, cancellationToken))
        {
            return null;
        }

        holder.Owner = owner;
        return new Releaser(holder);
    }

    /// <summary>What holds the instance's lock now (for log lines), or null.</summary>
    public string? Owner(string instance) => _locks.TryGetValue(instance, out var holder) && holder.Gate.CurrentCount == 0 ? holder.Owner : null;

    private sealed class Holder
    {
        public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);

        public string? Owner { get; set; }
    }

    private sealed class Releaser : IDisposable
    {
        private Holder? _holder;

        public Releaser(Holder holder) => _holder = holder;

        public void Dispose()
        {
            var holder = Interlocked.Exchange(ref _holder, null);
            if (holder is not null)
            {
                holder.Owner = null;
                holder.Gate.Release();
            }
        }
    }
}
