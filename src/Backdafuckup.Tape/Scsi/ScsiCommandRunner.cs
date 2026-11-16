namespace Backdafuckup.Tape.Scsi;

/// <summary>
/// Runs commands on one device with the rules the product relies on:
/// <list type="bullet">
/// <item>Status commands are retried after UNIT ATTENTION (reset, medium changed) and BUSY; the attention is passed on
/// through <see cref="UnitAttention"/> so that callers read the state again.</item>
/// <item>Commands that move or change the medium (<see cref="ScsiCommand.MovesMedium"/>) are never retried: the caller
/// reads the position and decides.</item>
/// <item>The drive's own timeouts are used when known.</item>
/// </list>
/// </summary>
public sealed class ScsiCommandRunner(IScsiDevice device, string label, TimeProvider? time = null)
{
    private const int MaxAttentionRetries = 3;
    private const int MaxBusyRetries = 10;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private IReadOnlyDictionary<(byte OpCode, ushort ServiceAction), TimeSpan> _timeouts = new Dictionary<(byte, ushort), TimeSpan>();

    public IScsiDevice Device { get; } = device;

    /// <summary>How the device is named in messages, e.g. "drive 'Drive 1' (serial 4C1234ABCD, /dev/sg3)".</summary>
    public string Label { get; } = label;

    /// <summary>Raised for every UNIT ATTENTION (also when the command was retried).</summary>
    public event Action<SenseData>? UnitAttention;

    /// <summary>Uses the drive's recommended timeouts from now on (REPORT SUPPORTED OPERATION CODES with RCTD).</summary>
    public void UseTimeouts(IReadOnlyDictionary<(byte OpCode, ushort ServiceAction), TimeSpan> timeouts) => _timeouts = timeouts;

    /// <summary>Called before every command: a drive with posted commands in flight completes them first.</summary>
    public Func<CancellationToken, Task>? BeforeCommand { get; set; }

    /// <summary>Runs the command and returns its result, also a CHECK CONDITION (the caller interprets the sense).</summary>
    public async Task<ScsiResult> RunAsync(ScsiCommand command, CancellationToken cancellationToken)
    {
        if (BeforeCommand is { } before)
        {
            await before(cancellationToken).ConfigureAwait(false);
        }

        if (_timeouts.TryGetValue((command.OperationCode, ServiceAction(command)), out var timeout) && timeout > command.Timeout)
        {
            command = new ScsiCommand
            {
                Name = command.Name,
                Cdb = command.Cdb,
                Direction = command.Direction,
                Buffer = command.Buffer,
                Timeout = timeout,
                SensitiveData = command.SensitiveData,
                MovesMedium = command.MovesMedium,
            };
        }

        int attentions = 0, busy = 0;
        while (true)
        {
            ScsiResult result;
            try
            {
                result = await Device.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            }
            catch (ScsiTransportException ex)
            {
                throw new ScsiTransportException($"{command.Name} on {Label} did not complete: {ex.Message}", ex);
            }

            if (result.IsCheckCondition && result.Sense is { Key: SenseKey.UnitAttention } attention)
            {
                UnitAttention?.Invoke(attention);
                if (!command.MovesMedium && ++attentions <= MaxAttentionRetries)
                {
                    continue;
                }
            }

            if (result.Status is ScsiStatus.Busy or ScsiStatus.TaskSetFull && !command.MovesMedium && ++busy <= MaxBusyRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return result;
        }
    }

    /// <summary>Runs the command and throws unless it ends GOOD or <paramref name="accept"/> takes its sense.</summary>
    public async Task<ScsiResult> RunCheckedAsync(ScsiCommand command, CancellationToken cancellationToken, Func<SenseData, bool>? accept = null)
    {
        var result = await RunAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.IsGood || (result.IsCheckCondition && result.Sense is { } sense && (sense.Key is SenseKey.NoSense or SenseKey.RecoveredError || accept?.Invoke(sense) == true)))
        {
            return result;
        }

        throw new ScsiCommandException(Label, command, result);
    }

    /// <summary>Runs a command that reads data and returns the bytes received.</summary>
    public async Task<byte[]> ReadAsync(string name, byte[] cdb, int allocation, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var buffer = new byte[allocation];
        var command = new ScsiCommand { Name = name, Cdb = cdb, Direction = ScsiDataDirection.In, Buffer = buffer, Timeout = timeout ?? ScsiTimeouts.Status };
        var result = await RunCheckedAsync(command, cancellationToken).ConfigureAwait(false);
        return buffer[..result.Transferred(command)];
    }

    /// <summary>Runs a command without data or with data sent to the device.</summary>
    public Task<ScsiResult> SendAsync(string name, byte[] cdb, CancellationToken cancellationToken, ReadOnlyMemory<byte> data = default, TimeSpan? timeout = null, bool movesMedium = false, bool sensitive = false, Func<SenseData, bool>? accept = null) =>
        RunCheckedAsync(
            new ScsiCommand
            {
                Name = name,
                Cdb = cdb,
                Direction = data.IsEmpty ? ScsiDataDirection.None : ScsiDataDirection.Out,
                Buffer = System.Runtime.InteropServices.MemoryMarshal.AsMemory(data),
                Timeout = timeout ?? ScsiTimeouts.Status,
                MovesMedium = movesMedium,
                SensitiveData = sensitive,
            },
            cancellationToken,
            accept);

    private static ushort ServiceAction(ScsiCommand command) => command.OperationCode switch
    {
        Cdb.MaintenanceInCode or Cdb.ReadAttributeCode => (ushort)(command.Cdb[1] & 0x1F),
        Cdb.ReadPositionCode => (ushort)(command.Cdb[1] & 0x1F),
        _ => 0,
    };
}
