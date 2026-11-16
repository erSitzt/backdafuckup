namespace Backdafuckup.Tape.Scsi;

public enum ScsiDataDirection
{
    None,

    /// <summary>From the device to the host (reads).</summary>
    In,

    /// <summary>From the host to the device (writes).</summary>
    Out,
}

/// <summary>One SCSI command: its CDB, its data buffer and how long it may take.</summary>
public sealed class ScsiCommand
{
    /// <summary>The command's name for messages, e.g. "READ POSITION".</summary>
    public required string Name { get; init; }

    public required byte[] Cdb { get; init; }

    public ScsiDataDirection Direction { get; init; }

    /// <summary>For <see cref="ScsiDataDirection.In"/> the destination, for <see cref="ScsiDataDirection.Out"/> the data sent.</summary>
    public Memory<byte> Buffer { get; init; }

    public TimeSpan Timeout { get; init; } = ScsiTimeouts.Status;

    /// <summary>The data must never be logged or traced (encryption keys, attributes).</summary>
    public bool SensitiveData { get; init; }

    /// <summary>Moves the medium or changes what is on it: never retried blindly.</summary>
    public bool MovesMedium { get; init; }

    public byte OperationCode => Cdb[0];

    public override string ToString() => $"{Name} ({Convert.ToHexString(Cdb)})";
}

/// <summary>Default timeouts, used when the drive does not report its own (REPORT SUPPORTED OPERATION CODES with RCTD).</summary>
public static class ScsiTimeouts
{
    public static readonly TimeSpan Status = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ReadWrite = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan Move = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Positioning = TimeSpan.FromHours(3);
}

/// <summary>SCSI status codes (SAM).</summary>
public static class ScsiStatus
{
    public const byte Good = 0x00;
    public const byte CheckCondition = 0x02;
    public const byte ConditionMet = 0x04;
    public const byte Busy = 0x08;
    public const byte ReservationConflict = 0x18;
    public const byte TaskSetFull = 0x28;
    public const byte TaskAborted = 0x40;

    public static string Describe(byte status) => status switch
    {
        Good => "GOOD",
        CheckCondition => "CHECK CONDITION",
        ConditionMet => "CONDITION MET",
        Busy => "BUSY",
        ReservationConflict => "RESERVATION CONFLICT",
        TaskSetFull => "TASK SET FULL",
        TaskAborted => "TASK ABORTED",
        _ => $"status 0x{status:X2}",
    };
}

/// <param name="Status">The SCSI status.</param>
/// <param name="Sense">Sense data of a CHECK CONDITION.</param>
/// <param name="Residual">Bytes of the buffer not transferred.</param>
public readonly record struct ScsiResult(byte Status, SenseData? Sense, int Residual)
{
    public bool IsGood => Status == ScsiStatus.Good;

    public bool IsCheckCondition => Status == ScsiStatus.CheckCondition;

    /// <summary>Bytes transferred for <paramref name="command"/>.</summary>
    public int Transferred(ScsiCommand command) => Math.Max(0, command.Buffer.Length - Residual);
}

/// <summary>
/// A SCSI device: a tape drive or a medium changer, reached through the operating system or a tape agent. One command
/// at a time per device.
/// </summary>
public interface IScsiDevice : IAsyncDisposable
{
    /// <summary>Where the device is, for messages: "/dev/sg3", "\\.\Tape0", "agent backup-01: /dev/sg3".</summary>
    string Path { get; }

    /// <summary>Largest data transfer of one command the path to the device allows.</summary>
    int MaxTransferLength { get; }

    /// <summary>Runs one command. A CHECK CONDITION is a result, not an exception; transport failures throw <see cref="ScsiTransportException"/>.</summary>
    ValueTask<ScsiResult> ExecuteAsync(ScsiCommand command, CancellationToken cancellationToken);
}

/// <summary>The command did not reach the device or no status came back (adapter, path, timeout, agent connection).</summary>
public sealed class ScsiTransportException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>A command ended with a status or sense the caller did not expect.</summary>
public sealed class ScsiCommandException(string device, ScsiCommand command, ScsiResult result)
    : IOException($"{command.Name} on {device} failed: {Describe(result)}")
{
    public string Device { get; } = device;

    public string CommandName { get; } = command.Name;

    public ScsiResult Result { get; } = result;

    public SenseData? Sense => Result.Sense;

    private static string Describe(ScsiResult result) =>
        result.Sense is { } sense ? sense.ToString() : ScsiStatus.Describe(result.Status);
}
