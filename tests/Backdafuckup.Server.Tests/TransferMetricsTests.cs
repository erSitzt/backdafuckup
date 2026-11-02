using Backdafuckup.Contracts;

namespace Backdafuckup.Server.Tests;

[Trait("Category", "Unit")]
public sealed class TransferMetricsTests
{
    [Fact]
    public void Ratios_rates_and_bottleneck_are_derived_from_the_counters()
    {
        var m = new TransferMetricsDto
        {
            ProcessedBytes = 1000,
            ReadBytes = 400,
            DedupedBytes = 300,
            NewDataBytes = 100,
            StoredBytes = 40,
            Seconds = 10,
            SourceLoad = 0.9,
            ProcessingLoad = 0.2,
            TargetLoad = 0.1,
        };

        Assert.Equal(100, m.ProcessingRate);
        Assert.Equal(40, m.ReadRate);
        Assert.Equal(4, m.DedupeRatio);
        Assert.Equal(2.5, m.CompressionRatio);
        Assert.Equal("Source", m.Bottleneck);
        Assert.Equal(double.PositiveInfinity, (m with { NewDataBytes = 0, StoredBytes = 0 }).DedupeRatio);
        Assert.Null(new TransferMetricsDto().DedupeRatio);
        Assert.Null(new TransferMetricsDto().CompressionRatio);
        Assert.Null(new TransferMetricsDto().Bottleneck);
        Assert.Equal("Target", (m with { SourceLoad = 0.1, TargetLoad = 0.5 }).Bottleneck);
    }

    [Fact]
    public void Combine_sums_bytes_and_weights_loads_by_transfer_time()
    {
        var a = new TransferMetricsDto { TotalBytes = 100, ProcessedBytes = 100, NewDataBytes = 50, StoredBytes = 25, Seconds = 30, SourceLoad = 1.0, CurrentBytesPerSecond = 5 };
        var b = new TransferMetricsDto { TotalBytes = 300, ProcessedBytes = 200, NewDataBytes = 50, StoredBytes = 25, Seconds = 10, SourceLoad = 0.2, TargetLoad = 0.8, CurrentBytesPerSecond = 7 };

        var combined = TransferMetricsDto.Combine([a, null, b], seconds: 20);

        Assert.Equal(400, combined.TotalBytes);
        Assert.Equal(300, combined.ProcessedBytes);
        Assert.Equal(15, combined.ProcessingRate);
        Assert.Equal(2, combined.CompressionRatio);
        Assert.Equal(12, combined.CurrentBytesPerSecond);
        Assert.Equal(0.8, combined.SourceLoad, 3);
        Assert.Equal(0.2, combined.TargetLoad, 3);
        Assert.Equal("Source", combined.Bottleneck);
    }
}
