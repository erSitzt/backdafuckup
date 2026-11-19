using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Proxies;
using Backdafuckup.Proxy;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Proxies;
using Backdafuckup.Testing.Proxy;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>Backup proxies (vSphere spec, T5): enrollment, the control connection, and a disk read through NBD tunnels.</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task A_proxy_enrolls_once_connects_and_serves_an_attached_disk_through_nbd_tunnels()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);

        // The agent package for download: none yet, then the files a build put next to the server.
        Assert.Equal((null, 0), ((await client.GetFromJsonAsync<ProxyPackageDto>("/api/proxies/package", Ct))!.Version, (await client.GetFromJsonAsync<ProxyPackageDto>("/api/proxies/package", Ct))!.Files.Count));
        Directory.CreateDirectory(Path.Combine(_root, "proxy"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "proxy", "backdafuckup-proxy"), [0x7f, (byte)'E', (byte)'L', (byte)'F'], Ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "proxy", "version.txt"), "0.3.0\n", Ct);
        var package = (await client.GetFromJsonAsync<ProxyPackageDto>("/api/proxies/package", Ct))!;
        Assert.Equal(("0.3.0", "backdafuckup-proxy", 4L), (package.Version, Assert.Single(package.Files).Name, package.Files[0].SizeBytes));
        Assert.Equal([0x7f, (byte)'E', (byte)'L', (byte)'F'], await client.GetByteArrayAsync("/api/proxies/package/backdafuckup-proxy", Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/proxies/package/version.txt", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/proxies/package/..%2Fappsettings.json", Ct)).StatusCode);

        var created = await Read<ProxyInstallDto>(client.PostAsJsonAsync("/api/proxies", new CreateProxyDto("proxy01"), Ct));
        Assert.StartsWith($"backdafuckup-proxy enroll --server https://localhost/ --proxy {created.Proxy.Id:D} --token bdfupi_", created.Command, StringComparison.Ordinal);
        Assert.False(created.Proxy.Enrolled);

        // The install token works once; a wrong one is refused.
        var wrong = await client.PostAsJsonAsync("/api/agent/proxies/enroll", new EnrollProxyRequestDto(created.Proxy.Id, "bdfupi_wrong", "proxy01"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var enrolled = await Read<EnrollProxyResponseDto>(client.PostAsJsonAsync("/api/agent/proxies/enroll", new EnrollProxyRequestDto(created.Proxy.Id, created.InstallToken, "proxy01"), Ct));
        Assert.StartsWith("bdfupk_", enrolled.AgentKey, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/agent/proxies/enroll", new EnrollProxyRequestDto(created.Proxy.Id, created.InstallToken, "proxy01"), Ct)).StatusCode);

        // The agent: its "devices" are files; the disk to read appears after a rescan, like a hot-added disk.
        Directory.CreateDirectory(_root);
        var diskFile = Path.Combine(_root, "hotadd-disk.img");
        var content = RandomNumberGenerator.GetBytes(6 << 20);
        await File.WriteAllBytesAsync(diskFile, content, Ct);
        var devices = new FileBlockDevices();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        // open-iscsi as Debian installs it (no name before iscsid's first start), and two more settings missing: all three are reported at once.
        var etc = Path.Combine(_root, "etc");
        Directory.CreateDirectory(Path.Combine(etc, "iscsi"));
        await File.WriteAllTextAsync(Path.Combine(etc, "iscsi", "initiatorname.iscsi"), "## DO NOT EDIT OR REMOVE THIS FILE!\nGenerateName=yes\n", Ct);
        var agent = new ProxyAgentClient(
            new ProxyAgentSettings(_factory.Server.BaseAddress, created.Proxy.Id, enrolled.AgentKey),
            new ProxyHostInfo("proxy01.corp.example", "421a2b3c-4d5e-6f70-8192-a3b4c5d6e7f8", "VMware-42 1a 2b 3c 4d 5e 6f 70-81 92 a3 b4 c5 d6 e7 f8", ["10.26.16.91"], "test", null),
            devices, ConnectAgentSocketAsync, log.Enqueue, new IscsiadmInitiator(etcRoot: etc),
            _ => Task.FromResult<IReadOnlyList<string>>(["LVM would activate the volume groups it finds on an array snapshot", "udisks2 runs"]));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = agent.RunAsync(stop.Token);
        try
        {
            var proxies = _factory.Services.GetRequiredService<ProxyAgents>();
            proxies.DeviceWait = TimeSpan.FromSeconds(2);
            var online = await WaitAsync(async () => (await client.GetFromJsonAsync<List<ProxyDto>>("/api/proxies", Ct))!.SingleOrDefault(p => p.Online));
            Assert.Equal(("proxy01", true, "proxy01.corp.example", "421a2b3c-4d5e-6f70-8192-a3b4c5d6e7f8"), (online.Name, online.Enrolled, online.HostName, online.SmbiosUuid));
            Assert.Equal([ProxyAgentProtocol.CapabilityHotAdd, ProxyAgentProtocol.CapabilityNbd], online.Capabilities);
            Assert.Null(online.InitiatorName);
            Assert.Equal(
                [$"open-iscsi is installed, but has no initiator name yet ({Path.Combine(etc, "iscsi", "initiatorname.iscsi")} says GenerateName=yes): start iscsid once (systemctl start iscsid)",
                 "LVM would activate the volume groups it finds on an array snapshot", "udisks2 runs"],
                online.SanRefusal!.Split('\n'));

            var pool = _factory.Services.GetRequiredService<IProxyPool>();
            var info = Assert.Single(pool.Connected());
            Assert.Equal(created.Proxy.Id, info.Id);

            // A disk that never appears: the proxy says which serial it waited for and what it sees.
            var missing = await Assert.ThrowsAsync<IOException>(() => pool.AttachAsync(info.Id, "6000C299-0000-0000-0000-000000000000", content.Length, writable: false, Ct));
            Assert.Contains("no device with serial 6000c299000000000000000000000000 appeared within 2 s on proxy01.corp.example", missing.Message, StringComparison.Ordinal);
            Assert.Contains("disk.EnableUUID=TRUE", missing.Message, StringComparison.Ordinal);

            // The hypervisor attaches the disk; the proxy finds it by serial after a rescan, and the server reads it over 3 tunnels.
            devices.Attach("sdb", diskFile, "6000C299-0a45-3675-4c2e-d06d38504664");
            await using (var disk = await pool.AttachAsync(info.Id, "6000C299-0a45-3675-4c2e-d06d38504664", content.Length, writable: false, Ct))
            {
                Assert.Equal($"proxy proxy01 (host proxy01.corp.example) {diskFile}", disk.Description);
                await using var reader = await disk.OpenReaderAsync(connections: 3, maxInFlightPerConnection: 4, Ct);
                Assert.Equal(("hotadd", (long)content.Length), (reader.TransportName, reader.Capacity));
                var copy = new byte[content.Length];
                await Task.WhenAll(Enumerable.Range(0, 24).Select(i => reader.ReadAsync(i * (256L << 10), copy.AsMemory(i * (256 << 10), 256 << 10), Ct).AsTask()));
                Assert.True(copy.AsSpan().SequenceEqual(content));
            }

            // Released: closed and removed from the proxy's kernel (before the disk is detached).
            Assert.Equal(["sdb"], devices.Deleted);
            Assert.Contains(log, l => l.Contains("Released", StringComparison.Ordinal) && l.Contains("removed it from the kernel", StringComparison.Ordinal));
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }

        // A renewed install token disconnects the proxy until it enrolls again.
        var renewed = await Read<ProxyInstallDto>(client.PostAsync($"/api/proxies/{created.Proxy.Id}/renew", null, Ct));
        Assert.False(renewed.Proxy.Enrolled);
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAgentSocketAsync(new Uri(_factory.Server.BaseAddress, ProxyAgentProtocol.ControlPath(created.Proxy.Id)), enrolled.AgentKey, Ct));
    }

    [Fact]
    public async Task A_proxy_whose_program_differs_from_the_package_is_offered_the_update_and_downloads_it_with_its_key()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        var build = RandomNumberGenerator.GetBytes(300_000);
        Directory.CreateDirectory(Path.Combine(_root, "proxy"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "proxy", ProxySelfUpdate.ProgramName), build, Ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "proxy", "version.txt"), "0.3.1\n", Ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(build));

        var created = await Read<ProxyInstallDto>(client.PostAsJsonAsync("/api/proxies", new CreateProxyDto("proxy01"), Ct));
        var enrolled = await Read<EnrollProxyResponseDto>(client.PostAsJsonAsync("/api/agent/proxies/enroll", new EnrollProxyRequestDto(created.Proxy.Id, created.InstallToken, "proxy01"), Ct));

        async Task<ProxyAgentMessage?> ConnectAsync(string filesHash)
        {
            var offered = new TaskCompletionSource<ProxyAgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var agent = new ProxyAgentClient(
                new ProxyAgentSettings(_factory.Server.BaseAddress, created.Proxy.Id, enrolled.AgentKey),
                new ProxyHostInfo("proxy01.corp.example", null, null, ["10.26.16.91"], "0.3.0+old", filesHash),
                new FileBlockDevices(), ConnectAgentSocketAsync, _ => { }, update: offer =>
                {
                    offered.TrySetResult(offer);
                    return Task.CompletedTask;
                });
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var running = agent.RunAsync(stop.Token);
            try
            {
                await WaitAsync(async () => (await client.GetFromJsonAsync<List<ProxyDto>>("/api/proxies", Ct))!.SingleOrDefault(p => p.Online));
                return await Task.WhenAny(offered.Task, Task.Delay(TimeSpan.FromSeconds(3), Ct)) == offered.Task ? offered.Task.Result : null;
            }
            finally
            {
                await stop.CancelAsync();
                await running;
            }
        }

        // An agent of another build gets the offer: the package's version, size and SHA-256, and where to download it.
        var offer = await ConnectAsync(new string('0', 64));
        Assert.NotNull(offer);
        Assert.Equal(("0.3.1", (long?)build.Length, hash, $"api/agent/proxies/{created.Proxy.Id:D}/package/backdafuckup-proxy"), (offer.AgentVersion, offer.SizeBytes, offer.Sha256, offer.Path));

        // The download needs the agent's key.
        var server = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.GetAsync(offer.Path, Ct)).StatusCode);
        using (var request = new HttpRequestMessage(HttpMethod.Get, offer.Path))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", enrolled.AgentKey);
            using var response = await server.SendAsync(request, Ct);
            Assert.Equal(build, await response.Content.ReadAsByteArrayAsync(Ct));
        }

        // An agent that runs the package's build is left alone.
        Assert.Null(await ConnectAsync(hash));
    }
}
