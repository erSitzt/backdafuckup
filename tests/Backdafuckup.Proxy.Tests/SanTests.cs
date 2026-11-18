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
        var initiator = new IscsiadmInitiator(commands.RunAsync);

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
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new IscsiadmInitiator(commands.RunAsync).LoginAsync(["10.26.101.10", "10.26.102.10"], Target, Ct));
        Assert.Equal($"No iSCSI login to {Target}: 10.26.101.10: discovery failed (4): iscsiadm: connect to 10.26.101.10 timed out; 10.26.102.10: discovery did not list {Target}", ex.Message);
    }

    [Fact]
    public async Task The_initiator_name_comes_from_the_open_iscsi_configuration()
    {
        var etc = Directory.CreateTempSubdirectory("bdfu-san-").FullName;
        try
        {
            Assert.Null(await new IscsiadmInitiator(null, etc).InitiatorNameAsync(Ct));
            Directory.CreateDirectory(Path.Combine(etc, "iscsi"));
            await File.WriteAllTextAsync(Path.Combine(etc, "iscsi", "initiatorname.iscsi"), "## DO NOT EDIT\nInitiatorName=iqn.2004-10.com.ubuntu:01:proxy01\n", Ct);
            Assert.Equal("iqn.2004-10.com.ubuntu:01:proxy01", await new IscsiadmInitiator(null, etc).InitiatorNameAsync(Ct));
        }
        finally
        {
            Directory.Delete(etc, recursive: true);
        }
    }

    [Theory]
    [InlineData("use_devicesfile=1\nglobal_filter=[]\nevent_activation=0\n", 3, null)]
    [InlineData("use_devicesfile=0\nglobal_filter=[\"a|^/dev/sda$|\", \"r|.*|\"]\nevent_activation=0\n", 3, null)]
    [InlineData("use_devicesfile=0\nglobal_filter=[]\nevent_activation=0\n", 3, "LVM would scan every disk")]
    [InlineData("use_devicesfile=1\nglobal_filter=[]\nevent_activation=1\n", 3, "set global/event_activation=0")]
    [InlineData("use_devicesfile=1\nglobal_filter=[]\nevent_activation=0\n", 0, "udisks2 runs")]
    public async Task San_access_is_offered_only_when_lvm_leaves_foreign_disks_alone_and_nothing_mounts(string lvmconfig, int udisksActive, string? refusal)
    {
        var commands = new FakeCommands(new()
        {
            ["lvmconfig"] = (0, lvmconfig),
            ["systemctl is-active --quiet udisks2"] = (udisksActive, string.Empty),
        });
        var finding = await SanSelfCheck.CheckAsync(commands.RunAsync, Ct);
        if (refusal is null)
        {
            Assert.Null(finding);
        }
        else
        {
            Assert.Contains(refusal, finding, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Without_lvm_tools_nothing_activates_volume_groups()
    {
        var commands = new FakeCommands(new() { ["lvmconfig"] = (Commands.NotFound, "lvmconfig is not installed"), ["systemctl is-active --quiet udisks2"] = (3, string.Empty) });
        Assert.Null(await SanSelfCheck.CheckAsync(commands.RunAsync, Ct));
    }
}
