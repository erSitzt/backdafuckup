using System.Collections.Concurrent;

namespace Backdafuckup.FileLevel;

/// <summary>
/// Threads for synchronous file system work. Parsers read synchronously; running them on the thread pool (or on
/// request threads) would block it and delay unrelated work such as agent long polls and VSS freezes.
/// </summary>
/// <remarks>
/// The threads get a large stack: parsers walk structures of an untrusted guest image, and a stack overflow ends
/// the process. Our own walkers are iterative; the large stack is a margin for recursion inside the parser library.
/// </remarks>
public sealed class FileLevelWorkers : IDisposable
{
    /// <summary>Stack size of every file-level thread.</summary>
    public const int StackSize = 16 * 1024 * 1024;

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread[] _threads;

    public FileLevelWorkers(int threads)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threads);
        _threads = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            _threads[i] = new Thread(Loop, StackSize) { IsBackground = true, Name = $"file-level-{i}" };
            _threads[i].Start();
        }
    }

    /// <summary>Runs short work (listing a folder, reading metadata) on one of the shared worker threads.</summary>
    public Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
        {
            completion.SetCanceled(cancellationToken);
            return completion.Task;
        }

        _queue.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    /// <summary>
    /// Runs long work (a download or a restore that streams many files) on its own thread, so it does not hold one of
    /// the shared workers for minutes.
    /// </summary>
    public static Task RunDedicated(Action work, string name)
    {
        ArgumentNullException.ThrowIfNull(work);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                try
                {
                    work();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            },
            StackSize)
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
        return completion.Task;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        foreach (var thread in _threads)
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }

        _queue.Dispose();
    }

    private void Loop()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                work();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
