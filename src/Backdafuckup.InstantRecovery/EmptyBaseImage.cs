namespace Backdafuckup.InstantRecovery;

/// <summary>The base of a disk that was left out of the backup: it reads as zeros until the VM writes it.</summary>
public sealed class EmptyBaseImage(long size) : IBaseImage
{
    public long Size => size;

    public long AllocatedBytes => 0;

    public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        destination.Span.Clear();
        return ValueTask.CompletedTask;
    }
}
