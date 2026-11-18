using System.Globalization;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere;

/// <summary>Reading a VM's virtual hardware (<c>config.hardware.device</c>).</summary>
internal static class VSphereDevices
{
    public static readonly string[] NicTypes = ["VirtualVmxnet3", "VirtualVmxnet3Vrdma", "VirtualVmxnet2", "VirtualVmxnet", "VirtualE1000e", "VirtualE1000", "VirtualPCNet32", "VirtualSriovEthernetCard"];

    public static readonly string[] ScsiControllerTypes = ["ParaVirtualSCSIController", "VirtualLsiLogicSASController", "VirtualLsiLogicController", "VirtualBusLogicController"];

    public static readonly string[] OtherDiskControllerTypes = ["VirtualAHCIController", "VirtualNVMEController"];

    /// <summary>
    /// The VM generation id the guest sees (Windows uses it to notice that it was restored or copied): vSphere keeps it in
    /// the VM's configuration as <c>vm.genid</c> and <c>vm.genidX</c>; written "genid:genidX" like Xen's. Null if the VM
    /// has none (older hardware versions, guests without the device).
    /// </summary>
    public static string? GenerationId(VimObject? extraConfig)
    {
        string? Value(string key) => extraConfig?.Items.FirstOrDefault(o => o.Str("key") == key)?.Str("value");
        return Value("vm.genid") is { Length: > 0 } low && Value("vm.genidX") is { Length: > 0 } high ? $"{low}:{high}" : null;
    }

    /// <summary>
    /// The NICs: device key, MAC, network (a standard network's managed object id, or a distributed port group's key,
    /// which is its managed object id too) and the network's name where the backing names it.
    /// </summary>
    public static IReadOnlyList<VmNic> Nics(VimObject? devices) =>
    [
        .. (devices?.Items ?? []).Where(d => NicTypes.Contains(d.Type)).Select(nic =>
        {
            var backing = nic.Child("backing");
            var (id, name) = backing?.Type switch
            {
                "VirtualEthernetCardNetworkBackingInfo" => (backing.Ref("network")?.Value ?? string.Empty, backing.Str("deviceName") ?? string.Empty),
                "VirtualEthernetCardDistributedVirtualPortBackingInfo" => (backing.Child("port")?.Str("portgroupKey") ?? string.Empty, backing.Child("port")?.Str("portgroupKey") ?? string.Empty),
                "VirtualEthernetCardOpaqueNetworkBackingInfo" => (backing.Str("opaqueNetworkId") ?? string.Empty, backing.Str("opaqueNetworkId") ?? string.Empty),
                _ => (string.Empty, string.Empty),
            };
            return new VmNic((nic.Int("key") ?? 0).ToString(CultureInfo.InvariantCulture), nic.Str("macAddress") ?? string.Empty, id, name, 1500);
        }),
    ];
}
