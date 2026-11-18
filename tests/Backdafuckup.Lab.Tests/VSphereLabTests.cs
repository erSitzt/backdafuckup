using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Tls;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.Lab.Tests;

/// <summary>
/// Opt-in, read-only checks of the vim25 client against a real vCenter: BDFU_VSPHERE_LAB=1 for the run, and
/// ~/.config/backdafuckup/vcenter-test.env (or BDFU_VSPHERE_ENV_FILE) with VCENTER_URL, VCENTER_USER, VCENTER_PASSWORD,
/// VCENTER_SPKI (SHA-256 of the vCenter certificate's public key, base64) and VCENTER_TEST_FOLDER (the moref of the test
/// VM folder). Nothing is changed in vCenter.
/// </summary>
public sealed class VSphereLabTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> Settings()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("BDFU_VSPHERE_LAB") == "1",
            "vSphere lab tests talk to a real vCenter. Set BDFU_VSPHERE_LAB=1 for the run to enable them.");
        var path = Environment.GetEnvironmentVariable("BDFU_VSPHERE_ENV_FILE") is { Length: > 0 } file
            ? file
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "backdafuckup", "vcenter-test.env");
        var settings = LabEnvironment.ReadFile(path);
        foreach (var key in new[] { "VCENTER_URL", "VCENTER_USER", "VCENTER_PASSWORD", "VCENTER_SPKI", "VCENTER_TEST_FOLDER" })
        {
            Assert.True(settings.ContainsKey(key), $"{path} has no {key}.");
        }

        return settings;
    }

    private static Task<VimSession> ConnectAsync(Dictionary<string, string> settings)
    {
        var url = new Uri(settings["VCENTER_URL"]);
        var transport = new VimTransport(url.Host, url.Port, CertificatePinning.CreateValidator(TlsTrustMode.Pinned, [settings["VCENTER_SPKI"]]));
        return VimSession.ConnectAsync(transport, settings["VCENTER_USER"], settings["VCENTER_PASSWORD"], Ct);
    }

    [Fact]
    public async Task The_client_logs_in_and_reads_the_test_folder_and_a_host_certificate()
    {
        var settings = Settings();
        await using var session = await ConnectAsync(settings);
        LabEnvironment.Say($"{session.Content.About.FullName}, API {session.Transport.Version}");
        Assert.Equal("VirtualCenter", session.Content.About.ApiType);

        var folder = new ManagedObjectReference("Folder", settings["VCENTER_TEST_FOLDER"]);
        var vms = await session.RetrieveAllAsync("VirtualMachine", ["name", "config.instanceUuid", "config.uuid", "runtime.powerState", "config.changeTrackingEnabled"], Ct, folder);
        foreach (var vm in vms)
        {
            LabEnvironment.Say($"{vm.Str("name")} {vm.Obj.Value} instance {vm.Str("config.instanceUuid")} bios {vm.Str("config.uuid")} {vm.Str("runtime.powerState")} cbt={vm.Str("config.changeTrackingEnabled")}");
        }

        Assert.NotEmpty(vms);
        Assert.All(vms, vm => Assert.True(Guid.TryParse(vm.Str("config.instanceUuid"), out _)));

        var host = (await session.RetrieveAllAsync("HostSystem", ["name", "config.certificate"], Ct)).First();
        var pem = System.Text.Encoding.ASCII.GetString(host["config.certificate"]!.Bytes());
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem, StringComparison.Ordinal);
        LabEnvironment.Say($"{host.Str("name")}: certificate SPKI {CertificatePinning.SpkiSha256FromPem(pem)}");
    }

    [Fact]
    public async Task The_provider_tests_the_connection_and_reads_inventory_storage_and_guest_addresses()
    {
        var settings = Settings();
        var url = new Uri(settings["VCENTER_URL"]);
        var connection = new ConnectionSettings(
            Guid.NewGuid(), HypervisorKind.VSphere, url.Host, url.Port, new System.Net.NetworkCredential(settings["VCENTER_USER"], settings["VCENTER_PASSWORD"]),
            TlsTrustMode.Pinned, [new PinnedCertificate(url.Host, settings["VCENTER_SPKI"], string.Empty, string.Empty)], new Dictionary<string, string>());
        var provider = new Backdafuckup.VSphere.VSphereProvider(new Backdafuckup.VSphere.VSphereOptions());

        var test = await provider.TestConnectionAsync(connection, Ct);
        Assert.True(test.Success, test.Error);
        LabEnvironment.Say($"{test.Platform!.ProductName} {test.Platform.ProductVersion}, platform {test.Platform.PlatformId}, {test.HostPins.Count} host pin(s)");
        Assert.NotEmpty(test.HostPins);

        await using var session = await provider.OpenAsync(connection, Ct);
        var inventory = await session.GetInventoryAsync(Ct);
        foreach (var kind in new[] { InventoryKind.Host, InventoryKind.Storage, InventoryKind.Network, InventoryKind.Vm })
        {
            LabEnvironment.Say($"{kind}: {inventory.Items.Count(i => i.Kind == kind)}");
        }

        var testVms = inventory.Items.Where(i => i.Kind == InventoryKind.Vm && i.Name.StartsWith("bdfu-vs-", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(testVms);
        foreach (var vm in testVms)
        {
            LabEnvironment.Say($"{vm.Name}: {string.Join(", ", vm.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"))}");
            Assert.True(long.Parse(vm.Properties["diskBytes"], System.Globalization.CultureInfo.InvariantCulture) > 0);
        }

        var state = await session.GetStorageStateAsync(Ct);
        LabEnvironment.Say($"datastores: {string.Join(", ", state.Storages.Select(s => $"{s.Name} {s.Disks} disks"))}; snapshots {state.Snapshots.Count} (ours {state.Snapshots.Count(s => s.Ours)})");
        Assert.DoesNotContain(state.Snapshots, s => s.Ours);

        var linux = testVms.First(v => v.Properties["powerState"] == "poweredOn");
        var addresses = await session.GetGuestAddressesAsync(linux.NativeId, Ct);
        LabEnvironment.Say($"{linux.Name}: {string.Join(", ", addresses)}");
        Assert.NotEmpty(addresses);
    }
}
