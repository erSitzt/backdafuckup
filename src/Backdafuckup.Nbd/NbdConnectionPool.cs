namespace Backdafuckup.Nbd;

/// <summary>
/// Several NBD connections to the same export. Reads go to the least-loaded healthy connection; broken
/// connections are re-established and the read is retried (reads are idempotent).
/// </summary>
public sealed class NbdConnectionPool : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<NbdClient>> _connect;
    private readonly Slot[] _slots;
    private readonly int _maxRetries;
    private int _disposed;
    private int _reconnects;

    private NbdConnectionPool(Func<CancellationToken, Task<NbdClient>> connect, NbdClient first, int connections, int maxRetries)
    {
        _connect = connect;
        _maxRetries = maxRetries;
        Size = first.Size;
        MaxPayload = first.MaxPayload;
        UsesTls = first.UsesTls;
        TlsProtocol = first.TlsProtocol;
        NegotiatedWithGo = first.NegotiatedWithGo;
        MaxInFlightPerConnection = 1;
        _slots = new Slot[connections];
        _slots[0] = new Slot(first);
        for (var i = 1; i < connections; i++)
        {
            _slots[i] = new Slot(null);
        }
    }

    public long Size { get; }

    public int MaxPayload { get; }

    public bool UsesTls { get; }

    public System.Security.Authentication.SslProtocols TlsProtocol { get; }

    /// <summary>True if the server supported NBD_OPT_GO (otherwise NBD_OPT_EXPORT_NAME was used).</summary>
    public bool NegotiatedWithGo { get; }

    /// <summary>Connections that are currently established.</summary>
    public int OpenConnections => _slots.Count(s => s.Client is { IsFaulted: false });

    /// <summary>Connections that broke and were opened again since the pool was created.</summary>
    public int Reconnects => Volatile.Read(ref _reconnects);

    public int Connections => _slots.Length;

    public int MaxInFlightPerConnection { get; private set; }

    /// <summary>Opens the first connection immediately and the others in parallel; extra connections that fail are retried later.</summary>
    public static async Task<NbdConnectionPool> CreateAsync(
        Func<CancellationToken, Task<NbdClient>> connect,
        int connections,
        int maxInFlightPerConnection,
        CancellationToken cancellationToken,
        int maxRetries = 3)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(connections);
        var first = await connect(cancellationToken).ConfigureAwait(false);
        var pool = new NbdConnectionPool(connect, first, connections, maxRetries)
        {
            MaxInFlightPerConnection = Math.Max(1, maxInFlightPerConnection),
        };

        // NBD_FLAG_CAN_MULTI_CONN only matters for write consistency; read-only snapshot exports are safe to
        // read over several connections.
        var others = Enumerable.Range(1, connections - 1).Select(i => pool.TryEnsureAsync(pool._slots[i], cancellationToken));
        await Task.WhenAll(others).ConfigureAwait(false);
        return pool;
    }

    public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        for (var attempt = 0; ; attempt++)
        {
            var client = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await client.ReadAsync(offset, destination, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is (NbdException and not NbdCommandException) or IOException or ObjectDisposedException
                                       && attempt < _maxRetries && !cancellationToken.IsCancellationRequested)
            {
                // Connection-level failure: the slot reconnects on the next acquire.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var slot in _slots)
        {
            if (slot.Client is { } client)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            slot.Lock.Dispose();
        }
    }

    private async ValueTask<NbdClient> AcquireAsync(CancellationToken cancellationToken)
    {
        NbdClient? best = null;
        foreach (var slot in _slots)
        {
            var client = slot.Client;
            if (client is { IsFaulted: false } && (best is null || client.InFlight < best.InFlight))
            {
                best = client;
            }
        }

        // Prefer repairing a broken slot over piling onto a busy connection.
        if (best is null || best.InFlight >= MaxInFlightPerConnection)
        {
            foreach (var slot in _slots)
            {
                if (slot.Client is null or { IsFaulted: true })
                {
                    var repaired = await TryEnsureAsync(slot, cancellationToken).ConfigureAwait(false);
                    if (repaired is not null)
                    {
                        return repaired;
                    }

                    break;
                }
            }
        }

        if (best is not null)
        {
            return best;
        }

        // Every connection is broken: reconnect one, letting the error surface.
        return await EnsureAsync(_slots[0], cancellationToken).ConfigureAwait(false);
    }

    private async Task<NbdClient?> TryEnsureAsync(Slot slot, CancellationToken cancellationToken)
    {
        try
        {
            return await EnsureAsync(slot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NbdException or IOException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return null;
        }
    }

    private async Task<NbdClient> EnsureAsync(Slot slot, CancellationToken cancellationToken)
    {
        await slot.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (slot.Client is { IsFaulted: false } healthy)
            {
                return healthy;
            }

            var replacing = slot.Client is not null;
            if (slot.Client is { } broken)
            {
                slot.Client = null;
                await broken.DisposeAsync().ConfigureAwait(false);
            }

            var client = await _connect(cancellationToken).ConfigureAwait(false);
            if (client.Size != Size)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw new NbdException($"NBD export size changed on reconnect ({client.Size} != {Size}).");
            }

            slot.Client = client;
            if (replacing)
            {
                Interlocked.Increment(ref _reconnects);
            }

            return client;
        }
        finally
        {
            slot.Lock.Release();
        }
    }

    private sealed class Slot(NbdClient? client)
    {
        public NbdClient? Client { get; set; } = client;

        public SemaphoreSlim Lock { get; } = new(1, 1);
    }
}
