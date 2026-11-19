using System.Text.RegularExpressions;

namespace Backdafuckup.Proxy.Tests;

/// <summary><c>backdafuckup-proxy prepare-san</c> on a fake Ubuntu proxy: what it changes, what it leaves to the admin.</summary>
public sealed partial class SanPreparationTests : IDisposable
{
    private readonly DirectoryInfo _etc = Directory.CreateTempSubdirectory("bdfu-prepare-san-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string InitiatorFile => Path.Combine(_etc.FullName, "iscsi", "initiatorname.iscsi");

    private string LvmLocal => Path.Combine(_etc.FullName, "lvm", "lvmlocal.conf");

    public void Dispose() => _etc.Delete(recursive: true);

    /// <summary>
    /// A proxy as Ubuntu 24.04 leaves it: open-iscsi without a name until iscsid starts, LVM 2.03.16 without a devices
    /// file, multipathd and udisks2 running. Commands answer from that state and change it.
    /// </summary>
    private sealed partial class FakeMachine
    {
        private readonly string _etc;

        public FakeMachine(string etc)
        {
            _etc = etc;
            Directory.CreateDirectory(Path.Combine(etc, "iscsi"));
            File.WriteAllText(Path.Combine(etc, "iscsi", "initiatorname.iscsi"), "## DO NOT EDIT OR REMOVE THIS FILE!\nGenerateName=yes\n");
            Directory.CreateDirectory(Path.Combine(etc, "lvm"));
            File.WriteAllText(Path.Combine(etc, "lvm", "lvmlocal.conf"), "# This is a local configuration file template.\nlocal {\n\t# system_id = \"\"\n}\n");
        }

        /// <summary>Debian and Ubuntu: iscsid's start writes the name; elsewhere it does not.</summary>
        public bool IscsidGeneratesName { get; set; } = true;

        public HashSet<string> Active { get; } = new(StringComparer.Ordinal) { "multipathd.service", "multipathd.socket", "udisks2.service" };

        public List<string> VolumeGroups { get; } = [];

        public string MultipathMaps { get; set; } = string.Empty;

        public string Sessions { get; set; } = string.Empty;

        public List<string> Calls { get; } = [];

        public Task<(int ExitCode, string Output)> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var line = $"{program} {string.Join(' ', arguments)}".TrimEnd();
            Calls.Add(line);
            const string isActive = "systemctl is-active --quiet ";
            const string mask = "systemctl mask --now ";
            return Task.FromResult(line switch
            {
                "iscsiadm --version" => (0, "iscsiadm version 2.1.9"),
                "iscsiadm -m session" => Sessions.Length == 0 ? (21, "iscsiadm: No active sessions.") : (0, Sessions),
                "systemctl start iscsid.service" => StartIscsid(),
                "iscsi-iname" => (0, "iqn.2016-04.com.open-iscsi:5c1f2e3d4a5b\n"),
                "systemctl try-restart iscsid.service" => (0, string.Empty),
                "lvmconfig --typeconfig full devices/use_devicesfile" => (0, "use_devicesfile=0"),
                "lvmconfig activation/auto_activation_volume_list" => ActivationList(),
                "lvmconfig --typeconfig full devices/global_filter" => (0, "global_filter=[\"a|.*|\"]"),
                "lvmconfig --validate" => (0, "  LVM configuration valid."),
                "vgs --noheadings -o vg_name" => (0, string.Concat(VolumeGroups.Select(v => $"  {v}\n")) + "  WARNING: PV /dev/sdz has no metadata area.\n"),
                "multipath -l -v1" => (0, MultipathMaps),
                _ when line.StartsWith(isActive, StringComparison.Ordinal) => Active.Contains(line[isActive.Length..]) ? (0, string.Empty) : (3, string.Empty),
                _ when line.StartsWith(mask, StringComparison.Ordinal) => Mask(line[mask.Length..].Split(' ')),
                _ => (Commands.NotFound, $"{program}: not expected in this test ({line})"),
            });
        }

        private (int, string) StartIscsid()
        {
            if (IscsidGeneratesName)
            {
                File.WriteAllText(Path.Combine(_etc, "iscsi", "initiatorname.iscsi"), "## DO NOT EDIT OR REMOVE THIS FILE!\nInitiatorName=iqn.2004-10.com.ubuntu:01:9f8e7d6c5b4a\n");
            }

            return (0, string.Empty);
        }

        private (int, string) ActivationList()
        {
            var match = ListSetting().Match(File.ReadAllText(Path.Combine(_etc, "lvm", "lvmlocal.conf")));
            return match.Success
                ? (0, $"auto_activation_volume_list=[{string.Join(',', match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))}]")
                : (5, "  Configuration node activation/auto_activation_volume_list not found");
        }

        private (int, string) Mask(string[] units)
        {
            foreach (var unit in units)
            {
                Active.Remove(unit);
            }

            return (0, string.Empty);
        }

        [GeneratedRegex(@"^\s*auto_activation_volume_list\s*=\s*\[([^\]]*)\]", RegexOptions.Multiline)]
        private static partial Regex ListSetting();
    }

    private static (SanPreparation Preparation, List<string> Output) Prepare(FakeMachine machine, string etc, bool privileged = true)
    {
        var output = new List<string>();
        return (new SanPreparation(output.Add, machine.RunAsync, etc, privileged), output);
    }

    [Fact]
    public async Task Prepare_san_makes_every_setting_after_which_the_agents_check_passes_and_a_second_run_changes_nothing()
    {
        var machine = new FakeMachine(_etc.FullName);
        machine.VolumeGroups.Add("ubuntu-vg");
        var (preparation, output) = Prepare(machine, _etc.FullName);

        Assert.Equal(0, await preparation.RunAsync(dryRun: false, Ct));

        Assert.Contains("  [changed] started iscsid, which generated the initiator name iqn.2004-10.com.ubuntu:01:9f8e7d6c5b4a: list it in the proxy's host object on the array", output);
        Assert.Contains(output, l => l.StartsWith("  [changed] LVM activates by itself only [\"ubuntu-vg\"] (activation/auto_activation_volume_list), written to ", StringComparison.Ordinal));
        Assert.Contains(output, l => l.StartsWith("  [changed] stopped and masked multipathd (no multipath devices on this machine)", StringComparison.Ordinal));
        Assert.Contains(output, l => l.StartsWith("  [changed] stopped and masked udisks2", StringComparison.Ordinal));
        Assert.Equal("SAN access is ready. The agent tells the server within a minute; the console then shows the initiator name.", output[^1]);
        Assert.Contains("systemctl mask --now multipathd.service multipathd.socket", machine.Calls);
        Assert.Contains("systemctl mask --now udisks2.service", machine.Calls);
        var local = await File.ReadAllTextAsync(LvmLocal, Ct);
        Assert.StartsWith("# This is a local configuration file template.\nlocal {", local, StringComparison.Ordinal);
        Assert.Contains("activation {\n\tauto_activation_volume_list = [ \"ubuntu-vg\" ]\n}\n", local, StringComparison.Ordinal);
        Assert.Empty(await SanSelfCheck.CheckAsync(machine.RunAsync, Ct, _etc.FullName));

        // Again: everything is in place, nothing is written twice.
        output.Clear();
        Assert.Equal(0, await preparation.RunAsync(dryRun: false, Ct));
        Assert.All(output.Where(l => l.StartsWith("  [", StringComparison.Ordinal)), l => Assert.StartsWith("  [ok] ", l, StringComparison.Ordinal));
        Assert.Equal(local, await File.ReadAllTextAsync(LvmLocal, Ct));
    }

    [Fact]
    public async Task A_dry_run_changes_nothing_and_lists_what_is_missing()
    {
        var machine = new FakeMachine(_etc.FullName);
        var before = (await File.ReadAllTextAsync(InitiatorFile, Ct), await File.ReadAllTextAsync(LvmLocal, Ct));
        var (preparation, output) = Prepare(machine, _etc.FullName, privileged: false);

        Assert.Equal(1, await preparation.RunAsync(dryRun: true, Ct));

        Assert.Equal(before, (await File.ReadAllTextAsync(InitiatorFile, Ct), await File.ReadAllTextAsync(LvmLocal, Ct)));
        Assert.DoesNotContain(machine.Calls, c => c.StartsWith("systemctl start", StringComparison.Ordinal) || c.StartsWith("systemctl mask", StringComparison.Ordinal));
        Assert.Contains("  [would change] start iscsid once, so that it generates the initiator name", output);
        Assert.Contains(output, l => l.StartsWith("  [would change] write activation/auto_activation_volume_list = [ ] into ", StringComparison.Ordinal));
        Assert.Contains("4 things are missing for SAN access (prepare-san without --dry-run makes what it can):", output);
    }

    [Fact]
    public async Task Without_root_nothing_runs()
    {
        var machine = new FakeMachine(_etc.FullName);
        var (preparation, output) = Prepare(machine, _etc.FullName, privileged: false);

        Assert.Equal(2, await preparation.RunAsync(dryRun: false, Ct));
        Assert.Empty(machine.Calls);
        Assert.StartsWith("prepare-san changes system settings: run it as root", Assert.Single(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_is_not_safe_to_change_is_left_to_the_admin_with_the_reason()
    {
        var machine = new FakeMachine(_etc.FullName)
        {
            // A backup is reading a snapshot right now; the machine uses multipath for its own disks.
            Sessions = "tcp: [3] 192.0.2.50:3260,21 iqn.2000-05.com.3pardata:20210002ac000001 (non-flash)\n",
            MultipathMaps = "mpatha\n",
        };
        var (preparation, output) = Prepare(machine, _etc.FullName);

        Assert.Equal(1, await preparation.RunAsync(dryRun: false, Ct));

        Assert.Contains(output, l => l.StartsWith("  [by hand] LVM would activate", StringComparison.Ordinal) && l.Contains("This machine has iSCSI sessions (tcp: [3] 192.0.2.50:3260,21", StringComparison.Ordinal)
                                     && l.EndsWith("run prepare-san again when no backup uses the proxy", StringComparison.Ordinal));
        Assert.Contains(output, l => l.StartsWith("  [by hand] multipathd runs and this machine uses multipath devices (mpatha): it stays", StringComparison.Ordinal));
        Assert.DoesNotContain("systemctl mask --now multipathd.service multipathd.socket", machine.Calls);
        Assert.Contains("systemctl mask --now udisks2.service", machine.Calls);
        Assert.DoesNotContain("activation {", await File.ReadAllTextAsync(LvmLocal, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_admins_own_activation_section_stays_and_an_srs_volume_group_never_lands_in_the_list()
    {
        var machine = new FakeMachine(_etc.FullName);
        machine.VolumeGroups.AddRange(["ubuntu-vg", "VG_XenStorage-3f1c2d4e-5a6b-7c8d-9e0f-1a2b3c4d5e6f", "data"]);
        await File.AppendAllTextAsync(LvmLocal, "activation {\n\tvolume_list = [ \"ubuntu-vg\" ]\n}\n", Ct);
        var (preparation, output) = Prepare(machine, _etc.FullName);

        await preparation.RunAsync(dryRun: false, Ct);
        Assert.Contains(output, l => l.StartsWith("  [by hand] LVM would activate", StringComparison.Ordinal)
                                     && l.EndsWith("lvmlocal.conf has an activation section already: add auto_activation_volume_list = [ \"ubuntu-vg\", \"data\" ] to it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Where_iscsid_generates_no_name_one_is_made_with_iscsi_iname_and_the_old_file_kept()
    {
        var machine = new FakeMachine(_etc.FullName) { IscsidGeneratesName = false };
        var (preparation, output) = Prepare(machine, _etc.FullName);

        await preparation.RunAsync(dryRun: false, Ct);

        Assert.Equal("InitiatorName=iqn.2016-04.com.open-iscsi:5c1f2e3d4a5b\n", await File.ReadAllTextAsync(InitiatorFile, Ct));
        Assert.Contains("GenerateName=yes", await File.ReadAllTextAsync(InitiatorFile + ".bdfu-backup", Ct), StringComparison.Ordinal);
        Assert.Contains("systemctl try-restart iscsid.service", machine.Calls);
        Assert.Contains(output, l => l.StartsWith("  [changed] wrote the initiator name iqn.2016-04.com.open-iscsi:5c1f2e3d4a5b to ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_second_run_replaces_its_own_block_and_keeps_the_rest()
    {
        var text = $"local {{\n}}\n\n{SanSelfCheck.LvmBlockBegin}: …\nactivation {{\n\tauto_activation_volume_list = [ ]\n}}\n{SanSelfCheck.LvmBlockEnd}\n";
        Assert.Equal("local {\n}\n", SanPreparation.WithoutOurBlock(text));
        Assert.Equal(string.Empty, SanPreparation.WithoutOurBlock($"{SanSelfCheck.LvmBlockBegin}\n{SanSelfCheck.LvmBlockEnd}\n"));
    }
}
