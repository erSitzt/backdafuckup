using Backdafuckup.Tape.Devices;

namespace Backdafuckup.Tape.Format;

/// <summary>What is at the beginning of a tape: nothing, a backdafuckup label, or something else.</summary>
/// <param name="Reason">Why it counts as foreign.</param>
public sealed record TapeLabelRead(TapeLabel? Label, bool Blank, bool Foreign, string? Reason);

public static class TapeLabelReader
{
    /// <summary>Rewinds and reads file 0 (blocks up to 1 MiB). Leaves the tape after the label's filemark, or at its beginning when blank.</summary>
    public static async Task<TapeLabelRead> ReadAsync(TapeDrive drive, CancellationToken cancellationToken)
    {
        await drive.RewindAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[1024 * 1024];
        var first = await drive.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (first.Kind == ReadOutcomeKind.EndOfData)
        {
            await drive.RewindAsync(cancellationToken).ConfigureAwait(false);
            return new TapeLabelRead(null, true, false, null);
        }

        await drive.RewindAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await new TapeFileReader(new DriveBlocks(drive), buffer.Length).ReadAllAsync(cancellationToken).ToListAsync(cancellationToken).ConfigureAwait(false);
            var label = records.FirstOrDefault(r => r.Type == TapeFormat.Label) is { } record ? TapeJson.Deserialize<TapeLabel>(record.Body) : null;
            return label is { Format: TapeLabel.FormatName }
                ? new TapeLabelRead(label, false, false, null)
                : new TapeLabelRead(null, false, true, "its first file is no backdafuckup label");
        }
        catch (Exception ex) when (ex is InvalidDataException or TornFileException or System.Text.Json.JsonException or NotSupportedException)
        {
            return new TapeLabelRead(null, false, true, ex.Message);
        }
    }
}
