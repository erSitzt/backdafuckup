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
public sealed partial class FakeVsphereLab
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
        using (var key = System.Security.Cryptography.ECDsa.Create())
        {
            using var certificate = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=esx01.corp.example", key, System.Security.Cryptography.HashAlgorithmName.SHA256)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            Host = Vcenter.AddHost(Cluster, "esx01.corp.example", certificate.ExportCertificatePem());
        }

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
        Vcenter.On("RevertToSnapshot_Task", request =>
        {
            var snapshot = FakeVcenter.Ref(request.Element(FakeVcenter.Ns + "_this")!);
            return Vcenter.StartTask(snapshot, "VirtualMachineSnapshot.revert", () =>
            {
                lock (_lock)
                {
                    var state = _snapshots[snapshot.Value];
                    foreach (var (uuid, (frozen, _, _)) in state.Disks)
                    {
                        File.Copy(frozen, _disks[uuid].File, overwrite: true);
                    }

                    Events.Add($"revert {Vcenter.Name(state.Vm)}");
                    Vcenter.Set(state.Vm, "runtime.powerState", Val.Str("poweredOff"));
                }

                return null;
            });
        });
        Vcenter.On("QueryChangedDiskAreas", QueryChangedDiskAreas);

        AddRestoreSupport();
        AddInstantRecoverySupport();

        // VMware Tools guest operations: files arrive with a PUT to the ESXi host, programs are the simulated sc.exe.
        Vcenter.Add("GuestOperationsManager", "guestOperationsManager",
            ("fileManager", Val.Mor("GuestFileManager", "guestFileManager")), ("processManager", Val.Mor("GuestProcessManager", "guestProcessManager")));
        Vcenter.On("MakeDirectoryInGuest", request =>
        {
            var vm = GuestVm(request);
            lock (_lock)
            {
                GuestDirectories.Add($"{Vcenter.Name(vm)}:{request.Element(FakeVcenter.Ns + "directoryPath")!.Value}");
            }

            return null;
        });
        Vcenter.On("InitiateFileTransferToGuest", request =>
        {
            var vm = GuestVm(request);
            var id = Interlocked.Increment(ref _nextTransfer).ToString(System.Globalization.CultureInfo.InvariantCulture);
            lock (_lock)
            {
                _transfers[id] = $"{Vcenter.Name(vm)}:{request.Element(FakeVcenter.Ns + "guestFilePath")!.Value}";
            }

            return Val.Typed("returnval", "xsd:string", $"https://*:443/guestFile?id={id}&token=52a1");
        });
        Vcenter.OnHttp(HttpMethod.Put, "/guestFile", (uri, data) =>
        {
            var id = System.Web.HttpUtility.ParseQueryString(uri.Query)["id"] ?? string.Empty;
            lock (_lock)
            {
                if (uri.Host != "esx01.corp.example" || !_transfers.Remove(id, out var path))
                {
                    return System.Net.HttpStatusCode.NotFound;
                }

                GuestFiles[path] = data;
            }

            return System.Net.HttpStatusCode.OK;
        });
        Vcenter.On("StartProgramInGuest", request =>
        {
            var vm = GuestVm(request);
            var spec = request.Element(FakeVcenter.Ns + "spec")!;
            var program = spec.Element(FakeVcenter.Ns + "programPath")!.Value;
            var arguments = spec.Element(FakeVcenter.Ns + "arguments")?.Value ?? string.Empty;
            var exitCode = RunInGuest(Vcenter.Name(vm)!, program, arguments);
            var pid = Interlocked.Increment(ref _nextPid);
            lock (_lock)
            {
                _exitCodes[pid] = exitCode;
            }

            return Val.Typed("returnval", "xsd:long", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        Vcenter.On("ListProcessesInGuest", request =>
        {
            GuestVm(request);
            var pid = long.Parse(request.Element(FakeVcenter.Ns + "pids")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            int exitCode;
            lock (_lock)
            {
                exitCode = _exitCodes[pid];
            }

            return new XElement(FakeVcenter.Ns + "returnval",
                Val.F("name", "program"), Val.F("pid", pid), Val.F("owner", GuestUser), Val.F("cmdLine", "program"),
                Val.F("startTime", DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
                Val.F("endTime", DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)), Val.F("exitCode", exitCode));
        });
    }

    /// <summary>The guest account VMware Tools accept (a local account without <c>.\</c>).</summary>
    public string GuestUser { get; set; } = @"CORP\backup-admin";

    public string GuestPassword { get; set; } = "secret";

    /// <summary>Files copied into guests, by "vm name:guest path".</summary>
    public Dictionary<string, byte[]> GuestFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> GuestDirectories { get; } = [];

    /// <summary>Programs run in guests, as "vm name: program arguments".</summary>
    public List<string> GuestCommands { get; } = [];

    /// <summary>Windows services in guests by "vm name:service": their state (RUNNING, STOPPED) and command line.</summary>
    public Dictionary<string, (string State, string BinaryPath)> GuestServices { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called when sc.exe starts a service in a guest (VM name, service name).</summary>
    public Action<string, string>? OnServiceStarted { get; set; }

    private readonly Dictionary<string, string> _transfers = new(StringComparer.Ordinal);
    private readonly Dictionary<long, int> _exitCodes = [];
    private int _nextTransfer;
    private long _nextPid = 4000;

    private (string Type, string Value) GuestVm(XElement request)
    {
        var vm = request.Element(FakeVcenter.Ns + "vm")!.Value;
        var auth = request.Element(FakeVcenter.Ns + "auth")!;
        if (auth.Element(FakeVcenter.Ns + "username")?.Value != GuestUser || auth.Element(FakeVcenter.Ns + "password")?.Value != GuestPassword)
        {
            throw new VimFault("InvalidGuestLogin", "Failed to authenticate with the guest operating system using the supplied credentials.");
        }

        return ("VirtualMachine", vm);
    }

    /// <summary>sc.exe and the service query through cmd.exe, as the agent installation uses them.</summary>
    private int RunInGuest(string vm, string program, string arguments)
    {
        lock (_lock)
        {
            GuestCommands.Add($"{vm}: {Path.GetFileName(program.Replace('\\', '/'))} {arguments}");
        }

        var words = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (program.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            var service = words[3];
            lock (_lock)
            {
                return GuestServices.TryGetValue($"{vm}:{service}", out var state) ? state.State switch { "RUNNING" => 0, "STOPPED" => 2, _ => 1 } : 1;
            }
        }

        var key = $"{vm}:{words[1]}";
        string? started = null;
        int result;
        lock (_lock)
        {
            var exists = GuestServices.TryGetValue(key, out var current);
            switch (words[0])
            {
                case "query":
                    result = exists ? 0 : 1060;
                    break;
                case "create":
                    var binPath = arguments[(arguments.IndexOf("binPath= ", StringComparison.Ordinal) + 9)..];
                    GuestServices[key] = ("STOPPED", binPath[..binPath.IndexOf(" start= ", StringComparison.Ordinal)]);
                    result = exists ? 1073 : 0;
                    break;
                case "start":
                    result = exists ? 0 : 1060;
                    if (exists)
                    {
                        GuestServices[key] = ("RUNNING", current.BinaryPath);
                        started = words[1];
                    }

                    break;
                case "stop":
                    result = exists ? 0 : 1060;
                    if (exists)
                    {
                        GuestServices[key] = ("STOPPED", current.BinaryPath);
                    }

                    break;
                case "delete":
                    result = GuestServices.Remove(key) ? 0 : 1060;
                    break;
                default:
                    result = 1;
                    break;
            }
        }

        if (started is not null)
        {
            OnServiceStarted?.Invoke(vm, started);
        }

        return result;
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

    /// <summary>The datacenter, its VM folder, a folder "backdafuckup-test" in it, and the cluster's root resource pool.</summary>
    public (string Type, string Value) Datacenter { get; private set; }

    public (string Type, string Value) VmFolder { get; private set; }

    public (string Type, string Value) TestFolder { get; private set; }

    public (string Type, string Value) ResourcePool { get; private set; }

    /// <summary>CreateVM_Task ends in an error (for a restore that fails after its disks were written).</summary>
    public bool CreateVmFails { get; set; }

    /// <summary>Folders made on datastores ("[ds] name") and the disk files there by name (to their disk's UUID).</summary>
    public HashSet<string> DatastoreFolders { get; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> DiskFiles
    {
        get
        {
            lock (_lock)
            {
                return _disks.Values.ToDictionary(d => d.FileName, d => d.Uuid, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>The VM's disks by key ("scsi0:1") with their file names and UUIDs, as its devices show them.</summary>
    public IReadOnlyList<(int Bus, int Unit, string FileName, string Uuid)> VmDisks((string Type, string Value) vm)
    {
        lock (_lock)
        {
            var controllers = _devices[vm.Value].Where(d => d.Element(FakeVcenter.Ns + "busNumber") is not null)
                .ToDictionary(d => d.Element(FakeVcenter.Ns + "key")!.Value, d => int.Parse(d.Element(FakeVcenter.Ns + "busNumber")!.Value, CultureInfo.InvariantCulture));
            return [.. _devices[vm.Value].Where(d => d.Attribute(FakeVcenter.Xsi + "type")!.Value == "VirtualDisk").Select(d => (
                controllers[d.Element(FakeVcenter.Ns + "controllerKey")!.Value],
                int.Parse(d.Element(FakeVcenter.Ns + "unitNumber")!.Value, CultureInfo.InvariantCulture),
                d.Element(FakeVcenter.Ns + "backing")!.Element(FakeVcenter.Ns + "fileName")!.Value,
                d.Element(FakeVcenter.Ns + "backing")!.Element(FakeVcenter.Ns + "uuid")!.Value))];
        }
    }

    /// <summary>The VM's NICs: device type, MAC, network, PCI slot and whether it starts connected.</summary>
    public IReadOnlyList<(string Type, string Mac, string Network, int? PciSlot, bool StartConnected)> VmNics((string Type, string Value) vm)
    {
        lock (_lock)
        {
            return [.. _devices[vm.Value].Where(d => d.Element(FakeVcenter.Ns + "macAddress") is not null).Select(d => (
                d.Attribute(FakeVcenter.Xsi + "type")!.Value, d.Element(FakeVcenter.Ns + "macAddress")!.Value,
                d.Element(FakeVcenter.Ns + "backing")?.Element(FakeVcenter.Ns + "network")?.Value ?? string.Empty,
                d.Element(FakeVcenter.Ns + "slotInfo")?.Element(FakeVcenter.Ns + "pciSlotNumber") is { } slot ? int.Parse(slot.Value, CultureInfo.InvariantCulture) : (int?)null,
                d.Element(FakeVcenter.Ns + "connectable")?.Element(FakeVcenter.Ns + "startConnected")?.Value != "false"))];
        }
    }

    public (string Type, string Value)? FindVm(string name) =>
        Vcenter.Objects("VirtualMachine").Where(v => Vcenter.Name(v) == name).Select(v => ((string, string)?)v).FirstOrDefault();

    private void AddRestoreSupport()
    {
        Datacenter = Vcenter.Objects("Datacenter").Single();
        VmFolder = FakeVcenter.Ref(Vcenter.Get(Datacenter, "vmFolder")!);
        Vcenter.Set(Datacenter, "datastore", Val.Array("ArrayOfManagedObjectReference", [Val.Mor("ManagedObjectReference", Datastore)]));
        TestFolder = Vcenter.Add("Folder", Vcenter.NewId("group-v"), ("name", Val.Str("backdafuckup-test")), ("parent", Val.Mor(VmFolder.Type, VmFolder.Value)));
        ResourcePool = Vcenter.Add("ResourcePool", Vcenter.NewId("resgroup"), ("name", Val.Str("Resources")), ("parent", Val.Mor(Cluster.Type, Cluster.Value)));
        Vcenter.Set(Cluster, "resourcePool", Val.Mor(ResourcePool.Type, ResourcePool.Value));
        Vcenter.Set(Host, "parent", Val.Mor(Cluster.Type, Cluster.Value));

        Vcenter.On("MakeDirectory", request =>
        {
            var name = request.Element(FakeVcenter.Ns + "name")!.Value;
            if (request.Element(FakeVcenter.Ns + "datacenter") is null)
            {
                throw new VimFault("InvalidArgument", "A datastore path needs the datacenter.");
            }

            lock (_lock)
            {
                if (_disks.Values.Any(d => d.FileName.StartsWith(name + "/", StringComparison.Ordinal)) || !DatastoreFolders.Add(name))
                {
                    throw new VimFault("FileAlreadyExists", $"Cannot complete the operation because the file or folder {name} already exists");
                }
            }

            return null;
        });
        Vcenter.On("CreateVM_Task", request =>
        {
            var folder = FakeVcenter.Ref(request.Element(FakeVcenter.Ns + "_this")!);
            var pool = FakeVcenter.Ref(request.Element(FakeVcenter.Ns + "pool")!);
            var config = request.Element(FakeVcenter.Ns + "config")!;
            return Vcenter.StartTask(folder, "Folder.createVm", () => CreateVm(folder, pool, config));
        });
        Vcenter.On("PowerOnVM_Task", request => PowerTask(request, "poweredOn"));
        Vcenter.On("PowerOffVM_Task", request => PowerTask(request, "poweredOff"));
        Vcenter.On("ShutdownGuest", request =>
        {
            var vm = FakeVcenter.Ref(request.Element(FakeVcenter.Ns + "_this")!);
            Vcenter.Set(vm, "runtime.powerState", Val.Str("poweredOff"));
            return null;
        });
        Vcenter.On("Rename_Task", request =>
        {
            var vm = FakeVcenter.Ref(request.Element(FakeVcenter.Ns + "_this")!);
            var name = request.Element(FakeVcenter.Ns + "newName")!.Value;
            return Vcenter.StartTask(vm, "VirtualMachine.rename", () =>
            {
                Vcenter.Set(vm, "name", Val.Str(name));
                return null;
            });
        });
        Vcenter.On("Destroy_Task", request =>
        {
            var vm = FakeVcenter.Ref(request.Element(FakeVcenter.Ns + "_this")!);
            return Vcenter.StartTask(vm, "VirtualMachine.destroy", () =>
            {
                if (Vcenter.Get(vm, "runtime.powerState")?.Value != "poweredOff")
                {
                    throw new VimFault("InvalidPowerState", "The attempted operation cannot be performed in the current state (Powered on).");
                }

                lock (_lock)
                {
                    foreach (var disk in DisksOf(vm))
                    {
                        _disks.Remove(disk.Uuid);
                        File.Delete(disk.File);
                    }

                    _devices.Remove(vm.Value);
                    Events.Add($"destroy {Vcenter.Name(vm)}");
                }

                Vcenter.Remove(vm);
                return null;
            });
        });
        Vcenter.On("DeleteVirtualDisk_Task", request =>
        {
            var name = request.Element(FakeVcenter.Ns + "name")!.Value;
            return Vcenter.StartTask(("VirtualDiskManager", "virtualDiskManager"), "VirtualDiskManager.deleteVirtualDisk", () =>
            {
                lock (_lock)
                {
                    var disk = _disks.Values.FirstOrDefault(d => d.FileName == name) ?? throw new VimFault("FileNotFound", $"File {name} was not found");
                    if (_devices.Values.Any(devices => devices.Any(d => d.Element(FakeVcenter.Ns + "backing")?.Element(FakeVcenter.Ns + "fileName")?.Value == name)))
                    {
                        throw new VimFault("FileLocked", $"Unable to access file {name} since it is locked");
                    }

                    _disks.Remove(disk.Uuid);
                    File.Delete(disk.File);
                    Events.Add($"delete {name}");
                }

                return null;
            });
        });
    }

    /// <summary>A new VM generation id, as vSphere gives each VM (vm.genid, vm.genidX).</summary>
    private static XElement GenerationIdConfig() => Val.Array("ArrayOfOptionValue",
    [
        Val.Obj("OptionValue", "OptionValue", Val.F("key", "vm.genid"), Val.F("value", Random.Shared.NextInt64().ToString(CultureInfo.InvariantCulture))),
        Val.Obj("OptionValue", "OptionValue", Val.F("key", "vm.genidX"), Val.F("value", Random.Shared.NextInt64().ToString(CultureInfo.InvariantCulture))),
    ]);

    private XElement PowerTask(XElement request, string state)
    {
        var vm = FakeVcenter.Ref(request.Element(FakeVcenter.Ns + "_this")!);
        return Vcenter.StartTask(vm, state == "poweredOn" ? "VirtualMachine.powerOn" : "VirtualMachine.powerOff", () =>
        {
            // As vCenter 7: nothing changes the power of a VM that waits on a question.
            if (Vcenter.Get(vm, "runtime.question") is { } question)
            {
                throw new VimFault("InvalidState",
                    $"The operation cannot be allowed at the current time because the virtual machine has a question pending: '{question.Element(FakeVcenter.Ns + "text")?.Value}'");
            }

            Vcenter.Set(vm, "runtime.powerState", Val.Str(state));
            lock (_lock)
            {
                Events.Add($"{(state == "poweredOn" ? "power on" : "power off")} {Vcenter.Name(vm)}");
            }

            return null;
        });
    }

    /// <summary>A restore's new disk file, created on the proxy VM in an existing datastore folder.</summary>
    private void CreateDisk((string Type, string Value) vm, XElement device)
    {
        var backing = device.Element(FakeVcenter.Ns + "backing")!;
        var fileName = backing.Element(FakeVcenter.Ns + "fileName")!.Value;
        var controller = int.Parse(device.Element(FakeVcenter.Ns + "controllerKey")!.Value, CultureInfo.InvariantCulture);
        var unit = int.Parse(device.Element(FakeVcenter.Ns + "unitNumber")!.Value, CultureInfo.InvariantCulture);
        var capacity = long.Parse(device.Element(FakeVcenter.Ns + "capacityInKB")!.Value, CultureInfo.InvariantCulture) * 1024;
        if (backing.Element(FakeVcenter.Ns + "diskMode")?.Value != "independent_persistent")
        {
            throw new VimFault("InvalidArgument", "A restore's disk on the proxy must be independent_persistent.");
        }

        lock (_lock)
        {
            if (!DatastoreFolders.Contains(fileName[..fileName.LastIndexOf('/')]))
            {
                throw new VimFault("FileNotFound", $"File {fileName[..fileName.LastIndexOf('/')]} was not found");
            }

            if (_disks.Values.Any(d => d.FileName == fileName))
            {
                throw new VimFault("FileAlreadyExists", $"Cannot complete the operation because the file or folder {fileName} already exists");
            }

            var uuid = $"6000C29f-{Guid.NewGuid().ToString()[9..]}";
            var file = Path.Combine(_root, $"created-{_nextKey}.img");
            using (var stream = File.Create(file))
            {
                stream.SetLength(capacity);
            }

            _disks[uuid] = new DiskState(uuid, fileName, file, capacity);
            _devices[vm.Value].Add(Disk(_nextKey++, controller, unit, fileName, capacity, uuid, null));
            ProxyDevices.Attach($"sd{(char)('b' + _nextDevice++)}", file, uuid);
            Events.Add($"create {fileName} on {Vcenter.Name(vm)} at {controller}:{unit}");
        }

        PublishDevices(vm);
    }

    private XElement CreateVm((string Type, string Value) folder, (string Type, string Value) pool, XElement config)
    {
        if (CreateVmFails)
        {
            throw new VimFault("InsufficientResourcesFault", "The available Memory resources in the parent resource pool are insufficient for the operation.");
        }

        if (!Vcenter.Exists(folder) || !Vcenter.Exists(pool))
        {
            throw new VimFault("ManagedObjectNotFound", "The object has already been deleted or has not been completely created");
        }

        string Text(string name) => config.Element(FakeVcenter.Ns + name)?.Value ?? string.Empty;
        var devices = new List<XElement>();
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextNic = 4000;
        lock (_lock)
        {
            foreach (var device in config.Elements(FakeVcenter.Ns + "deviceChange").Select(c => c.Element(FakeVcenter.Ns + "device")!))
            {
                var type = device.Attribute(FakeVcenter.Xsi + "type")!.Value;
                var key = device.Element(FakeVcenter.Ns + "key")!.Value;
                if (device.Element(FakeVcenter.Ns + "busNumber") is { } bus)
                {
                    var busNumber = int.Parse(bus.Value, CultureInfo.InvariantCulture);
                    keys[key] = 1000 + busNumber;
                    devices.Add(Controller(1000 + busNumber, busNumber));
                }
                else if (type == "VirtualDisk")
                {
                    var fileName = device.Element(FakeVcenter.Ns + "backing")!.Element(FakeVcenter.Ns + "fileName")!.Value;
                    var (capacity, uuid) = _disks.Values.FirstOrDefault(d => d.FileName == fileName) is { } disk ? (disk.Capacity, disk.Uuid)
                        : NasDisk(fileName, device) ?? throw new VimFault("FileNotFound", $"File {fileName} was not found");
                    devices.Add(Disk(_nextKey++, keys[device.Element(FakeVcenter.Ns + "controllerKey")!.Value], int.Parse(device.Element(FakeVcenter.Ns + "unitNumber")!.Value, CultureInfo.InvariantCulture),
                        fileName, capacity, uuid, null));
                }
                else
                {
                    var mac = device.Element(FakeVcenter.Ns + "addressType")?.Value == "manual"
                        ? device.Element(FakeVcenter.Ns + "macAddress")!.Value
                        : $"00:50:56:{Random.Shared.Next(0x80):x2}:{Random.Shared.Next(256):x2}:{Random.Shared.Next(256):x2}";
                    devices.Add(Val.Obj("VirtualDevice", type, Val.F("key", nextNic++), new XElement(device.Element(FakeVcenter.Ns + "backing")!),
                        device.Element(FakeVcenter.Ns + "connectable") is { } connectable ? new XElement(connectable) : null,
                        device.Element(FakeVcenter.Ns + "slotInfo") is { } slot ? new XElement(slot) : null, Val.F("macAddress", mac)));
                }
            }
        }

        var vm = Vcenter.Add(
            "VirtualMachine", Vcenter.NewId("vm"),
            ("name", Val.Str(Text("name"))), ("config.instanceUuid", Val.Str(Guid.NewGuid().ToString())),
            ("config.uuid", Val.Str(Text("uuid") is { Length: > 0 } bios ? bios : Guid.NewGuid().ToString())),
            ("config.template", Val.Bool(false)), ("runtime.powerState", Val.Str("poweredOff")), ("runtime.host", Val.Mor(Host.Type, Host.Value)),
            ("config.hardware.numCPU", Val.Int(int.Parse(Text("numCPUs"), CultureInfo.InvariantCulture))),
            ("config.hardware.memoryMB", Val.Int(int.Parse(Text("memoryMB"), CultureInfo.InvariantCulture))),
            ("config.changeTrackingEnabled", Val.Bool(false)), ("config.guestId", Val.Str(Text("guestId"))), ("config.firmware", Val.Str(Text("firmware"))),
            ("config.guestFullName", Val.Str("Ubuntu Linux (64-bit)")), ("parent", Val.Mor(folder.Type, folder.Value)), ("resourcePool", Val.Mor(pool.Type, pool.Value)),
            ("summary.config.vmPathName", Val.Str(config.Element(FakeVcenter.Ns + "files")!.Element(FakeVcenter.Ns + "vmPathName")!.Value)),
            ("config.extraConfig", GenerationIdConfig()));
        lock (_lock)
        {
            _devices[vm.Value] = devices;
            Events.Add($"createvm {Text("name")}");
        }

        PublishDevices(vm);
        OnVmCreated(vm, config);
        return Val.Mor("returnval", vm);
    }

    /// <summary>VMware Tools fail to quiesce (a quiesced snapshot ends in an error).</summary>
    public bool QuiesceFails { get; set; }

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
        Vcenter.Set(vm, "guest.toolsRunningStatus", Val.Str("guestToolsRunning"));
        Vcenter.Set(vm, "guest.guestOperationsReady", Val.Bool(true));
        Vcenter.Set(vm, "parent", Val.Mor(VmFolder.Type, VmFolder.Value));
        Vcenter.Set(vm, "resourcePool", Val.Mor(ResourcePool.Type, ResourcePool.Value));
        Vcenter.Set(vm, "config.firmware", Val.Str("efi"));
        Vcenter.Set(vm, "config.extraConfig", GenerationIdConfig());
        lock (_lock)
        {
            _devices[vm.Value] =
            [
                Controller(1000, 0), .. disks.Select(d => Disk(d.Key, 1000, d.Unit, d.FileName, d.CapacityBytes, d.Uuid, null)),
                Val.Obj("VirtualDevice", "VirtualVmxnet3", Val.F("key", 4000), Val.F("deviceInfo", new XElement(FakeVcenter.Ns + "label", "Network adapter 1")),
                    Val.Obj("backing", "VirtualEthernetCardNetworkBackingInfo", Val.F("deviceName", "VM Network"), Val.Mor("network", Network)),
                    Val.F("connectable", new XElement(FakeVcenter.Ns + "startConnected", "true")),
                    Val.Obj("slotInfo", "VirtualDevicePciBusSlotInfo", Val.F("pciSlotNumber", 192)), Val.F("controllerKey", 100), Val.F("unitNumber", 7),
                    Val.F("addressType", "assigned"), Val.F("macAddress", $"00:50:56:8b:{Random.Shared.Next(256):x2}:{Random.Shared.Next(256):x2}")),
            ];
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
        if (spec.Descendants(FakeVcenter.Ns + "fileOperation").Any(f => f.Value != "create" || f.Parent!.Element(FakeVcenter.Ns + "operation")!.Value != "add"))
        {
            throw new VimFault("InvalidArgument", "The fake vSphere refuses a fileOperation other than create: nothing may ever destroy or replace disk files.");
        }

        return Vcenter.StartTask(vm, "VirtualMachine.reconfigure", () =>
        {
            if (spec.Element(FakeVcenter.Ns + "changeTrackingEnabled")?.Value == "false")
            {
                Vcenter.Set(vm, "config.changeTrackingEnabled", Val.Bool(false));
                lock (_lock)
                {
                    foreach (var disk in DisksOf(vm))
                    {
                        disk.Tracking = false;
                        disk.Writes.Clear();
                    }

                    Events.Add($"cbt off {Vcenter.Name(vm)}");
                }
            }

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
                    case "add" when change.Element(FakeVcenter.Ns + "fileOperation")?.Value == "create":
                        CreateDisk(vm, device);
                        break;
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
            if (mode == "persistent")
            {
                // A quick rollback: the VM's current disk, written through the proxy.
                var live = _disks.Values.FirstOrDefault(d => d.FileName == fileName) ?? throw new VimFault("FileNotFound", $"File {fileName} was not found");
                if (Vcenter.Objects("VirtualMachine").Any(v => Vcenter.Get(v, "runtime.powerState")?.Value == "poweredOn" && v != vm && DisksOf(v).Contains(live)))
                {
                    throw new VimFault("FileLocked", $"Unable to access file {fileName} since it is locked (its VM is running)");
                }

                _devices[vm.Value].Add(Disk(_nextKey++, controller, unit, fileName, live.Capacity, live.Uuid, null));
                ProxyDevices.Attach($"sd{(char)('b' + _nextDevice++)}", live.File, live.Uuid);
                Events.Add($"attach {fileName} to {Vcenter.Name(vm)} at {controller}:{unit} persistent");
                PublishDevicesLocked(vm);
                return;
            }

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
        var quiesce = request.Element(FakeVcenter.Ns + "quiesce")?.Value == "true";
        return Vcenter.StartTask(vm, "VirtualMachine.createSnapshot", () =>
        {
            if (quiesce && QuiesceFails)
            {
                throw new VimFault("ApplicationQuiesceFault", "An error occurred while quiescing the virtual machine.");
            }

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
                Events.Add($"snapshot {name} of {Vcenter.Name(vm)}{(quiesce ? " (quiesced)" : string.Empty)}");
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

    private void PublishDevicesLocked((string Type, string Value) vm) =>
        Vcenter.Set(vm, "config.hardware.device", Val.Array("ArrayOfVirtualDevice", _devices[vm.Value].Select(d => new XElement(d))));

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

    private XElement Disk(int key, int controller, int unit, string fileName, long capacity, string uuid, string? changeId) =>
        Val.Obj("VirtualDevice", "VirtualDisk",
            Val.F("key", key),
            Val.F("deviceInfo", new XElement(FakeVcenter.Ns + "label", $"Hard disk {unit + 1}")),
            Val.Obj("backing", "VirtualDiskFlatVer2BackingInfo", Val.F("fileName", fileName), Val.Mor("datastore", Datastore), Val.F("diskMode", "persistent"), Val.F("thinProvisioned", "true"), Val.F("uuid", uuid),
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
