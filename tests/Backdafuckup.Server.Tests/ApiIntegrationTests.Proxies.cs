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

        // Without the systemd unit the package is incomplete: no installer.
        var incomplete = await Read<ProxyInstallDto>(client.PostAsJsonAsync("/api/proxies", new CreateProxyDto("proxy00"), Ct));
        Assert.Equal((null, null), (incomplete.InstallCommand, incomplete.InstallScript));
        await client.DeleteAsync($"/api/proxies/{incomplete.Proxy.Id}", Ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "proxy", "backdafuckup-proxy.service"), "[Unit]\nDescription=backdafuckup backup proxy\n\n[Service]\nExecStart=/opt/backdafuckup-proxy/backdafuckup-proxy run\n", Ct);

        var created = await Read<ProxyInstallDto>(client.PostAsJsonAsync("/api/proxies", new CreateProxyDto("proxy01"), Ct));
        Assert.StartsWith($"backdafuckup-proxy enroll --server https://localhost/ --proxy {created.Proxy.Id:D} --token bdfupi_", created.Command, StringComparison.Ordinal);
        Assert.False(created.Proxy.Enrolled);

        // The one-liner and the same installer as a file: the token in a header, the agent's SHA-256 and the unit in the script.
        Assert.StartsWith("curl -sSL ", created.InstallCommand, StringComparison.Ordinal);
        Assert.Contains($"-H 'Authorization: Bearer {created.InstallToken}' https://localhost/api/agent/proxies/{created.Proxy.Id:D}/install.sh | sudo bash", created.InstallCommand, StringComparison.Ordinal);
        var script = created.InstallScript!;
        Assert.Contains($"local proxy_id='{created.Proxy.Id:D}'", script, StringComparison.Ordinal);
        Assert.Contains($"local token='{created.InstallToken}'", script, StringComparison.Ordinal);
        Assert.Contains($"local program_sha256='{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([0x7f, (byte)'E', (byte)'L', (byte)'F']))}'", script, StringComparison.Ordinal);
        Assert.Contains("\n[Unit]\nDescription=backdafuckup backup proxy\n", script, StringComparison.Ordinal);
        Assert.Contains("\nBACKDAFUCKUP_UNIT\n", script, StringComparison.Ordinal);
        Assert.Contains("trap \"rm -rf '$work'\" EXIT", script, StringComparison.Ordinal);
        if (OperatingSystem.IsLinux())
        {
            var file = Path.Combine(_root, "install.sh");
            await File.WriteAllTextAsync(file, script, Ct);
            using var bash = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("bash", ["-n", file]) { RedirectStandardError = true })!;
            var syntax = await bash.StandardError.ReadToEndAsync(Ct);
            await bash.WaitForExitAsync(Ct);
            Assert.True(bash.ExitCode == 0, $"bash -n: {syntax}");
        }

        // The script and the program for the install token only (in the header); the token is not spent by that.
        var anonymous = _factory.CreateClient();
        HttpRequestMessage Install(string path, string? token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/agent/proxies/{created.Proxy.Id:D}/{path}");
            if (token is not null)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            return request;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Install("install.sh", null), Ct)).StatusCode);
        var refused = await anonymous.SendAsync(Install("install.sh", "bdfupi_wrong"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        // The refusal is a script too (the one-liner pipes it into bash): it says why and ends with 1.
        var refusal = await refused.Content.ReadAsStringAsync(Ct);
        Assert.Contains("backdafuckup proxy install: Unknown proxy or wrong install token", refusal, StringComparison.Ordinal);
        if (OperatingSystem.IsLinux())
        {
            using var bash = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("bash") { RedirectStandardInput = true, RedirectStandardError = true })!;
            await bash.StandardInput.WriteAsync(refusal);
            bash.StandardInput.Close();
            var said = await bash.StandardError.ReadToEndAsync(Ct);
            await bash.WaitForExitAsync(Ct);
            Assert.Equal(1, bash.ExitCode);
            Assert.StartsWith("backdafuckup proxy install: Unknown proxy or wrong install token", said, StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Install("install/backdafuckup-proxy", null), Ct)).StatusCode);
        var served = await anonymous.SendAsync(Install("install.sh", created.InstallToken), Ct);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Contains($"local proxy_id='{created.Proxy.Id:D}'", await served.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal([0x7f, (byte)'E', (byte)'L', (byte)'F'], await (await anonymous.SendAsync(Install("install/backdafuckup-proxy", created.InstallToken), Ct)).Content.ReadAsByteArrayAsync(Ct));

        // The install token works once; a wrong one is refused.
        var wrong = await client.PostAsJsonAsync("/api/agent/proxies/enroll", new EnrollProxyRequestDto(created.Proxy.Id, "bdfupi_wrong", "proxy01"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var enrolled = await Read<EnrollProxyResponseDto>(client.PostAsJsonAsync("/api/agent/proxies/enroll", new EnrollProxyRequestDto(created.Proxy.Id, created.InstallToken, "proxy01"), Ct));
        // Spent by the enrollment: the installer is refused from now on.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Install("install.sh", created.InstallToken), Ct)).StatusCode);
        Assert.StartsWith("bdfupk_", enrolled.AgentKey, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/agent/proxies/enroll", new EnrollProxyRequestDto(created.Proxy.Id, created.InstallToken, "proxy01"), Ct)).StatusCode);

        // The agent: its "devices" are files; the disk to read appears after a rescan, like a hot-added disk.
        Directory.CreateDirectory(_root);
        var diskFile = Path.Combine(_root, "hotadd-disk.img");
        var content = RandomNumberGenerator.GetBytes(6 << 20);
        await File.WriteAllBytesAsync(diskFile, content, Ct);
        var devices = new FileBlockDevices();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        // open-iscsi as Debian installs it (no name before iscsid's first start), and two more settings missing. The agent's own
        // preparation runs once before it reports and makes the two (not the name here); what is left is reported.
        var etc = Path.Combine(_root, "etc");
        var prepared = 0;
        Directory.CreateDirectory(Path.Combine(etc, "iscsi"));
        await File.WriteAllTextAsync(Path.Combine(etc, "iscsi", "initiatorname.iscsi"), "## DO NOT EDIT OR REMOVE THIS FILE!\nGenerateName=yes\n", Ct);
        var agent = new ProxyAgentClient(
            new ProxyAgentSettings(_factory.Server.BaseAddress, created.Proxy.Id, enrolled.AgentKey),
            new ProxyHostInfo("proxy01.corp.example", "421a2b3c-4d5e-6f70-8192-a3b4c5d6e7f8", "VMware-42 1a 2b 3c 4d 5e 6f 70-81 92 a3 b4 c5 d6 e7 f8", ["10.26.16.91"], "test", null),
            devices, ConnectAgentSocketAsync, log.Enqueue, new IscsiadmInitiator(etcRoot: etc),
            _ => Task.FromResult<IReadOnlyList<string>>(prepared == 0 ? ["LVM would activate the volume groups it finds on an array snapshot", "udisks2 runs"] : []),
            prepareSan: _ =>
            {
                Interlocked.Increment(ref prepared);
                return Task.CompletedTask;
            });
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
            Assert.Equal(1, prepared);
            Assert.Contains(log, l => l.StartsWith("No SAN access yet (open-iscsi is installed, but has no initiator name yet", StringComparison.Ordinal) && l.EndsWith("preparing this machine (prepare-san).", StringComparison.Ordinal));
            Assert.Equal(
                [$"open-iscsi is installed, but has no initiator name yet ({Path.Combine(etc, "iscsi", "initiatorname.iscsi")} says GenerateName=yes): start iscsid once (systemctl start iscsid)"],
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

    [Fact]
    public async Task A_windows_proxy_is_offered_the_windows_build_never_the_linux_one_and_never_hotadd()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        var linux = RandomNumberGenerator.GetBytes(200_000);
        var windows = RandomNumberGenerator.GetBytes(250_000);
        Directory.CreateDirectory(Path.Combine(_root, "proxy"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "proxy", ProxySelfUpdate.ProgramName), linux, Ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "proxy", "version.txt"), "0.3.1\n", Ct);

        var created = await Read<ProxyInstallDto>(client.PostAsJsonAsync("/api/proxies", new CreateProxyDto("win-proxy-1"), Ct));
        var enrolled = await Read<EnrollProxyResponseDto>(client.PostAsJsonAsync("/api/agent/proxies/enroll", new EnrollProxyRequestDto(created.Proxy.Id, created.InstallToken, "win-proxy-1"), Ct));

        async Task<ProxyAgentMessage?> ConnectAsync()
        {
            var offered = new TaskCompletionSource<ProxyAgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var agent = new ProxyAgentClient(
                new ProxyAgentSettings(_factory.Server.BaseAddress, created.Proxy.Id, enrolled.AgentKey),
                new ProxyHostInfo("win-proxy-1.corp.example", null, null, ["192.0.2.111"], "0.3.0+old", new string('0', 64), ProxyPlatforms.Windows, HotAdd: false),
                new FileBlockDevices(), ConnectAgentSocketAsync, _ => { }, update: offer =>
                {
                    offered.TrySetResult(offer);
                    return Task.CompletedTask;
                });
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var running = agent.RunAsync(stop.Token);
            try
            {
                var online = await WaitAsync(async () => (await client.GetFromJsonAsync<List<ProxyDto>>("/api/proxies", Ct))!.SingleOrDefault(p => p.Online));
                Assert.Equal(ProxyPlatforms.Windows, online.Platform);
                Assert.DoesNotContain(ProxyCapabilities.HotAdd, online.Capabilities);
                Assert.Equal(ProxyPlatforms.Windows, _factory.Services.GetRequiredService<IProxyPool>().Connected().Single().Platform);
                return await Task.WhenAny(offered.Task, Task.Delay(TimeSpan.FromSeconds(3), Ct)) == offered.Task ? offered.Task.Result : null;
            }
            finally
            {
                await stop.CancelAsync();
                await running;
            }
        }

        // The package has only the Linux build: nothing is offered to the Windows agent.
        Assert.Null(await ConnectAsync());

        // With the Windows build in the package, that one is offered, at its own path.
        await File.WriteAllBytesAsync(Path.Combine(_root, "proxy", ProxySelfUpdate.WindowsProgramName), windows, Ct);
        var offer = await ConnectAsync();
        Assert.NotNull(offer);
        Assert.Equal(((long?)windows.Length, Convert.ToHexStringLower(SHA256.HashData(windows)), $"api/agent/proxies/{created.Proxy.Id:D}/package/backdafuckup-proxy.exe"),
            (offer.SizeBytes, offer.Sha256, offer.Path));
        using var request = new HttpRequestMessage(HttpMethod.Get, offer.Path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", enrolled.AgentKey);
        using var response = await _factory.CreateClient().SendAsync(request, Ct);
        Assert.Equal(windows, await response.Content.ReadAsByteArrayAsync(Ct));

        // Only the two program names are served.
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync($"/api/agent/proxies/{created.Proxy.Id:D}/package/backdafuckup-proxy.service", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_windows_installer_pins_the_server_checks_the_agent_and_throws_the_reason_when_refused()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://backup.corp.example:9555/", Ct);
        var windows = RandomNumberGenerator.GetBytes(100_000);
        Directory.CreateDirectory(Path.Combine(_root, "proxy"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "proxy", ProxySelfUpdate.WindowsProgramName), windows, Ct);

        var created = await Read<ProxyInstallDto>(client.PostAsJsonAsync("/api/proxies", new CreateProxyDto("win-proxy-1"), Ct));

        // No Linux package: no Linux one-liner, but the Windows one.
        Assert.Null(created.InstallCommand);
        // This test server has no pinned key (its own endpoints); with one, the line pins it (checked below).
        var line = created.WindowsInstallCommand!;
        Assert.StartsWith("$s = curl.exe -sSL ", line, StringComparison.Ordinal);
        Assert.Contains($"-H 'Authorization: Bearer {created.InstallToken}' 'https://backup.corp.example:9555/api/agent/proxies/{created.Proxy.Id:D}/install.ps1'", line, StringComparison.Ordinal);
        Assert.EndsWith("iex ($s -join \"`n\")", line, StringComparison.Ordinal);
        var pin = new string('a', 64);
        Assert.Contains($"curl.exe -sSL --pinnedpubkey 'sha256//{Convert.ToBase64String(Convert.FromHexString(pin))}' --insecure -H",
            ProxyWindowsInstall.OneLiner("https://backup.corp.example:9555/", created.Proxy.Id, "bdfupi_x", [pin]), StringComparison.Ordinal);
        Assert.Contains("$curlPins = 'sha256//", ProxyWindowsInstall.Build("p", created.Proxy.Id, "https://b/", "t", [pin], "1", "ab", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => ProxyWindowsInstall.OneLiner("https://b/", created.Proxy.Id, "it's", []));
        Assert.StartsWith($"backdafuckup-proxy.exe install --server https://backup.corp.example:9555/ --proxy {created.Proxy.Id:D} --token {created.InstallToken} --pins ", created.WindowsCommand, StringComparison.Ordinal);

        // The script the line fetches: with the install token only, the agent's SHA-256 in it.
        var server = _factory.CreateClient();
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"/api/agent/proxies/{created.Proxy.Id:D}/install.ps1"))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", created.InstallToken);
            using var response = await server.SendAsync(request, Ct);
            var script = await response.Content.ReadAsStringAsync(Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains($"$sha256 = '{Convert.ToHexString(SHA256.HashData(windows))}'", script, StringComparison.Ordinal);
            Assert.Contains("& $file install --server $server --proxy $proxyId --token $token --pins $pins", script, StringComparison.Ordinal);
            Assert.Contains("& {", created.WindowsInstallScript, StringComparison.Ordinal);
        }

        // A wrong token: a script that throws the reason (the one-liner runs whatever comes).
        using (var wrong = new HttpRequestMessage(HttpMethod.Get, $"/api/agent/proxies/{created.Proxy.Id:D}/install.ps1"))
        {
            wrong.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "bdfupi_wrong");
            using var response = await server.SendAsync(wrong, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.StartsWith("throw 'backdafuckup proxy install: Unknown proxy or wrong install token", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
    }
}
