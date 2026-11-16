using System.Security.Cryptography;
using Backdafuckup.Tape.Agent;
using Backdafuckup.Tape.Devices;
using Backdafuckup.Tape.Scsi;
using Backdafuckup.Testing.Tape;

namespace Backdafuckup.Tape.Tests;

/// <summary>
/// The block pump: through a tape agent (the real client and connection, over WebSockets in memory) writes are posted
/// and reads go ahead, in chains that stop at early warning, a filemark, the end of data or a full tape.
/// </summary>
public sealed class TapeAgentPipelineTests
{
    private const int Block = 64 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Posted_writes_and_reads_ahead_keep_the_blocks_in_order_and_stop_at_filemarks()
    {
        await using var agent = await AgentDriveAsync(64L << 20);
        var drive = agent.Drive;
        var files = new List<List<byte[]>>();
        for (var file = 0; file < 3; file++)
        {
            var blocks = Enumerable.Range(0, 40 + file).Select(_ => RandomNumberGenerator.GetBytes(Block)).ToList();
            foreach (var block in blocks)
            {
                Assert.False((await drive.WriteBlockAsync(block, Ct)).EarlyWarning);
            }

            await drive.WriteFilemarksAsync(1, immediate: false, Ct);
            files.Add(blocks);
        }

        Assert.True(drive.PostedCommands > 100, $"{drive.PostedCommands} posted");
        Assert.Equal(3 * 41 + 3, (await drive.ReadPositionAsync(Ct)).Block);

        // Read ahead file by file: each stops at its filemark, the next starts right after it.
        await drive.RewindAsync(Ct);
        var buffer = new byte[Block];
        foreach (var blocks in files)
        {
            foreach (var expected in blocks)
            {
                var read = await drive.ReadBlockAsync(buffer, Ct, readAhead: true);
                Assert.Equal((ReadOutcomeKind.Block, Block), (read.Kind, read.Length));
                Assert.Equal(expected, buffer);
            }

            Assert.Equal(ReadOutcomeKind.Filemark, (await drive.ReadBlockAsync(buffer, Ct, readAhead: true)).Kind);
        }

        Assert.Equal(ReadOutcomeKind.EndOfData, (await drive.ReadBlockAsync(buffer, Ct, readAhead: true)).Kind);

        // Reads ahead not taken are dropped before the next command; a locate puts the tape where it is wanted
        // (file 1 begins at block 41, after file 0's 40 blocks and its filemark).
        await drive.LocateBlockAsync(41, Ct);
        await drive.ReadBlockAsync(buffer, Ct, readAhead: true);
        Assert.Equal(files[1][0], buffer);
        await drive.LocateBlockAsync(42, Ct);
        Assert.Equal(42, (await drive.ReadPositionAsync(Ct)).Block);
        await drive.ReadBlockAsync(buffer, Ct);
        Assert.Equal(files[1][1], buffer);
    }

    [Fact]
    public async Task Early_warning_in_the_middle_of_posted_writes_loses_and_repeats_no_block()
    {
        await using var agent = await AgentDriveAsync(16L << 20, earlyWarningZone: 4L << 20);
        var drive = agent.Drive;
        var written = new List<byte[]>();
        var warned = 0;
        while (warned < 4)
        {
            var block = RandomNumberGenerator.GetBytes(Block);
            var outcome = await drive.WriteBlockAsync(block, Ct);
            written.Add(block);
            warned += outcome.EarlyWarning ? 1 : 0;
        }

        Assert.True((await drive.WriteFilemarksAsync(1, immediate: false, Ct)).EarlyWarning);
        await AssertOnTapeAsync(drive, written);
    }

    [Fact]
    public async Task A_full_tape_without_early_warning_keeps_every_block_before_it_in_order()
    {
        await using var agent = await AgentDriveAsync(8L << 20, holo: true);
        var drive = agent.Drive;
        var written = new List<byte[]>();
        await Assert.ThrowsAsync<TapeFullException>(async () =>
        {
            while (true)
            {
                var block = RandomNumberGenerator.GetBytes(Block);
                await drive.WriteBlockAsync(block, Ct);
                written.Add(block);
            }
        });

        // The blocks on tape are the first ones written, without a gap; those after the full tape are not on it.
        var onTape = agent.Cartridge.Records.Count(r => r.Data is not null);
        Assert.InRange(onTape, 100, written.Count);
        await drive.RewindAsync(Ct);
        var buffer = new byte[Block];
        for (var i = 0; i < onTape; i++)
        {
            await drive.ReadBlockAsync(buffer, Ct, readAhead: true);
            Assert.Equal(written[i], buffer);
        }
    }

    [Fact]
    public async Task An_agent_of_the_old_protocol_gets_every_command_on_its_own()
    {
        await using var agent = await AgentDriveAsync(64L << 20, protocol: 1);
        var blocks = Enumerable.Range(0, 20).Select(_ => RandomNumberGenerator.GetBytes(Block)).ToList();
        foreach (var block in blocks)
        {
            await agent.Drive.WriteBlockAsync(block, Ct);
        }

        await agent.Drive.WriteFilemarksAsync(1, immediate: false, Ct);
        Assert.Equal(0, agent.Drive.PostedCommands);
        await AssertOnTapeAsync(agent.Drive, blocks);
    }

    private static async Task AssertOnTapeAsync(TapeDrive drive, List<byte[]> written)
    {
        await drive.RewindAsync(Ct);
        var buffer = new byte[Block];
        foreach (var expected in written)
        {
            var read = await drive.ReadBlockAsync(buffer, Ct, readAhead: true);
            Assert.Equal(ReadOutcomeKind.Block, read.Kind);
            Assert.Equal(expected, buffer);
        }

        Assert.Equal(ReadOutcomeKind.Filemark, (await drive.ReadBlockAsync(buffer, Ct, readAhead: true)).Kind);
    }

    /// <summary>A drive of a fake library on an agent's host, opened by the server through the agent.</summary>
    private static async Task<AgentDrive> AgentDriveAsync(long capacity, long? earlyWarningZone = null, bool holo = false, int protocol = TapeAgentProtocol.Version)
    {
        var library = new FakeTapeLibrary { HoloQuirks = holo };
        var cartridge = library.AddCartridge("BDF000L9", capacity);
        cartridge.EarlyWarningZone = earlyWarningZone ?? cartridge.EarlyWarningZone;
        library.Drives[0].OperatorInsert(cartridge);
        var (server, client) = InMemoryWebSockets.Create();
        var stop = new CancellationTokenSource();
        var agentClient = new TapeAgentClient(new FakeTapeHost(library), _ => Task.FromResult(client), "the test server", "test", _ => { });
        var session = agentClient.SessionAsync(client, stop.Token);
        var hello = (await TapeAgentProtocol.ReceiveAsync(server, Ct))!.Value.Header;
        var connection = new TapeAgentConnection(server, "tape agent test", hello with { Protocol = protocol });
        var reading = connection.RunAsync(stop.Token);
        var device = await connection.OpenAsync(library.Drives[0].Serial, Ct);
        var drive = new TapeDrive(new ScsiCommandRunner(device, "drive 1 on the agent")) { PipelineBytes = 2 * 1024 * 1024, PollInterval = TimeSpan.FromMilliseconds(10) };
        await drive.WaitReadyAsync(TimeSpan.FromMinutes(1), Ct);
        await drive.ReadMediumAsync(Ct);
        await drive.PrepareAsync(Ct);
        return new AgentDrive(drive, device, cartridge, stop, session, reading);
    }

    private sealed record AgentDrive(TapeDrive Drive, IScsiDevice Device, FakeCartridge Cartridge, CancellationTokenSource Stop, Task Session, Task Reading) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Device.DisposeAsync();
            await Stop.CancelAsync();
            await Task.WhenAny(Task.WhenAll(Session, Reading), Task.Delay(TimeSpan.FromSeconds(5)));
            Stop.Dispose();
        }
    }
}
