namespace Backdafuckup.Server.Engine;

/// <summary>
/// A lock that is granted in the order it was asked for, and says who holds it and who waits (for the console). It lives
/// in memory only: after a restart nothing holds it (a drive, a chain).
/// </summary>
public sealed class FifoLease(TimeProvider time)
{
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _waiting = [];
    private string? _holder;
    private DateTimeOffset _since;

    public (string? Holder, DateTimeOffset? Since, IReadOnlyList<string> Waiting) Snapshot()
    {
        lock (_gate)
        {
            return (_holder, _holder is null ? null : _since, _waiting.Select(w => w.Holder).ToList());
        }
    }

    /// <summary>Takes the lock now if it is free.</summary>
    public IAsyncDisposable? TryAcquire(string holder)
    {
        lock (_gate)
        {
            if (_holder is not null)
            {
                return null;
            }

            _holder = holder;
            _since = time.GetUtcNow();
            return new Lease(this);
        }
    }

    /// <summary>Waits for the lock; <paramref name="waiting"/> is told the current holder once, when it has to wait.</summary>
    public async Task<IAsyncDisposable> AcquireAsync(string holder, Action<string>? waiting, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LinkedListNode<Waiter> node;
        string current;
        lock (_gate)
        {
            if (_holder is null)
            {
                _holder = holder;
                _since = time.GetUtcNow();
                return new Lease(this);
            }

            node = _waiting.AddLast(new Waiter(holder, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)));
            current = _holder;
        }

        waiting?.Invoke(current);
        using (cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                if (node.List is not null)
                {
                    _waiting.Remove(node);
                    node.Value.Granted.TrySetCanceled(cancellationToken);
                }
            }
        }))
        {
            await node.Value.Granted.Task.ConfigureAwait(false);
        }

        return new Lease(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_waiting.First is { } next)
            {
                _waiting.RemoveFirst();
                _holder = next.Value.Holder;
                _since = time.GetUtcNow();
                next.Value.Granted.TrySetResult();
            }
            else
            {
                _holder = null;
            }
        }
    }

    private sealed record Waiter(string Holder, TaskCompletionSource Granted);

    private sealed class Lease(FifoLease owner) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
