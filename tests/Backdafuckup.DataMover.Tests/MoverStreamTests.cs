using Backdafuckup.Core.DataMover;

namespace Backdafuckup.DataMover.Tests;

public sealed class MoverStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Records_and_heartbeats_round_trip_and_the_end_carries_the_total()
    {
        var stream = new MemoryStream();
        await MoverStream.WriteHeaderAsync(stream, 10_000, Ct);
        await MoverStream.WriteRecordAsync(stream, 100, new byte[] { 1, 2, 3 }, Ct);
        await MoverStream.WriteHeartbeatAsync(stream, Ct);
        await MoverStream.WriteRecordAsync(stream, 9_998, new byte[] { 4, 5 }, Ct);
        await MoverStream.WriteEndAsync(stream, 5, Ct);
        stream.Position = 0;

        var records = new List<string>();
        Assert.Equal(10_000, await MoverStream.ReadHeaderAsync(stream, Ct));
        Assert.Equal(5, await MoverStream.ReadRecordsAsync(stream, 10_000, (offset, data, _) =>
        {
            records.Add($"{offset}:{Convert.ToHexString(data.Span)}");
            return ValueTask.CompletedTask;
        }, Ct));
        Assert.Equal(["100:010203", "9998:0405"], records);
    }

    [Fact]
    public async Task A_stream_without_its_end_out_of_order_or_past_the_capacity_is_refused()
    {
        async Task<InvalidDataException> Refused(Func<Stream, Task> write)
        {
            var stream = new MemoryStream();
            await MoverStream.WriteHeaderAsync(stream, 1000, Ct);
            await write(stream);
            stream.Position = 0;
            var capacity = await MoverStream.ReadHeaderAsync(stream, Ct);
            return await Assert.ThrowsAsync<InvalidDataException>(() => MoverStream.ReadRecordsAsync(stream, capacity, (_, _, _) => ValueTask.CompletedTask, Ct));
        }

        Assert.Contains("ended without its end after 3 bytes", (await Refused(s => MoverStream.WriteRecordAsync(s, 0, new byte[3], Ct).AsTask())).Message, StringComparison.Ordinal);
        Assert.Contains("record at 0 of 3 bytes after 10", (await Refused(async s =>
        {
            await MoverStream.WriteRecordAsync(s, 5, new byte[5], Ct);
            await MoverStream.WriteRecordAsync(s, 0, new byte[3], Ct);
        })).Message, StringComparison.Ordinal);
        Assert.Contains("record at 999 of 2 bytes", (await Refused(s => MoverStream.WriteRecordAsync(s, 999, new byte[2], Ct).AsTask())).Message, StringComparison.Ordinal);
        Assert.Contains("announces 7 bytes of data at its end, but carried 3", (await Refused(async s =>
        {
            await MoverStream.WriteRecordAsync(s, 0, new byte[3], Ct);
            await MoverStream.WriteEndAsync(s, 7, Ct);
        })).Message, StringComparison.Ordinal);

        var foreign = new MemoryStream("GET / HTTP/1.1\r\n\r\n"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => MoverStream.ReadHeaderAsync(foreign, Ct).AsTask());
    }
}
