using System.Globalization;
using Backdafuckup.Contracts;

namespace Backdafuckup.Server.Engine;

/// <summary>Current transfer rate from successive byte counter samples (at least one second apart).</summary>
public sealed class RateMeter
{
    private readonly Lock _lock = new();
    private DateTimeOffset? _at;
    private long _bytes;

    public double Current { get; private set; }

    public void Sample(DateTimeOffset now, long bytes)
    {
        lock (_lock)
        {
            if (_at is { } at)
            {
                var seconds = (now - at).TotalSeconds;
                if (seconds < 1)
                {
                    return;
                }

                Current = Math.Max(0, (bytes - _bytes) / seconds);
            }

            _at = now;
            _bytes = bytes;
        }
    }
}

/// <summary>Formats transfer statistics for session log lines.</summary>
public static class MetricsText
{
    public static string Describe(TransferMetricsDto m)
    {
        var parts = new List<string>
        {
            $"processed {VmBackupTask.Format(m.ProcessedBytes)} of {VmBackupTask.Format(m.TotalBytes)} in {Duration(m.Seconds)} ({Rate(m.ProcessingRate)})",
            $"read {VmBackupTask.Format(m.ReadBytes)} ({Rate(m.ReadRate)})",
            $"unchanged {VmBackupTask.Format(m.UnchangedBytes)}, empty {VmBackupTask.Format(m.ZeroBytes)}, already stored {VmBackupTask.Format(m.DedupedBytes)}, new {VmBackupTask.Format(m.NewDataBytes)}",
            $"stored {VmBackupTask.Format(m.StoredBytes)}",
            $"dedupe {Ratio(m.DedupeRatio)}, compression {Ratio(m.CompressionRatio)}",
        };
        if (m.Bottleneck is { } bottleneck)
        {
            parts.Add($"bottleneck {bottleneck} (source {Percent(m.SourceLoad)}, processing {Percent(m.ProcessingLoad)}, target {Percent(m.TargetLoad)})");
        }

        return string.Join(" · ", parts);
    }

    public static string Rate(double bytesPerSecond) => VmBackupTask.Format((long)bytesPerSecond) + "/s";

    public static string Ratio(double? ratio) => ratio switch
    {
        null => "–",
        double.PositiveInfinity => "∞ (all data already stored)",
        { } r => r.ToString("0.0", CultureInfo.InvariantCulture) + "x",
    };

    public static string Percent(double load) => (load * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    public static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes:00}:{span.Seconds:00}";
    }
}
