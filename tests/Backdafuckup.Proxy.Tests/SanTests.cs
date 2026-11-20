namespace Backdafuckup.Proxy.Tests;

/// <summary>The proxy's SAN access: iSCSI login through iscsiadm, and the self-check before it is offered.</summary>
public sealed class SanTests
{
    private const string Target = "iqn.2000-05.com.3pardata:20210002ac000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers commands from a table (program and arguments joined by spaces), records every call.</summary>
    private sealed class FakeCommands(Dictionary<string, (int Code, string Output)> answers)
    {
        public List<string> Calls { get; } = [];

        public Task<(int ExitCode, string Output)> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var line = $"{program} {string.Join(' ', arguments)}";
            Calls.Add(line);
            return Task.FromResult(answers.FirstOrDefault(a => line.StartsWith(a.Key, StringComparison.Ordinal)).Value is var (code, output) && (code != 0 || output is not null)
                ? (code, output ?? string.Empty)
                : (0, string.Empty));
        }
    }

    [Fact]
    public async Task The_login_discovers_sets_manual_startup_logs_in_and_accepts_an_existing_session()
    {
        var commands = new FakeCommands(new()
        {
            ["iscsiadm -m discovery -t sendtargets -p 10.26.101.10"] = (0, $"10.26.101.10:3260,21 {Target}\n"),
            ["iscsiadm -m discovery -t sendtargets -p 10.26.102.10"] = (0, $"10.26.102.10:3260,22 {Target}\n"),
            ["iscsiadm -m node -T " + Target + " -p 10.26.102.10 --login"] = (15, "iscsiadm: default: 1 session requested, but 1 already present."),
        });
        var initiator = new IscsiadmInitiator(commands.RunAsync, connect: Reachable);

        await initiator.LoginAsync(["10.26.101.10", "10.26.102.10"], Target, Ct);

        Assert.Contains($"iscsiadm -m node -T {Target} -p 10.26.101.10 -o update -n node.startup -v manual", commands.Calls);
        Assert.Contains($"iscsiadm -m node -T {Target} -p 10.26.101.10 --login", commands.Calls);
        Assert.Contains($"iscsiadm -m node -T {Target} -p 10.26.102.10 --login", commands.Calls);
    }

    [Fact]
    public async Task A_login_that_reaches_no_portal_names_each_portal_and_why()
    {
        var commands = new FakeCommands(new()
        {
            ["iscsiadm -m discovery -t sendtargets -p 10.26.101.10"] = (4, "iscsiadm: connect to 10.26.101.10 timed out"),
            ["iscsiadm -m discovery -t sendtargets -p 10.26.102.10"] = (0, "10.26.102.10:3260,22 iqn.other:target\n"),
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new IscsiadmInitiator(commands.RunAsync, connect: Reachable).LoginAsync(["10.26.101.10", "10.26.102.10"], Target, Ct));
        Assert.Equal($"No iSCSI login to {Target}: 10.26.101.10: discovery failed (4): iscsiadm: connect to 10.26.101.10 timed out; 10.26.102.10: discovery did not list {Target}", ex.Message);
    }

    private static Task Reachable(string host, int port, CancellationToken cancellationToken) => Task.CompletedTask;

    [Fact]
    public async Task A_proxy_outside_the_iscsi_networks_is_told_so_in_seconds_with_its_own_addresses_and_iscsiadm_is_not_run()
    {
        var commands = new FakeCommands([]);
        // An address outside the proxy's networks: the connection never opens.
        var initiator = new IscsiadmInitiator(commands.RunAsync, connect: (_, _, ct) => Task.Delay(Timeout.Infinite, ct), ownAddresses: () => ["192.0.2.91/24 (ens192)"]);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => initiator.LoginAsync(["10.26.101.10", "10.26.102.10:3261"], Target, Ct));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Equal("none of the array's iSCSI addresses answers: 10.26.101.10:3260 (no TCP connection within 3 s); 10.26.102.10:3261 (no TCP connection within 3 s). " +
                     "This proxy's addresses: 192.0.2.91/24 (ens192). It needs a network interface in the array's iSCSI network(s), or a route to them.", ex.Message);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task Only_the_portals_that_answer_are_logged_in_to_and_the_others_are_named()
    {
        var commands = new FakeCommands(new()
        {
            ["iscsiadm -m discovery -t sendtargets -p 10.26.101.10"] = (0, $"10.26.101.10:3260,21 {Target}\n"),
        });
        var initiator = new IscsiadmInitiator(commands.RunAsync,
            connect: (host, _, ct) => host == "10.26.101.10" ? Task.CompletedTask : Task.FromException(new System.Net.Sockets.SocketException(113)));

        await initiator.LoginAsync(["10.26.101.10", "10.26.102.10"], Target, Ct);
        var reach = await initiator.ProbeAsync(["10.26.101.10", "10.26.102.10"], Ct);

        Assert.Contains($"iscsiadm -m node -T {Target} -p 10.26.101.10 --login", commands.Calls);
        Assert.DoesNotContain(commands.Calls, c => c.Contains("10.26.102.10", StringComparison.Ordinal));
        Assert.Equal([true, false], reach.Select(r => r.Reachable));
        Assert.Equal("10.26.102.10:3260", reach[1].Portal);
        Assert.NotNull(reach[1].Error);
    }

    [Fact]
    public async Task The_initiator_name_comes_from_the_open_iscsi_configuration()
    {
        var etc = Directory.CreateTempSubdirectory("bdfu-san-").FullName;
        try
        {
            var initiator = new IscsiadmInitiator(null, etc);
            Assert.Null(await initiator.InitiatorNameAsync(Ct));
            Assert.StartsWith("no iSCSI initiator (open-iscsi) is installed:", await initiator.InitiatorProblemAsync(Ct), StringComparison.Ordinal);

            // As Debian and Ubuntu install it: the name is generated at iscsid's first start.
            Directory.CreateDirectory(Path.Combine(etc, "iscsi"));
            await File.WriteAllTextAsync(Path.Combine(etc, "iscsi", "initiatorname.iscsi"), "## DO NOT EDIT\nGenerateName=yes\n", Ct);
            Assert.Null(await initiator.InitiatorNameAsync(Ct));
            Assert.Contains("open-iscsi is installed, but has no initiator name yet", await initiator.InitiatorProblemAsync(Ct), StringComparison.Ordinal);
            Assert.Contains("systemctl start iscsid", await initiator.InitiatorProblemAsync(Ct), StringComparison.Ordinal);

            await File.WriteAllTextAsync(Path.Combine(etc, "iscsi", "initiatorname.iscsi"), "## DO NOT EDIT\nInitiatorName=iqn.2004-10.com.ubuntu:01:proxy01\n", Ct);
            Assert.Equal("iqn.2004-10.com.ubuntu:01:proxy01", await initiator.InitiatorNameAsync(Ct));
            Assert.Null(await initiator.InitiatorProblemAsync(Ct));
        }
        finally
        {
            Directory.Delete(etc, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, "use_devicesfile=1", true, 5, null, "global_filter=[\"a|.*|\"]", null)]
    [InlineData(0, "use_devicesfile=1", false, 5, null, "global_filter=[\"a|.*|\"]", "LVM would activate the volume groups")]
    [InlineData(0, "use_devicesfile=0", false, 0, "auto_activation_volume_list=[]", "global_filter=[\"a|.*|\"]", null)]
    [InlineData(0, "use_devicesfile=0", false, 0, "auto_activation_volume_list=[\"ubuntu-vg\"]", "global_filter=[\"a|.*|\"]", null)]
    [InlineData(0, "use_devicesfile=0", false, 0, "auto_activation_volume_list=[\"@*\"]", "global_filter=[\"a|.*|\"]", "LVM would activate the volume groups")]
    [InlineData(0, "use_devicesfile=0", false, 5, null, "global_filter=[\"a|^/dev/sda3$|\", \"r|.*|\"]", null)]
    [InlineData(0, "use_devicesfile=0", false, 5, null, "global_filter=[\"r|/dev/cdrom|\"]", "LVM would activate the volume groups")]
    // LVM before 2.03.12 does not know use_devicesfile: no devices file, and no "lvmconfig failed".
    [InlineData(5, "Configuration node devices/use_devicesfile not found", false, 5, null, "global_filter=[\"a|.*|\"]", "list the local ones in activation/auto_activation_volume_list")]
    public async Task Lvm_leaves_foreign_volume_groups_alone_with_a_devices_file_an_activation_list_or_an_accept_list(
        int devicesCode, string devicesFile, bool systemDevicesExists, int listCode, string? list, string filter, string? problem)
    {
        var etc = Directory.CreateTempSubdirectory("bdfu-san-");
        try
        {
            if (systemDevicesExists)
            {
                Directory.CreateDirectory(Path.Combine(etc.FullName, "lvm", "devices"));
                await File.WriteAllTextAsync(Path.Combine(etc.FullName, "lvm", "devices", "system.devices"), "VERSION=1.1.1\n", Ct);
            }

            var commands = new FakeCommands(new()
            {
                ["lvmconfig --typeconfig full devices/use_devicesfile"] = (devicesCode, devicesFile),
                ["lvmconfig activation/auto_activation_volume_list"] = (listCode, list ?? "Configuration node activation/auto_activation_volume_list not found"),
                ["lvmconfig --typeconfig full devices/global_filter"] = (0, filter),
                ["systemctl is-active"] = (3, string.Empty),
            });
            var problems = await SanSelfCheck.CheckAsync(commands.RunAsync, Ct, etc.FullName);
            if (problem is null)
            {
                Assert.Empty(problems);
            }
            else
            {
                Assert.Contains(problem, Assert.Single(problems), StringComparison.Ordinal);
            }

            // event_activation=0 is not asked for any more: since LVM 2.03.15 nothing activates volume groups at boot then.
            Assert.DoesNotContain(commands.Calls, c => c.Contains("event_activation", StringComparison.Ordinal));
        }
        finally
        {
            etc.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Every_missing_setting_is_reported_at_once()
    {
        var commands = new FakeCommands(new()
        {
            ["lvmconfig --typeconfig full devices/use_devicesfile"] = (0, "use_devicesfile=0"),
            ["lvmconfig activation/auto_activation_volume_list"] = (5, "Configuration node activation/auto_activation_volume_list not found"),
            ["lvmconfig --typeconfig full devices/global_filter"] = (0, "global_filter=[\"a|.*|\"]"),
            ["systemctl is-active --quiet multipathd.socket"] = (3, string.Empty),
        });
        var problems = await SanSelfCheck.CheckAsync(commands.RunAsync, Ct, Path.GetTempPath());

        Assert.Equal(3, problems.Count);
        Assert.StartsWith("LVM would activate the volume groups it finds on an array snapshot", problems[0], StringComparison.Ordinal);
        Assert.StartsWith("multipathd runs: it would claim a snapshot LUN", problems[1], StringComparison.Ordinal);
        Assert.StartsWith("udisks2 runs", problems[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_lvm_tools_nothing_activates_volume_groups()
    {
        var commands = new FakeCommands(new() { ["lvmconfig"] = (Commands.NotFound, "lvmconfig is not installed"), ["systemctl is-active"] = (3, string.Empty) });
        Assert.Empty(await SanSelfCheck.CheckAsync(commands.RunAsync, Ct));
    }
}

/// <summary>The proxy agent's self-update: the server's program file checked and swapped in.</summary>
public sealed class SelfUpdateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Serve(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
    }

    [Fact]
    public async Task The_checked_download_replaces_the_program_and_the_old_file_goes_at_the_next_start()
    {
        var folder = Directory.CreateTempSubdirectory("bdfu-update-").FullName;
        try
        {
            var program = Path.Combine(folder, ProxySelfUpdate.ProgramName);
            await File.WriteAllTextAsync(program, "old build", Ct);
            var build = "new build"u8.ToArray();
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(build));
            using var http = new HttpClient(new Serve(build));

            await ProxySelfUpdate.InstallAsync(http, new Uri("https://backup01.corp.example:9555/x"), program, build.Length, hash, Ct);

            Assert.Equal("new build", await File.ReadAllTextAsync(program, Ct));
            Assert.Equal("old build", await File.ReadAllTextAsync(program + ".old", Ct));
            Assert.Equal(hash, ProxySelfUpdate.HashOf(program));
            if (!OperatingSystem.IsWindows())
            {
                Assert.True(File.GetUnixFileMode(program).HasFlag(UnixFileMode.UserExecute));
            }

            ProxySelfUpdate.RemoveReplaced(program);
            Assert.False(File.Exists(program + ".old"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_download_that_does_not_match_leaves_the_program_untouched()
    {
        var folder = Directory.CreateTempSubdirectory("bdfu-update-").FullName;
        try
        {
            var program = Path.Combine(folder, ProxySelfUpdate.ProgramName);
            await File.WriteAllTextAsync(program, "old build", Ct);
            using var http = new HttpClient(new Serve("tampered"u8.ToArray()));
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ProxySelfUpdate.InstallAsync(http, new Uri("https://backup01.corp.example:9555/x"), program, 9, new string('a', 64), Ct));
            Assert.Contains("the server named 9 bytes and aaaa", ex.Message, StringComparison.Ordinal);
            Assert.Equal("old build", await File.ReadAllTextAsync(program, Ct));
            Assert.Equal([ProxySelfUpdate.ProgramName], Directory.GetFiles(folder).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
