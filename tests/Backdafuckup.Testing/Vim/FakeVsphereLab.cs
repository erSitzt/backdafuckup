using System.Globalization;
using System.Xml.Linq;
using Backdafuckup.Core.Extents;
using Backdafuckup.Testing.Proxy;

namespace Backdafuckup.Testing.Vim;

/// <summary>
/// A small vSphere for backup tests on top of <see cref="FakeVcenter"/>: one host and datastore, a proxy VM whose
/// hot-added disks appear in <see cref="ProxyDevices"/> (the in-process proxy agent's devices), and VMs whose disks are
/// files. Snapshots freeze the disks' content; CBT records the writes after each snapshot; QueryChangedDiskAreas answers
/// from them ("*": the non-zero 64 KiB blocks). Any <c>fileOperation</c> in a reconfiguration fails the test.
/// </summary>
public sealed class FakeVsphereLab
{
    private const int Block = 64 * 1024;
    private readonly string _root;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DiskState> _disks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<XElement>> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SnapshotState> _snapshots = new(StringComparer.Ordinal);
    private int _nextKey = 3000;
    private int _nextDevice;

    public FakeVsphereLab(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
        Vcenter = new FakeVcenter();
        var dc = Vcenter.AddDatacenter("DC-Lab");
        Cluster = Vcenter.AddCluster(dc, "Cluster-A");
        Host = Vcenter.AddHost(Cluster, "esx01.corp.example");
        Datastore = Vcenter.AddDatastore("ds-shared-01", 1L << 40, 600L << 30);
        Vcenter.Set(Host, "datastore", Val.Array("ArrayOfManagedObjectReference", [Val.Mor("ManagedObjectReference", Datastore)]));
        Network = Vcenter.AddNetwork("VM Network");

        ProxyVm = Vcenter.Add("VirtualMachine", Vcenter.NewId("vm"),
            ("name", Val.Str("proxy01")), ("config.instanceUuid", Val.Str(Guid.NewGuid().ToString())), ("config.uuid", Val.Str(ProxySmbiosUuid)),
            ("config.template", Val.Bool(false)), ("runtime.powerState", Val.Str("poweredOn")), ("runtime.host", Val.Mor(Host.Type, Host.Value)),
            ("config.extraConfig", Val.Array("ArrayOfOptionValue", [Val.Obj("OptionValue", "OptionValue", Val.F("key", "disk.EnableUUID"), Val.F("value", "TRUE"))])));
        _devices[ProxyVm.Value] =
        [
            Controller(1000, 0),
            Controller(1001, 1),
            Disk(2000, 1000, 0, "[ds-shared-01] proxy01/proxy01.vmdk", 10L << 30, "6000C29a-0000-0000-0000-00000000aaaa", null),
        ];
        PublishDevices(ProxyVm);

        Vcenter.On("FindAllByUuid", request =>
        {
            var path = request.Element(FakeVcenter.Ns + "instanceUuid")?.Value == "true" ? "config.instanceUuid" : "config.uuid";
            var uuid = request.Element(FakeVcenter.Ns + "uuid")!.Value;
            var match = Vcenter.Objects("VirtualMachine").FirstOrDefault(vm => string.Equals(Vcenter.Get(vm, path)?.Value, uuid, StringComparison.OrdinalIgnoreCase));
            return match == default ? null : Val.Mor("returnval", match);
        });
        Vcenter.On("ReconfigVM_Task", Reconfigure);
        Vcenter.On("CreateSnapshot_Task", CreateSnapshot);
        Vcenter.On("RemoveSnapshot_Task", RemoveSnapshot);
        Vcenter.On("QueryChangedDiskAreas", QueryChangedDiskAreas);
    }

    public FakeVcenter Vcenter { get; }

    /// <summary>The in-process proxy agent's devices: hot-added disks appear here (after a rescan).</summary>
    public FileBlockDevices ProxyDevices { get; } = new();

    public string ProxySmbiosUuid { get; } = "420bba8e-6f4d-f008-67cd-a1a7c07d7961";

    public (string Type, string Value) Cluster { get; }

    public (string Type, string Value) Host { get; }

    public (string Type, string Value) Datastore { get; }

    public (string Type, string Value) Network { get; }

    public (string Type, string Value) ProxyVm { get; }

    /// <summary>Snapshots taken and removed, and HotAdd attaches and detaches, in order (for assertions).</summary>
    public List<string> Events { get; } = [];

    /// <summary>A VM with disks of the given sizes (zeros); returns its reference, instance UUID and the disks' UUIDs.</summary>
    public ((string Type, string Value) Ref, string InstanceUuid, IReadOnlyList<string> DiskUuids) AddVm(string name, params long[] diskSizes)
    {
        ArgumentNullException.ThrowIfNull(diskSizes);
        var disks = new List<FakeVmDisk>();
        var uuids = new List<string>();
        for (var i = 0; i < diskSizes.Length; i++)
        {
            var uuid = $"6000C29{i}-{Guid.NewGuid().ToString()[9..]}";
            var fileName = $"[ds-shared-01] {name}/{name}{(i == 0 ? string.Empty : $"_{i}")}.vmdk";
            var file = Path.Combine(_root, $"{name}-{i}.img");
            using (var stream = File.Create(file))
            {
                stream.SetLength(diskSizes[i]);
            }

            lock (_lock)
            {
                _disks[uuid] = new DiskState(uuid, fileName, file, diskSizes[i]);
            }

            disks.Add(new FakeVmDisk(fileName, diskSizes[i], Datastore, uuid, Key: 2000 + i, Unit: i));
            uuids.Add(uuid);
        }

        var (vm, instanceUuid) = Vcenter.AddVm(name, Host, disks);
        lock (_lock)
        {
            _devices[vm.Value] = [Controller(1000, 0), .. disks.Select(d => Disk(d.Key, 1000, d.Unit, d.FileName, d.CapacityBytes, d.Uuid, null))];
        }

        PublishDevices(vm);
        return (vm, instanceUuid, uuids);
    }

    /// <summary>The guest writes: the disk's file changes, and CBT records the blocks if it is on.</summary>
    public void Write(string diskUuid, long offset, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_lock)
        {
            var disk = _disks[diskUuid];
            using (var stream = new FileStream(disk.File, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                stream.Position = offset;
                stream.Write(data);
            }

            if (disk.Tracking)
            {
                disk.Writes.Add((disk.Sequence, new DiskExtent(offset, data.Length)));
            }
        }
    }

    public byte[] Content(string diskUuid)
    {
        lock (_lock)
        {
            return File.ReadAllBytes(_disks[diskUuid].File);
        }
    }

    /// <summary>Starts a new change-tracking epoch for the disk (as after CBT was switched off and on).</summary>
    public void ResetChangeTracking(string diskUuid)
    {
        lock (_lock)
        {
            var disk = _disks[diskUuid];
            disk.Epoch = NewEpoch();
            disk.Sequence = 0;
            disk.Writes.Clear();
        }
    }

    public IReadOnlyList<(string Type, string Value)> Snapshots()
    {
        lock (_lock)
        {
            return [.. _snapshots.Keys.Select(k => ("VirtualMachineSnapshot", k))];
        }
    }

    private XElement? Reconfigure(XElement request)
    {
        var vm = FakeVcenter.Ref(request.Element(FakeVcenter.Ns + "_this")!);
        var spec = request.Element(FakeVcenter.Ns + "spec")!;
        if (spec.Descendants(FakeVcenter.Ns + "fileOperation").Any())
        {
            throw new VimFault("InvalidArgument", "The fake vSphere refuses fileOperation: a backup must never create or destroy disk files.");
        }

        return Vcenter.StartTask(vm, "VirtualMachine.reconfigure", () =>
        {
            if (spec.Element(FakeVcenter.Ns + "changeTrackingEnabled")?.Value == "true")
            {
                Vcenter.Set(vm, "config.changeTrackingEnabled", Val.Bool(true));
                lock (_lock)
                {
                    foreach (var disk in DisksOf(vm).Where(d => !d.Tracking))
                    {
                        disk.Tracking = true;
                        disk.Epoch = NewEpoch();
                        disk.Sequence = 0;
                    }
                }
            }

            foreach (var change in spec.Elements(FakeVcenter.Ns + "deviceChange"))
            {
                var device = change.Element(FakeVcenter.Ns + "device")!;
                var key = int.Parse(device.Element(FakeVcenter.Ns + "key")!.Value, CultureInfo.InvariantCulture);
                switch (change.Element(FakeVcenter.Ns + "operation")!.Value)
                {
                    case "add":
                        HotAdd(vm, device);
                        break;
                    case "remove":
                        lock (_lock)
                        {
                            var removed = _devices[vm.Value].Single(d => d.Element(FakeVcenter.Ns + "key")!.Value == key.ToString(CultureInfo.InvariantCulture));
                            _devices[vm.Value].Remove(removed);
                            Events.Add($"detach {removed.Element(FakeVcenter.Ns + "backing")!.Element(FakeVcenter.Ns + "fileName")!.Value} from {Vcenter.Name(vm)}");
                        }

                        PublishDevices(vm);
                        break;
                }
            }

            return null;
        });
    }

    private void HotAdd((string Type, string Value) vm, XElement device)
    {
        var fileName = device.Element(FakeVcenter.Ns + "backing")!.Element(FakeVcenter.Ns + "fileName")!.Value;
        var mode = device.Element(FakeVcenter.Ns + "backing")!.Element(FakeVcenter.Ns + "diskMode")?.Value;
        var controller = int.Parse(device.Element(FakeVcenter.Ns + "controllerKey")!.Value, CultureInfo.InvariantCulture);
        var unit = int.Parse(device.Element(FakeVcenter.Ns + "unitNumber")!.Value, CultureInfo.InvariantCulture);
        lock (_lock)
        {
            // The base disk of a snapshot: its content as it was when the snapshot was taken.
            var (disk, frozen) = _snapshots.Values.SelectMany(s => s.Disks.Select(d => (Disk: _disks[d.Key], Frozen: d.Value.File)))
                .LastOrDefault(d => d.Disk.FileName == fileName);
            if (disk is null)
            {
                throw new VimFault("FileNotFound", $"File {fileName} was not found (the fake only hot-adds the base disks of snapshots).");
            }

            if (mode != "independent_nonpersistent")
            {
                throw new VimFault("InvalidArgument", $"Hot-added disks must be independent_nonpersistent, not {mode}.");
            }

            _devices[vm.Value].Add(Disk(_nextKey++, controller, unit, fileName, disk.Capacity, disk.Uuid, null));
            ProxyDevices.Attach($"sd{(char)('b' + _nextDevice++)}", frozen, disk.Uuid);
            Events.Add($"attach {fileName} to {Vcenter.Name(vm)} at {controller}:{unit}");
        }

        PublishDevices(vm);
    }

    private XElement CreateSnapshot(XElement request)
    {
        var vm = FakeVcenter.Ref(request.Element(FakeVcenter.Ns + "_this")!);
        var name = request.Element(FakeVcenter.Ns + "name")!.Value;
        var description = request.Element(FakeVcenter.Ns + "description")?.Value ?? string.Empty;
        return Vcenter.StartTask(vm, "VirtualMachine.createSnapshot", () =>
        {
            var id = Vcenter.NewId("snapshot");
            var state = new SnapshotState(vm);
            var devices = new List<XElement>();
            lock (_lock)
            {
                foreach (var device in _devices[vm.Value])
                {
                    var copy = new XElement(device);
                    if (device.Attribute(FakeVcenter.Xsi + "type")?.Value == "VirtualDisk")
                    {
                        var uuid = device.Element(FakeVcenter.Ns + "backing")!.Element(FakeVcenter.Ns + "uuid")!.Value;
                        var disk = _disks[uuid];
                        string? changeId = null;
                        if (disk.Tracking)
                        {
                            disk.Sequence++;
                            changeId = $"{disk.Epoch}/{disk.Sequence.ToString(CultureInfo.InvariantCulture)}";
                        }

                        var frozen = Path.Combine(_root, $"{id}-{uuid}.img");
                        File.Copy(disk.File, frozen);
                        state.Disks[uuid] = (frozen, disk.Sequence, changeId);
                        copy.Element(FakeVcenter.Ns + "backing")!.Add(changeId is null ? null : new XElement(FakeVcenter.Ns + "changeId", changeId));
                    }

                    devices.Add(copy);
                }

                _snapshots[id] = state;
                Events.Add($"snapshot {name} of {Vcenter.Name(vm)}");
            }

            var snapshot = Vcenter.Add("VirtualMachineSnapshot", id, ("config.hardware.device", Val.Array("ArrayOfVirtualDevice", devices)));
            Vcenter.SetSnapshots(vm, (name, description, DateTimeOffset.UtcNow));
            return Val.Mor("val", snapshot);
        });
    }

    private XElement RemoveSnapshot(XElement request)
    {
        var snapshot = FakeVcenter.Ref(request.Element(FakeVcenter.Ns + "_this")!);
        SnapshotState state;
        lock (_lock)
        {
            if (!_snapshots.Remove(snapshot.Value, out state!))
            {
                throw new VimFault("ManagedObjectNotFound", $"The object 'vim.vm.Snapshot:{snapshot.Value}' has already been deleted or has not been completely created");
            }

            Events.Add($"remove snapshot of {Vcenter.Name(state.Vm)}");
        }

        return Vcenter.StartTask(state.Vm, "VirtualMachine.removeSnapshot", () =>
        {
            foreach (var (file, _, _) in state.Disks.Values)
            {
                File.Delete(file);
            }

            Vcenter.Remove(snapshot);
            Vcenter.SetSnapshots(state.Vm);
            return null;
        });
    }

    private XElement QueryChangedDiskAreas(XElement request)
    {
        var snapshot = FakeVcenter.Ref(request.Element(FakeVcenter.Ns + "snapshot")!);
        var vm = FakeVcenter.Ref(request.Element(FakeVcenter.Ns + "_this")!);
        var deviceKey = request.Element(FakeVcenter.Ns + "deviceKey")!.Value;
        var changeId = request.Element(FakeVcenter.Ns + "changeId")!.Value;
        lock (_lock)
        {
            var state = _snapshots[snapshot.Value];
            var device = _devices[vm.Value].Single(d => d.Element(FakeVcenter.Ns + "key")!.Value == deviceKey);
            var uuid = device.Element(FakeVcenter.Ns + "backing")!.Element(FakeVcenter.Ns + "uuid")!.Value;
            var disk = _disks[uuid];
            var (frozen, sequence, _) = state.Disks[uuid];
            List<DiskExtent> areas;
            if (changeId == "*")
            {
                var content = File.ReadAllBytes(frozen);
                areas = [];
                for (var offset = 0; offset < content.Length; offset += Block)
                {
                    var length = Math.Min(Block, content.Length - offset);
                    if (content.AsSpan(offset, length).IndexOfAnyExcept((byte)0) >= 0)
                    {
                        areas.Add(new DiskExtent(offset, length));
                    }
                }
            }
            else
            {
                var parts = changeId.Split('/');
                if (parts[0] != disk.Epoch)
                {
                    throw new VimFault("FileFault", $"Error caused by file {disk.FileName}: the change id {changeId} is not valid for this disk.");
                }

                var since = long.Parse(parts[1], CultureInfo.InvariantCulture);
                areas = [.. disk.Writes.Where(w => w.Sequence >= since && w.Sequence < sequence).Select(w => w.Extent)];
            }

            return Val.Obj("returnval", "DiskChangeInfo",
                Val.F("startOffset", 0), Val.F("length", disk.Capacity),
                ExtentMath.Normalize(areas).Select(a => Val.Obj("changedArea", "DiskChangeExtent", Val.F("start", a.Offset), Val.F("length", a.Length))));
        }
    }

    private List<DiskState> DisksOf((string Type, string Value) vm) =>
        [.. _devices[vm.Value].Where(d => d.Attribute(FakeVcenter.Xsi + "type")?.Value == "VirtualDisk")
            .Select(d => d.Element(FakeVcenter.Ns + "backing")!.Element(FakeVcenter.Ns + "uuid")!.Value)
            .Where(_disks.ContainsKey).Select(u => _disks[u])];

    private void PublishDevices((string Type, string Value) vm)
    {
        lock (_lock)
        {
            Vcenter.Set(vm, "config.hardware.device", Val.Array("ArrayOfVirtualDevice", _devices[vm.Value].Select(d => new XElement(d))));
        }
    }

    private static XElement Controller(int key, int bus) =>
        Val.Obj("VirtualDevice", "ParaVirtualSCSIController", Val.F("key", key), Val.F("deviceInfo", new XElement(FakeVcenter.Ns + "label", $"SCSI controller {bus}")),
            Val.F("controllerKey", 100), Val.F("unitNumber", 3 + bus), Val.F("busNumber", bus), Val.F("scsiCtlrUnitNumber", 7));

    private static XElement Disk(int key, int controller, int unit, string fileName, long capacity, string uuid, string? changeId) =>
        Val.Obj("VirtualDevice", "VirtualDisk",
            Val.F("key", key),
            Val.F("deviceInfo", new XElement(FakeVcenter.Ns + "label", $"Hard disk {unit + 1}")),
            Val.Obj("backing", "VirtualDiskFlatVer2BackingInfo", Val.F("fileName", fileName), Val.F("diskMode", "persistent"), Val.F("thinProvisioned", "true"), Val.F("uuid", uuid),
                changeId is null ? null : Val.F("changeId", changeId)),
            Val.F("controllerKey", controller), Val.F("unitNumber", unit), Val.F("capacityInKB", capacity / 1024), Val.F("capacityInBytes", capacity));

    private static string NewEpoch()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        return $"{string.Join(' ', bytes[..8].Select(b => b.ToString("x2", CultureInfo.InvariantCulture)))}-{string.Join(' ', bytes[8..].Select(b => b.ToString("x2", CultureInfo.InvariantCulture)))}";
    }

    private sealed class DiskState(string uuid, string fileName, string file, long capacity)
    {
        public string Uuid { get; } = uuid;

        public string FileName { get; } = fileName;

        public string File { get; } = file;

        public long Capacity { get; } = capacity;

        public bool Tracking { get; set; }

        public string Epoch { get; set; } = string.Empty;

        public long Sequence { get; set; }

        public List<(long Sequence, DiskExtent Extent)> Writes { get; } = [];
    }

    private sealed class SnapshotState((string Type, string Value) vm)
    {
        public (string Type, string Value) Vm { get; } = vm;

        public Dictionary<string, (string File, long Sequence, string? ChangeId)> Disks { get; } = new(StringComparer.Ordinal);
    }
}
