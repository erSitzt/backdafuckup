using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Arrays;

namespace Backdafuckup.Server.Engine;

/// <summary>
/// When a VM of a job run may go on. A VM outside any group holds one of the job's <c>Max parallel VMs</c> slots for its
/// whole backup, as always. A VM of a planned group (VMs on the same array volumes) has two phases: its VM snapshot,
/// taken in its group's turn, and, once the group's array snapshot is taken and its VM snapshot is gone, the read, in one
/// of the job's slots. So all VMs of a volume share one array snapshot, however few VMs are read at once.
/// </summary>
internal abstract class VmPhases : IDisposable
{
    public virtual Guid? GroupId => null;

    public virtual int GroupSize => 0;

    /// <summary>Waits until the VM may start.</summary>
    public abstract Task EnterAsync(CancellationToken cancellationToken);

    /// <summary>Held while the VM snapshot is taken.</summary>
    public virtual Task<IDisposable> SnapshotSlotAsync(CancellationToken cancellationToken) => Task.FromResult<IDisposable>(Nothing.Instance);

    /// <summary>The VM snapshot exists.</summary>
    public virtual void SnapshotTaken()
    {
    }

    /// <summary>The VM goes to the array hook now (it joins its group's batch there, or drops out of it there).</summary>
    public virtual void AtHook()
    {
    }

    /// <summary>Waits until the VM may read its disks: from the array snapshot, or (<paramref name="fromArray"/> false) from its VM snapshot.</summary>
    public virtual Task ReadAsync(bool fromArray, IOperationLog log, CancellationToken cancellationToken) => Task.CompletedTask;

    public abstract void Dispose();

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>A VM outside any group: one slot of the job for its whole backup.</summary>
internal sealed class WholeVmPhases(SemaphoreSlim slots) : VmPhases
{
    private bool _held;

    public override async Task EnterAsync(CancellationToken cancellationToken)
    {
        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        _held = true;
    }

    public override void Dispose()
    {
        if (_held)
        {
            _held = false;
            slots.Release();
        }
    }
}

/// <summary>
/// A group of the run: its VM snapshots start once the previous group's are all taken (or dropped out), at most
/// <see cref="MaxSnapshotsAtOnce"/> at a time (XCP-ng takes one per storage anyway).
/// </summary>
internal sealed class GroupTurn(ArrayGroup group, GroupTurn? previous)
{
    /// <summary>VM snapshots of one group taken at the same time (for vCenter; XCP-ng's gate allows one per SR).</summary>
    public const int MaxSnapshotsAtOnce = 8;

    private readonly TaskCompletionSource _taken = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _passed;

    public ArrayGroup Group { get; } = group;

    public SemaphoreSlim Snapshots { get; } = new(Math.Clamp(group.Vms.Count, 1, MaxSnapshotsAtOnce));

    /// <summary>Done when every VM of the previous group has its VM snapshot (or dropped out).</summary>
    public Task PreviousTaken => previous?._taken.Task ?? Task.CompletedTask;

    /// <summary>One VM of the group is past its VM snapshot (taken, failed or skipped).</summary>
    public void Passed()
    {
        if (Interlocked.Increment(ref _passed) >= Group.Vms.Count)
        {
            _taken.TrySetResult();
        }
    }
}

/// <summary>A VM of a planned group: its snapshot in the group's turn, its read in one of the job's slots.</summary>
internal sealed class GroupedVmPhases(GroupTurn turn, SemaphoreSlim slots, ISnapshotLifecycleHook hook, Guid sessionId, int attempt) : VmPhases
{
    private bool _passed;
    private bool _atHook;
    private bool _reading;

    public override Guid? GroupId => turn.Group.Id;

    public override int GroupSize => turn.Group.Vms.Count;

    public override Task EnterAsync(CancellationToken cancellationToken) => turn.PreviousTaken.WaitAsync(cancellationToken);

    public override async Task<IDisposable> SnapshotSlotAsync(CancellationToken cancellationToken)
    {
        await turn.Snapshots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Slot(turn.Snapshots);
    }

    public override void SnapshotTaken() => Pass();

    public override void AtHook() => _atHook = true;

    public override async Task ReadAsync(bool fromArray, IOperationLog log, CancellationToken cancellationToken)
    {
        if (!fromArray)
        {
            // Its VM snapshot stays until its disks are read: it does not wait for a slot.
            log.Info("Its disks are read from the VM snapshot right away, without waiting for a slot of the job.");
            return;
        }

        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        _reading = true;
    }

    public override void Dispose()
    {
        if (_reading)
        {
            _reading = false;
            slots.Release();
        }

        Pass();
        if (!_atHook)
        {
            // It never reached its group's batch (its snapshot failed, it was skipped): the group waits for one VM less.
            _atHook = true;
            hook.Withdraw(sessionId, attempt, turn.Group.Id);
        }
    }

    private void Pass()
    {
        if (!_passed)
        {
            _passed = true;
            turn.Passed();
        }
    }

    private sealed class Slot(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
