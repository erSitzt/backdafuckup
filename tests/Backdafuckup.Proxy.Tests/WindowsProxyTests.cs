using System.Buffers.Binary;
using System.Text;
using Backdafuckup.Proxy.Windows;
using Microsoft.Win32.SafeHandles;

namespace Backdafuckup.Proxy.Tests;

/// <summary>
/// The Windows proxy's logic on Linux: the WWN from Windows' storage descriptors, sector-aligned reads, disks found by WWN
/// and set offline and read-only before they are read, iSCSI through existing sessions only, the SAN policy, the service
/// setup. Windows itself sits behind fakes (PowerShell, sc.exe, the disk list).
/// </summary>
public sealed class WindowsProxyTests
{
    private const string Naa = "60002ac0000000000000002300019c45";
    private const string Target = "iqn.2000-05.com.3pardata:20210002ac000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers PowerShell scripts by the first rule whose text the script contains; records every script.</summary>
    private sealed class FakePowerShell
    {
        public List<string> Scripts { get; } = [];

        public List<(string Contains, Func<(int, string)> Answer)> Rules { get; } = [];

        public PowerShellRunner Runner => (script, _) =>
        {
            lock (Scripts)
            {
                Scripts.Add(script);
            }

            return Task.FromResult(Rules.FirstOrDefault(r => script.Contains(r.Contains, StringComparison.Ordinal)).Answer?.Invoke() ?? (0, string.Empty));
        };

        public FakePowerShell On(string contains, int code, string output)
        {
            Rules.Add((contains, () => (code, output)));
            return this;
        }
    }

    // --- Storage descriptors -------------------------------------------------------------------------------------

    /// <summary>A STORAGE_DEVICE_UNIQUE_IDENTIFIER with a STORAGE_DEVICE_ID_DESCRIPTOR as Windows returned it on the lab VM (spike).</summary>
    private static byte[] UniqueId(params (int CodeSet, int Type, int Association, byte[] Id)[] identifiers)
    {
        var entries = new List<byte>();
        foreach (var (codeSet, type, association, id) in identifiers)
        {
            var entry = new byte[(16 + id.Length + 3) / 4 * 4];
            BinaryPrimitives.WriteInt32LittleEndian(entry, codeSet);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(4), type);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(8), (ushort)id.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(10), (ushort)entry.Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), association);
            id.CopyTo(entry, 16);
            entries.AddRange(entry);
        }

        var descriptor = new byte[12 + entries.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4), (uint)descriptor.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(8), (uint)identifiers.Length);
        entries.CopyTo(descriptor, 12);

        var unique = new byte[20 + descriptor.Length + 48];
        BinaryPrimitives.WriteUInt32LittleEndian(unique, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(unique.AsSpan(4), (uint)unique.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(unique.AsSpan(8), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(unique.AsSpan(12), (uint)(20 + descriptor.Length));
        descriptor.CopyTo(unique, 20);
        return unique;
    }

    [Fact]
    public void The_wwn_is_the_device_naa_identifier_of_page_0x83_the_longest_when_there_are_several()
    {
        var naa16 = Convert.FromHexString(Naa);
        var naa8 = Convert.FromHexString("2ff70002ac000001");
        Assert.Equal(Naa, WindowsStorageLayout.NaaFromUniqueId(UniqueId((1, 3, 0, naa16))));
        Assert.Equal(Naa, WindowsStorageLayout.NaaFromUniqueId(UniqueId((1, 3, 0, naa8), (2, 8, 0, Encoding.ASCII.GetBytes("naa.x")), (1, 3, 0, naa16))));

        // A port's NAA name, an ASCII identifier or only the header (StorageDeviceIdProperty on the lab VM): no WWN.
        Assert.Null(WindowsStorageLayout.NaaFromUniqueId(UniqueId((1, 3, 1, naa16))));
        Assert.Null(WindowsStorageLayout.NaaFromUniqueId(UniqueId((2, 3, 0, naa16))));
        var headerOnly = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(headerOnly, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(headerOnly.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(headerOnly.AsSpan(8), 1);
        Assert.Null(WindowsStorageLayout.NaaFromDeviceId(headerOnly));
        Assert.Null(WindowsStorageLayout.NaaFromUniqueId(new byte[8]));
    }

    [Fact]
    public void The_model_and_sector_size_come_from_their_descriptors()
    {
        // STORAGE_DEVICE_DESCRIPTOR as the spike's disk had it: VendorIdOffset 40, ProductIdOffset 49.
        var device = new byte[96];
        BinaryPrimitives.WriteUInt32LittleEndian(device, 0x28);
        BinaryPrimitives.WriteUInt32LittleEndian(device.AsSpan(4), (uint)device.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(device.AsSpan(12), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(device.AsSpan(16), 49);
        Encoding.ASCII.GetBytes("3PARdata\0VV              \0").CopyTo(device, 40);
        Assert.Equal("3PARdata VV", WindowsStorageLayout.Model(device));

        var alignment = new byte[28];
        BinaryPrimitives.WriteInt32LittleEndian(alignment.AsSpan(16), 4096);
        Assert.Equal(4096, WindowsStorageLayout.LogicalSector(alignment));
        Assert.Equal(512, WindowsStorageLayout.LogicalSector(new byte[8]));
    }

    // --- Aligned reads -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reads_of_any_offset_and_length_reach_the_disk_only_as_whole_aligned_sectors()
    {
        var disk = new byte[3 << 20];
        new Random(5).NextBytes(disk);
        var reads = new List<(long Offset, int Length, bool Aligned)>();
        var device = new AlignedDiskDevice(new ProxyDevice("PhysicalDrive3", @"\\.\PhysicalDrive3", disk.Length, Naa, "3PARdata VV"), 512, (buffer, offset) =>
        {
            unsafe
            {
                fixed (byte* address = buffer)
                {
                    lock (reads)
                    {
                        reads.Add((offset, buffer.Length, (nint)address % 512 == 0));
                    }
                }
            }

            disk.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
            return buffer.Length;
        });

        foreach (var (offset, length) in new (long, int)[] { (0, 4096), (100, 1000), (511, 2), (4095, 5 << 20 >> 2), (disk.Length - 7, 7), (1 << 20, 3 << 20 >> 1) })
        {
            var into = new byte[length];
            await device.ReadAsync(offset, into, Ct);
            Assert.Equal(disk.AsSpan((int)offset, length).ToArray(), into);
        }

        Assert.All(reads, r => Assert.True(r.Offset % 512 == 0 && r.Length % 512 == 0 && r.Aligned, $"read of {r.Length} at {r.Offset}"));
        Assert.All(reads, r => Assert.True(r.Offset + r.Length <= disk.Length));
        await Assert.ThrowsAsync<IOException>(async () => await device.ReadAsync(disk.Length - 10, new byte[20], Ct));
        await Assert.ThrowsAsync<IOException>(async () => await device.WriteAsync(0, new byte[512], Ct));
        Assert.True(device.ReadOnly);
    }

    // --- Disks ---------------------------------------------------------------------------------------------------

    private sealed class FakeDisks(string image) : IWindowsDisks
    {
        public List<WindowsDisk> Disks { get; } = [];

        public List<WindowsDisk> Opened { get; } = [];

        public IReadOnlyList<WindowsDisk> List() => [.. Disks];

        public SafeFileHandle OpenRead(WindowsDisk disk)
        {
            Opened.Add(disk);
            return File.OpenHandle(image, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
    }

    [Fact]
    public async Task A_snapshot_disk_is_found_by_its_wwn_set_offline_and_read_only_and_never_opened_for_writing()
    {
        var image = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(image, Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray(), Ct);
            var disks = new FakeDisks(image);
            disks.Disks.Add(new WindowsDisk(0, @"\\.\PhysicalDrive0", 1L << 40, 512, null, "HPE LOGICAL VOLUME"));
            disks.Disks.Add(new WindowsDisk(7, @"\\.\PhysicalDrive7", 8192, 512, Naa.ToUpperInvariant(), "3PARdata VV"));
            var powershell = new FakePowerShell().On("Get-Disk", 0, "7 True True");
            var devices = new WindowsBlockDevices(disks, powershell.Runner);

            var listed = Assert.Single(await devices.ListAsync(Ct), d => d.Serial == Naa);
            Assert.Equal(("PhysicalDrive7", 8192L), (listed.Name, listed.SizeBytes));

            await Assert.ThrowsAsync<InvalidOperationException>(() => devices.OpenAsync(listed, writable: true, Ct));
            Assert.Empty(powershell.Scripts);

            using (var opened = await devices.OpenAsync(listed, writable: false, Ct))
            {
                Assert.True(opened.ReadOnly);
                var first = new byte[1000];
                await opened.ReadAsync(100, first, Ct);
                Assert.Equal(Enumerable.Range(100, 1000).Select(i => (byte)i), first);
            }

            var script = Assert.Single(powershell.Scripts);
            Assert.Contains($"-eq '{Naa}'", script, StringComparison.Ordinal);
            Assert.Contains("Set-Disk -InputObject $d -IsOffline $true", script, StringComparison.Ordinal);
            Assert.Contains("Set-Disk -InputObject $d -IsReadOnly $true", script, StringComparison.Ordinal);
            Assert.Equal(7, Assert.Single(disks.Opened).Number);

            // Windows says the disk is still online (or another disk has the number now): it is not read.
            powershell.Rules.Clear();
            powershell.On("Get-Disk", 0, "7 False True");
            var refused = await Assert.ThrowsAsync<IOException>(() => devices.OpenAsync(listed, writable: false, Ct));
            Assert.Contains("could not be set offline and read-only", refused.Message, StringComparison.Ordinal);
            Assert.Single(disks.Opened);

            // Rescan: Windows' own.
            await devices.RescanAsync(Ct);
            Assert.Equal("Update-HostStorageCache", powershell.Scripts[^1]);
        }
        finally
        {
            File.Delete(image);
        }
    }

    // --- iSCSI ---------------------------------------------------------------------------------------------------

    private static Task Reachable(string host, int port, CancellationToken cancellationToken) => Task.CompletedTask;

    [Fact]
    public async Task The_windows_proxy_uses_existing_iscsi_sessions_and_never_logs_in_itself()
    {
        var powershell = new FakePowerShell()
            .On("Get-Service MSiSCSI", 0, "{\"service\":\"Running\",\"node\":\"iqn.1991-05.com.microsoft:win-proxy-1.corp.example\"}")
            .On("Get-IscsiSession", 0, "2");
        var initiator = new WindowsIscsiInitiator(powershell.Runner, Reachable, () => ["192.0.2.111/24 (SAN-A)"]);

        Assert.Equal("iqn.1991-05.com.microsoft:win-proxy-1.corp.example", await initiator.InitiatorNameAsync(Ct));
        Assert.Null(await initiator.InitiatorProblemAsync(Ct));
        await initiator.LoginAsync(["192.0.2.15", "192.0.2.16"], Target, Ct);
        Assert.Contains(powershell.Scripts, s => s.Contains($"TargetNodeAddress -eq '{Target}'", StringComparison.Ordinal));

        // Without a session the proxy says so, and still does not log in.
        powershell.Rules.Insert(0, ("Get-IscsiSession", () => (0, "0")));
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => initiator.LoginAsync(["192.0.2.15"], Target, Ct));
        Assert.Equal($"this server has no iSCSI session to {Target}: connect the array's target as for its other LUNs (with MPIO); " +
                     "the Windows proxy uses the existing sessions only and does not log in itself", missing.Message);
        Assert.DoesNotContain(powershell.Scripts, s => s.Contains("Connect-IscsiTarget", StringComparison.Ordinal) || s.Contains("New-IscsiTargetPortal", StringComparison.Ordinal)
                                                        || s.Contains("iscsicli", StringComparison.OrdinalIgnoreCase) || s.Contains("Disconnect-IscsiTarget", StringComparison.Ordinal));

        // A portal it cannot reach is named with the server's own addresses, as on Linux.
        var unreachable = new WindowsIscsiInitiator(powershell.Runner, (_, _, ct) => Task.Delay(Timeout.Infinite, ct), () => ["192.0.2.111/24 (SAN-A)"]);
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => unreachable.LoginAsync(["192.0.2.15"], Target, Ct));
        Assert.StartsWith("none of the array's iSCSI addresses answers: 192.0.2.15:3260 (no TCP connection within 3 s). This proxy's addresses: 192.0.2.111/24 (SAN-A).", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_iscsi_initiator_service_the_proxy_offers_no_san_access_and_says_why()
    {
        var powershell = new FakePowerShell().On("Get-Service MSiSCSI", 0, "{\"service\":\"Stopped\",\"node\":null}");
        var initiator = new WindowsIscsiInitiator(powershell.Runner, Reachable);
        Assert.Null(await initiator.InitiatorNameAsync(Ct));
        Assert.Equal("the Microsoft iSCSI initiator service (MSiSCSI) is stopped: the proxy reads the array's LUNs through the server's existing iSCSI sessions",
            await initiator.InitiatorProblemAsync(Ct));
    }

    // --- SAN policy ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("OfflineShared", false)]
    [InlineData("OfflineAll", false)]
    [InlineData("OnlineAll", true)]
    [InlineData("OfflineInternal", true)]
    public async Task An_unsafe_san_policy_is_reported_and_set_to_offline_shared_as_the_other_product_does(string policy, bool unsafePolicy)
    {
        var powershell = new FakePowerShell().On("NewDiskPolicy", 0, policy);
        var problems = await WindowsSanCheck.CheckAsync(powershell.Runner, Ct);
        Assert.Equal(unsafePolicy, problems.Count == 1);

        var lines = new List<string>();
        Assert.Equal(0, await new WindowsSanPreparation(powershell.Runner, lines.Add).RunAsync(dryRun: true, Ct));
        Assert.DoesNotContain(powershell.Scripts, s => s.Contains("Set-StorageSetting", StringComparison.Ordinal));

        Assert.Equal(0, await new WindowsSanPreparation(powershell.Runner, lines.Add).RunAsync(dryRun: false, Ct));
        Assert.Equal(unsafePolicy ? 1 : 0, powershell.Scripts.Count(s => s == "Set-StorageSetting -NewDiskPolicy OfflineShared"));
        Assert.Contains(lines, l => unsafePolicy ? l.StartsWith($"Set the SAN policy from {policy} to OfflineShared", StringComparison.Ordinal) : l == $"SAN policy {policy}: nothing to do.");
    }

    [Fact]
    public void PowerShell_errors_come_back_as_their_message_and_values_are_quoted()
    {
        const string clixml = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><S S=\"Error\">No MSFT_Disk objects found_x000D__x000A_</S></Objs>";
        Assert.Equal("No MSFT_Disk objects found", PowerShell.Clean(clixml));
        Assert.Equal("plain", PowerShell.Clean(" plain\n"));
        Assert.Equal("'it''s'", PowerShell.Quote("it's"));
        Assert.StartsWith("$ErrorActionPreference = 'Stop'", Encoding.Unicode.GetString(Convert.FromBase64String(PowerShell.Encode("Get-Disk"))), StringComparison.Ordinal);
    }

    // --- Service setup -------------------------------------------------------------------------------------------

    /// <summary>sc.exe for one service: query, create, config, start, stop, delete, failure; records every call.</summary>
    private sealed class FakeSc
    {
        public string? State { get; set; }

        public List<string> Calls { get; } = [];

        public Task<(int ExitCode, string Output)> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(string.Join(' ', arguments));
            return Task.FromResult(arguments[0] switch
            {
                "query" => State is null ? (1060, "The specified service does not exist as an installed service.") : (0, $"SERVICE_NAME: backdafuckup-proxy\n        STATE              : 4  {State}"),
                "create" => Set("STOPPED"),
                "start" => Set("RUNNING"),
                "stop" => Set("STOPPED"),
                "delete" => Set(null),
                _ => (0, string.Empty),
            });
        }

        private (int, string) Set(string? state)
        {
            State = state;
            return (0, string.Empty);
        }
    }

    [Fact]
    public async Task Install_puts_the_program_in_place_enrolls_and_starts_a_service_that_restarts_after_an_update()
    {
        var root = Directory.CreateTempSubdirectory("bdfu-winsetup-").FullName;
        try
        {
            var source = Path.Combine(root, "download", "backdafuckup-proxy.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllTextAsync(source, "program v1", Ct);
            var sc = new FakeSc();
            var lines = new List<string>();
            var setup = new WindowsSetup(sc.RunAsync, lines.Add, Path.Combine(root, "Program Files", "backdafuckup Proxy"), Path.Combine(root, "ProgramData", "backdafuckup-proxy"))
            {
                Watch = TimeSpan.FromMilliseconds(50),
            };
            var enrolled = 0;

            Assert.Equal(0, await setup.InstallAsync(source, _ =>
            {
                enrolled++;
                return Task.FromResult(0);
            }, Ct));

            Assert.Equal("program v1", await File.ReadAllTextAsync(setup.ProgramPath, Ct));
            Assert.Equal(1, enrolled);
            Assert.Contains($"create backdafuckup-proxy binPath= \"{setup.ProgramPath}\" run start= delayed-auto obj= LocalSystem DisplayName= backdafuckup backup proxy", sc.Calls);
            Assert.Contains("failure backdafuckup-proxy reset= 86400 actions= restart/10000/restart/10000/restart/60000", sc.Calls);
            Assert.Contains("failureflag backdafuckup-proxy 1", sc.Calls);
            Assert.Equal("RUNNING", sc.State);
            Assert.StartsWith("Installed and started", lines[^1], StringComparison.Ordinal);

            // Again (a new token, a new build): the running agent is stopped first, the service updated, not created twice.
            await File.WriteAllTextAsync(source, "program v2", Ct);
            sc.Calls.Clear();
            Assert.Equal(0, await setup.InstallAsync(source, _ => Task.FromResult(0), Ct));
            Assert.Equal("stop backdafuckup-proxy", sc.Calls.First(c => !c.StartsWith("query", StringComparison.Ordinal)));
            Assert.Contains(sc.Calls, c => c.StartsWith("config backdafuckup-proxy binPath=", StringComparison.Ordinal));
            Assert.DoesNotContain(sc.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
            Assert.Equal("program v2", await File.ReadAllTextAsync(setup.ProgramPath, Ct));

            // A refused enrollment (a spent token) installs no service.
            var fresh = new FakeSc();
            var other = new WindowsSetup(fresh.RunAsync, lines.Add, Path.Combine(root, "p2"), Path.Combine(root, "d2"));
            Assert.Equal(1, await other.InstallAsync(source, _ => Task.FromResult(1), Ct));
            Assert.DoesNotContain(fresh.Calls, c => c.StartsWith("create", StringComparison.Ordinal));

            // Uninstall: service, key and log gone; the program folder after this process when it runs from there.
            var later = new List<string>();
            Assert.Equal(0, await setup.UninstallAsync(setup.ProgramPath, later.Add, Ct));
            Assert.Null(sc.State);
            Assert.False(Directory.Exists(Path.Combine(root, "ProgramData", "backdafuckup-proxy")));
            Assert.Equal([Path.Combine(root, "Program Files", "backdafuckup Proxy")], later);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
