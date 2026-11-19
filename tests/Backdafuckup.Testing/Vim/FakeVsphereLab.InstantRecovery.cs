using System.Globalization;
using System.Xml.Linq;

namespace Backdafuckup.Testing.Vim;

/// <summary>
/// What an instant recovery uses: the host's VMkernel NICs and NFS.MaxVolumes, NFS datastores (CreateNasDatastore,
/// RemoveDatastore), VMs registered around files already on them, Storage vMotion (RelocateVM_Task) and UnregisterVM.
/// </summary>
public sealed partial class FakeVsphereLab
{
    private readonly Dictionary<string, (string RemoteHost, string RemotePath)> _nas = new(StringComparer.Ordinal);

    /// <summary>The host's VMkernel addresses.</summary>
    public IReadOnlyList<string> VmkernelAddresses { get; private set; } = [];

    /// <summary>NFS.MaxVolumes of the host.</summary>
    public int NfsMaxVolumes { get; set; } = 8;

    /// <summary>CreateNasDatastore fails with this message (as when the host cannot reach the NFS server).</summary>
    public string? NasMountFails { get; set; }

    /// <summary>The NFS datastores mounted now, by name.</summary>
    public IReadOnlyDictionary<string, (string Type, string Value)> NasDatastores
    {
        get
        {
            lock (_lock)
            {
                return _nas.Keys.Select(id => ("Datastore", id)).ToDictionary(d => Vcenter.Get(d, "summary.name")!.Value, d => d, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>The VM's files as its layout lists them (name, type).</summary>
    public IReadOnlyList<(string Name, string Type)> VmFiles((string Type, string Value) vm) =>
        [.. (Vcenter.Get(vm, "layoutEx.file")?.Elements() ?? []).Select(f => (f.Element(FakeVcenter.Ns + "name")!.Value, f.Element(FakeVcenter.Ns + "type")!.Value))];

    /// <summary>Adds a disk on another datastore to the VM, as an administrator could by hand.</summary>
    public void AddForeignDisk((string Type, string Value) vm, string fileName, long capacity)
    {
        lock (_lock)
        {
            _devices[vm.Value].Add(Disk(_nextKey++, 1000, 9, fileName, capacity, $"6000C29f-{Guid.NewGuid().ToString()[9..]}", null));
            PublishDevicesLocked(vm);
        }

        UpdateLayout(vm);
    }

    private void AddInstantRecoverySupport()
    {
        VmkernelAddresses = ["192.0.2.21", "198.51.100.21"];
        Vcenter.Set(Host, "config.network.vnic", Val.Array("ArrayOfHostVirtualNic", VmkernelAddresses.Select((address, i) => Val.Obj("HostVirtualNic", "HostVirtualNic",
            Val.F("device", $"vmk{i}"), Val.F("portgroup", i == 0 ? "Management Network" : "Storage"),
            Val.F("spec", new XElement(FakeVcenter.Ns + "ip", Val.F("dhcp", "false"), Val.F("ipAddress", address), Val.F("subnetMask", "255.255.255.0")))))));
        var options = Vcenter.Add("OptionManager", "EsxHostAdvSettings-" + Host.Value);
        var system = Vcenter.Add("HostDatastoreSystem", "datastoreSystem-" + Host.Value);
        Vcenter.Set(Host, "configManager.advancedOption", Val.Mor(options.Type, options.Value));
        Vcenter.Set(Host, "configManager.datastoreSystem", Val.Mor(system.Type, system.Value));
        Vcenter.Set(Datastore, "host", MountedOn(Host));

        Vcenter.On("QueryOptions", request =>
        {
            var name = request.Element(FakeVcenter.Ns + "name")!.Value;
            return name == "NFS.MaxVolumes"
                ? Val.Obj("returnval", "OptionValue", Val.F("key", name), Val.Typed("value", "xsd:int", NfsMaxVolumes.ToString(CultureInfo.InvariantCulture)))
                : throw new VimFault("InvalidName", $"Invalid name: {name}");
        });
        Vcenter.On("CreateNasDatastore", request =>
        {
            var spec = request.Element(FakeVcenter.Ns + "spec")!;
            string Text(string field) => spec.Element(FakeVcenter.Ns + field)?.Value ?? string.Empty;
            if (Text("type") != "NFS" || Text("accessMode") != "readWrite")
            {
                throw new VimFault("InvalidArgument", "An instant recovery datastore is NFS 3 and read-write.");
            }

            if (NasMountFails is { } failure)
            {
                throw new VimFault("HostConfigFault", failure);
            }

            if (Vcenter.Objects("Datastore").Any(d => Vcenter.Get(d, "summary.name")?.Value == Text("localPath")))
            {
                throw new VimFault("DuplicateName", $"The name '{Text("localPath")}' already exists.");
            }

            var datastore = Vcenter.AddDatastore(Text("localPath"), 4L << 40, 2L << 40, shared: false, type: "NFS");
            Vcenter.Set(datastore, "info", Val.Obj("val", "NasDatastoreInfo", Val.F("name", Text("localPath")),
                new XElement(FakeVcenter.Ns + "nas", Val.F("name", Text("localPath")), Val.F("capacity", 4L << 40), Val.F("remoteHost", Text("remoteHost")),
                    Val.F("remotePath", Text("remotePath")), Val.F("type", "NFS"))));
            Vcenter.Set(datastore, "host", MountedOn(Host));
            lock (_lock)
            {
                _nas[datastore.Value] = (Text("remoteHost"), Text("remotePath"));
                Events.Add($"mount {Text("remoteHost")}:{Text("remotePath")} as {Text("localPath")}");
            }

            SetHostDatastores();
            RefreshDatastoreVms();
            return Val.Mor("returnval", datastore);
        });
        Vcenter.On("RemoveDatastore", request =>
        {
            var datastore = FakeVcenter.Ref(request.Element(FakeVcenter.Ns + "datastore")!);
            if (!Vcenter.Exists(datastore))
            {
                throw new VimFault("NotFound", "The object or item referred to could not be found.");
            }

            if (Vcenter.Get(datastore, "vm")?.Elements().Any() == true)
            {
                throw new VimFault("ResourceInUse", $"The resource '{Vcenter.Get(datastore, "summary.name")?.Value}' is in use.");
            }

            lock (_lock)
            {
                _nas.Remove(datastore.Value);
                Events.Add($"unmount {Vcenter.Get(datastore, "summary.name")?.Value}");
            }

            Vcenter.Remove(datastore);
            SetHostDatastores();
            return null;
        });
        Vcenter.On("UnregisterVM", request =>
        {
            var vm = FakeVcenter.Ref(request.Element(FakeVcenter.Ns + "_this")!);
            if (Vcenter.Get(vm, "runtime.powerState")?.Value != "poweredOff")
            {
                throw new VimFault("InvalidPowerState", "The attempted operation cannot be performed in the current state (Powered on).");
            }

            lock (_lock)
            {
                _devices.Remove(vm.Value);
                Events.Add($"unregister {Vcenter.Name(vm)}");
            }

            Vcenter.Remove(vm);
            RefreshDatastoreVms();
            return null;
        });
        Vcenter.On("RelocateVM_Task", request =>
        {
            var vm = FakeVcenter.Ref(request.Element(FakeVcenter.Ns + "_this")!);
            var spec = request.Element(FakeVcenter.Ns + "spec")!;
            return Vcenter.StartTask(vm, "VirtualMachine.relocate", () =>
            {
                Relocate(vm, spec);
                return null;
            });
        });
    }

    private XElement MountedOn((string Type, string Value) host) =>
        Val.Array("ArrayOfDatastoreHostMount", [Val.Obj("DatastoreHostMount", "DatastoreHostMount", Val.Mor("key", host),
            Val.F("mountInfo", new[] { Val.F("accessMode", "readWrite"), Val.F("mounted", "true"), Val.F("accessible", "true") }))]);

    private void SetHostDatastores()
    {
        List<(string Type, string Value)> all;
        lock (_lock)
        {
            all = [Datastore, .. _nas.Keys.Select(id => ("Datastore", id))];
        }

        Vcenter.Set(Host, "datastore", Val.Array("ArrayOfManagedObjectReference", all.Select(d => Val.Mor("ManagedObjectReference", d))));
        Vcenter.Set(Datacenter, "datastore", Val.Array("ArrayOfManagedObjectReference", all.Select(d => Val.Mor("ManagedObjectReference", d))));
    }

    /// <summary>A disk file on an NFS datastore of the fake: its capacity as the VM's device says, a new UUID; null if the file is elsewhere.</summary>
    private (long Capacity, string Uuid)? NasDisk(string fileName, XElement device)
    {
        lock (_lock)
        {
            var onNas = _nas.Keys.Any(id => fileName.StartsWith($"[{Vcenter.Get(("Datastore", id), "summary.name")?.Value}] ", StringComparison.Ordinal));
            return onNas ? (long.Parse(device.Element(FakeVcenter.Ns + "capacityInKB")!.Value, CultureInfo.InvariantCulture) * 1024, $"6000C29e-{Guid.NewGuid().ToString()[9..]}") : null;
        }
    }

    private void OnVmCreated((string Type, string Value) vm, XElement config)
    {
        var vmx = config.Element(FakeVcenter.Ns + "files")!.Element(FakeVcenter.Ns + "vmPathName")!.Value;
        Vcenter.Set(vm, "config.swapPlacement", Val.Str(config.Element(FakeVcenter.Ns + "swapPlacement")?.Value ?? "inherit"));
        Vcenter.Set(vm, "config.annotation", Val.Str(config.Element(FakeVcenter.Ns + "annotation")?.Value ?? string.Empty));
        SetFiles(vm, vmx);
        UpdateLayout(vm);
        RefreshDatastoreVms();
    }

    private void SetFiles((string Type, string Value) vm, string vmx)
    {
        var directory = vmx[..(vmx.LastIndexOf('/') + 1)];
        Vcenter.Set(vm, "config.files", Val.Obj("val", "VirtualMachineFileInfo", Val.F("vmPathName", vmx), Val.F("snapshotDirectory", directory),
            Val.F("suspendDirectory", directory), Val.F("logDirectory", directory)));
        Vcenter.Set(vm, "summary.config.vmPathName", Val.Str(vmx));
    }

    /// <summary>layoutEx.file: the .vmx, nvram and log next to it, the disks' descriptors and data files, the swap file (on the host's local datastore if asked).</summary>
    private void UpdateLayout((string Type, string Value) vm)
    {
        var vmx = Vcenter.Get(vm, "config.files")!.Element(FakeVcenter.Ns + "vmPathName")!.Value;
        var directory = vmx[..(vmx.LastIndexOf('/') + 1)];
        var name = Path.GetFileNameWithoutExtension(vmx[(vmx.LastIndexOf('/') + 1)..]);
        var files = new List<(string Name, string Type)> { (vmx, "config"), (directory + name + ".nvram", "nvram"), (directory + "vmware.log", "log") };
        lock (_lock)
        {
            foreach (var disk in _devices[vm.Value].Where(d => d.Attribute(FakeVcenter.Xsi + "type")?.Value == "VirtualDisk"))
            {
                var file = disk.Element(FakeVcenter.Ns + "backing")!.Element(FakeVcenter.Ns + "fileName")!.Value;
                files.Add((file, "diskDescriptor"));
                files.Add((file[..^5] + "-flat.vmdk", "diskExtent"));
            }
        }

        files.Add(Vcenter.Get(vm, "config.swapPlacement")?.Value == "hostLocal"
            ? ($"[esx01-local] {name}-{vm.Value}.vswp", "swap")
            : (directory + name + ".vswp", "swap"));
        Vcenter.Set(vm, "layoutEx.file", Val.Array("ArrayOfVirtualMachineFileLayoutExFileInfo", files.Select((f, i) => Val.Obj("VirtualMachineFileLayoutExFileInfo", "VirtualMachineFileLayoutExFileInfo",
            Val.F("key", i), Val.F("name", f.Name), Val.F("type", f.Type), Val.F("size", 0)))));
    }

    /// <summary>Each NFS datastore's "vm": the VMs with a file on it.</summary>
    private void RefreshDatastoreVms()
    {
        List<string> nas;
        lock (_lock)
        {
            nas = [.. _nas.Keys];
        }

        foreach (var id in nas)
        {
            var prefix = $"[{Vcenter.Get(("Datastore", id), "summary.name")?.Value}] ";
            var vms = Vcenter.Objects("VirtualMachine").Where(vm => VmFiles(vm).Any(f => f.Name.StartsWith(prefix, StringComparison.Ordinal)));
            Vcenter.Set(("Datastore", id), "vm", Val.Array("ArrayOfManagedObjectReference", vms.Select(v => Val.Mor("ManagedObjectReference", v))));
        }
    }

    /// <summary>Storage vMotion: every file to the target datastore, into a folder named after the VM; the disks thin or thick as the locators ask.</summary>
    private void Relocate((string Type, string Value) vm, XElement spec)
    {
        var target = FakeVcenter.Ref(spec.Element(FakeVcenter.Ns + "datastore")!);
        if (!Vcenter.Exists(target))
        {
            throw new VimFault("ManagedObjectNotFound", "The object has already been deleted or has not been completely created");
        }

        var hostDatastores = Vcenter.Get(Host, "datastore")!.Elements().Select(e => e.Value).ToHashSet(StringComparer.Ordinal);
        if (!hostDatastores.Contains(target.Value))
        {
            throw new VimFault("InvalidDatastore", $"The datastore {Vcenter.Get(target, "summary.name")?.Value} is not accessible from the host.");
        }

        var name = Vcenter.Name(vm)!;
        var targetName = Vcenter.Get(target, "summary.name")!.Value;
        var locators = spec.Elements(FakeVcenter.Ns + "disk").ToDictionary(d => d.Element(FakeVcenter.Ns + "diskId")!.Value, d => d, StringComparer.Ordinal);
        lock (_lock)
        {
            var index = 0;
            foreach (var disk in _devices[vm.Value].Where(d => d.Attribute(FakeVcenter.Xsi + "type")?.Value == "VirtualDisk"))
            {
                var backing = disk.Element(FakeVcenter.Ns + "backing")!;
                backing.Element(FakeVcenter.Ns + "fileName")!.Value = $"[{targetName}] {name}/{name}{(index == 0 ? string.Empty : "_" + index.ToString(CultureInfo.InvariantCulture))}.vmdk";
                backing.Element(FakeVcenter.Ns + "datastore")!.Value = target.Value;
                if (locators.TryGetValue(disk.Element(FakeVcenter.Ns + "key")!.Value, out var locator)
                    && locator.Element(FakeVcenter.Ns + "diskBackingInfo")?.Element(FakeVcenter.Ns + "thinProvisioned") is { } thin)
                {
                    backing.Element(FakeVcenter.Ns + "thinProvisioned")!.Value = thin.Value;
                }

                index++;
            }

            PublishDevicesLocked(vm);
            Events.Add($"relocate {name} to {targetName}");
        }

        SetFiles(vm, $"[{targetName}] {name}/{name}.vmx");
        UpdateLayout(vm);
        RefreshDatastoreVms();
    }
}
