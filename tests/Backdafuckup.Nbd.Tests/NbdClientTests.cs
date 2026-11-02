using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using Backdafuckup.Nbd;
using Backdafuckup.Testing.Nbd;

namespace Backdafuckup.Nbd.Tests;

[Trait("Category", "Unit")]
public sealed class NbdClientTests
{
    private const int MiB = 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] RandomData(int size) => RandomNumberGenerator.GetBytes(size);

    private static NbdConnectOptions Options(InProcessNbdServer server, NbdTlsMode tls = NbdTlsMode.Disabled, RemoteCertificateValidationCallback? validation = null) => new()
    {
        Host = "127.0.0.1",
        Port = server.Port,
        ExportName = "export",
        TlsMode = tls,
        ServerCertificateValidation = validation,
        TlsTargetHost = "localhost",
        MaxInFlight = 16,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    private static RemoteCertificateValidationCallback PinTo(InProcessNbdServer server) =>
        (_, certificate, _, _) => certificate is not null && certificate.GetPublicKey().AsSpan().SequenceEqual(server.Certificate.GetPublicKey());

    [Fact]
    public async Task Negotiates_with_go_and_reads_ranges()
    {
        var data = RandomData(4 * MiB);
        await using var server = new InProcessNbdServer(data);
        await using var client = await NbdClient.ConnectAsync(Options(server), Ct);

        Assert.Equal(data.Length, client.Size);
        Assert.True(client.NegotiatedWithGo);
        Assert.True(client.IsReadOnly);
        Assert.False(client.UsesTls);

        var buffer = new byte[MiB + 17];
        await client.ReadAsync(123_456, buffer, Ct);
        AssertBytes(data.AsSpan(123_456, buffer.Length), buffer);
    }

    [Fact]
    public async Task Uses_tls_with_pinned_certificate()
    {
        var data = RandomData(MiB);
        await using var server = new InProcessNbdServer(data, new NbdTestServerOptions { RequireTls = true });
        await using var client = await NbdClient.ConnectAsync(Options(server, NbdTlsMode.Required, PinTo(server)), Ct);

        Assert.True(client.UsesTls);
        var buffer = new byte[4096];
        await client.ReadAsync(MiB - 4096, buffer, Ct);
        AssertBytes(data.AsSpan(MiB - 4096), buffer);
    }

    [Fact]
    public async Task Rejects_server_certificate_that_does_not_match_the_pin()
    {
        await using var server = new InProcessNbdServer(RandomData(MiB), new NbdTestServerOptions { RequireTls = true });

        await Assert.ThrowsAsync<AuthenticationException>(async () =>
            await NbdClient.ConnectAsync(Options(server, NbdTlsMode.Required, (_, _, _, _) => false), Ct));
    }

    [Fact]
    public async Task Reports_tls_required_when_connecting_without_tls()
    {
        await using var server = new InProcessNbdServer(RandomData(MiB), new NbdTestServerOptions { RequireTls = true });

        var ex = await Assert.ThrowsAsync<NbdOptionException>(async () => await NbdClient.ConnectAsync(Options(server), Ct));
        Assert.Equal(NbdProtocol.RepErrTlsReqd, ex.ReplyType);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Falls_back_to_export_name_when_go_is_unsupported(bool noZeroes)
    {
        var data = RandomData(MiB);
        await using var server = new InProcessNbdServer(data, new NbdTestServerOptions { SupportGo = false, AdvertiseNoZeroes = noZeroes });
        await using var client = await NbdClient.ConnectAsync(Options(server), Ct);

        Assert.False(client.NegotiatedWithGo);
        Assert.Equal(data.Length, client.Size);
        var buffer = new byte[1000];
        await client.ReadAsync(5000, buffer, Ct);
        AssertBytes(data.AsSpan(5000, 1000), buffer);
    }

    [Fact]
    public async Task Pipelined_reads_with_out_of_order_replies_land_in_the_right_buffers()
    {
        var data = RandomData(8 * MiB);
        await using var server = new InProcessNbdServer(data, new NbdTestServerOptions { MaxReplyDelayMs = 20 });
        await using var client = await NbdClient.ConnectAsync(Options(server), Ct);

        var reads = Enumerable.Range(0, 64).Select(async i =>
        {
            var offset = i * 128L * 1024;
            var buffer = new byte[128 * 1024];
            await client.ReadAsync(offset, buffer, Ct);
            AssertBytes(data.AsSpan((int)offset, buffer.Length), buffer);
        });
        await Task.WhenAll(reads);
    }

    [Fact]
    public async Task Splits_reads_larger_than_the_announced_maximum_block_size()
    {
        var data = RandomData(2 * MiB);
        await using var server = new InProcessNbdServer(data, new NbdTestServerOptions { MaxBlockSize = 64 * 1024 });
        await using var client = await NbdClient.ConnectAsync(Options(server), Ct);

        Assert.Equal(64 * 1024, client.MaxPayload);
        var buffer = new byte[MiB + 5];
        await client.ReadAsync(3, buffer, Ct);
        AssertBytes(data.AsSpan(3, buffer.Length), buffer);
    }

    [Fact]
    public async Task Server_errors_fail_only_the_affected_request()
    {
        var data = RandomData(MiB);
        await using var server = new InProcessNbdServer(data, new NbdTestServerOptions { FailReadAtOffset = 500_000 });
        await using var client = await NbdClient.ConnectAsync(Options(server), Ct);

        var ex = await Assert.ThrowsAsync<NbdCommandException>(async () => await client.ReadAsync(499_000, new byte[4096], Ct));
        Assert.Equal(5u, ex.Error);

        var buffer = new byte[4096];
        await client.ReadAsync(0, buffer, Ct);
        AssertBytes(data.AsSpan(0, 4096), buffer);
        Assert.False(client.IsFaulted);
    }

    [Fact]
    public async Task Cancelled_read_never_writes_into_the_callers_buffer()
    {
        var data = RandomData(MiB);
        await using var server = new InProcessNbdServer(data, new NbdTestServerOptions { MaxReplyDelayMs = 300 });
        await using var client = await NbdClient.ConnectAsync(Options(server), Ct);

        var buffer = new byte[64 * 1024];
        Array.Fill(buffer, (byte)0xAA);
        using var cts = new CancellationTokenSource();
        var read = client.ReadAsync(0, buffer, cts.Token).AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);

        // Wait until the delayed reply has certainly arrived and been drained.
        await Task.Delay(500, Ct);
        Assert.All(buffer, b => Assert.Equal(0xAA, b));

        var check = new byte[1024];
        await client.ReadAsync(1024, check, Ct);
        AssertBytes(data.AsSpan(1024, 1024), check);
    }

    [Fact]
    public async Task Writes_and_flushes_on_writable_exports()
    {
        var data = new byte[MiB];
        await using var server = new InProcessNbdServer(data, new NbdTestServerOptions { ReadOnly = false });
        await using var client = await NbdClient.ConnectAsync(Options(server), Ct);

        var payload = RandomData(100_000);
        await client.WriteAsync(4096, payload, Ct);
        await client.FlushAsync(Ct);

        var back = new byte[payload.Length];
        await client.ReadAsync(4096, back, Ct);
        Assert.Equal(payload, back);
        Assert.Equal(payload, server.Data.AsSpan(4096, payload.Length).ToArray());
    }

    [Fact]
    public async Task Pool_reconnects_after_connections_are_dropped()
    {
        var data = RandomData(4 * MiB);
        await using var server = new InProcessNbdServer(data);
        await using var pool = await NbdConnectionPool.CreateAsync(ct => NbdClient.ConnectAsync(Options(server), ct), 4, 4, Ct);

        Assert.Equal(4, server.ActiveConnections);
        await ReadAllAndCompareAsync(pool, data);

        server.DropAllConnections();
        await ReadAllAndCompareAsync(pool, data);
        Assert.True(server.TotalConnections > 4);
    }

    private static void AssertBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        var mismatch = expected.CommonPrefixLength(actual);
        Assert.True(mismatch == expected.Length, $"Bytes differ at index {mismatch}.");
    }

    private static async Task ReadAllAndCompareAsync(NbdConnectionPool pool, byte[] data)
    {
        const int chunk = 256 * 1024;
        var tasks = Enumerable.Range(0, data.Length / chunk).Select(async i =>
        {
            var buffer = new byte[chunk];
            await pool.ReadAsync(i * (long)chunk, buffer, Ct);
            AssertBytes(data.AsSpan(i * chunk, chunk), buffer);
        });
        await Task.WhenAll(tasks);
    }
}
