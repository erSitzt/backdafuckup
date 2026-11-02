using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Backdafuckup.Core.Extents;
using Backdafuckup.Testing.Nbd;

namespace Backdafuckup.Testing.Xapi;

/// <summary>
/// A stateful in-memory XCP-ng pool for tests: XAPI JSON-RPC (the subset used by backdafuckup), VM snapshots,
/// changed block tracking with generations, real NBD exports (TLS, via <see cref="InProcessNbdServer"/>) and the
/// export_metadata / import_raw_vdi / import_metadata HTTP handlers. Bitmaps are encoded most-significant-bit first.
/// </summary>
public sealed class FakeXenPool : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Dictionary<string, JsonObject>> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _data = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(long Generation, DiskExtent Extent)>> _writes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InProcessNbdServer> _nbd = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private long _generation;
    private int _nextRef;

    public FakeXenPool()
    {
        foreach (var table in new[] { "pool", "host", "VM", "VBD", "VDI", "SR", "PBD", "network", "VIF", "task" })
        {
            _tables[table] = new(StringComparer.Ordinal);
        }

        HostRef = Add("host", new JsonObject
        {
            ["name_label"] = "xcp-host-1",
            ["address"] = "xcp1",
            ["enabled"] = true,
            ["software_version"] = new JsonObject { ["product_brand"] = "XCP-ng", ["product_version"] = "8.3.0" },
        });
        Add("pool", new JsonObject { ["name_label"] = "lab-pool", ["master"] = HostRef });
        SrRef = AddSr("Local storage", shared: false);
        SecondSrRef = AddSr("NFS storage", shared: true);
        NetworkRef = Add("network", new JsonObject { ["name_label"] = "Pool-wide network", ["bridge"] = "xenbr0", ["MTU"] = 1500, ["purpose"] = new JsonArray("nbd") });
        SecondNetworkRef = Add("network", new JsonObject { ["name_label"] = "Backup network", ["bridge"] = "xenbr1", ["MTU"] = 1500, ["purpose"] = new JsonArray() });
    }

    public string HostRef { get; }

    public string SrRef { get; }

    public string SecondSrRef { get; }

    public string NetworkRef { get; }

    public string SecondNetworkRef { get; }

    public string Username { get; set; } = "root";

    /// <summary>Certificate returned by host.get_server_certificate.</summary>
    public string HostCertificatePem { get; } = CreateHostCertificatePem();

    public string Password { get; set; } = "secret";

    /// <summary>When false, VDI.get_nbd_info returns an empty list (no NBD network).</summary>
    public bool NbdEnabled { get; set; } = true;

    public int ActiveNbdServers
    {
        get
        {
            lock (_lock)
            {
                return _nbd.Count;
            }
        }
    }

    public string Uuid(string table, string reference)
    {
        lock (_lock)
        {
            return _tables[table][reference]["uuid"]!.GetValue<string>();
        }
    }

    public IReadOnlyList<JsonObject> Records(string table)
    {
        lock (_lock)
        {
            return _tables[table].Values.Select(r => (JsonObject)r.DeepClone()).ToList();
        }
    }

    /// <summary>Adds a running VM with disks of the given sizes (random content with zero regions) and one NIC.</summary>
    public (string VmUuid, IReadOnlyList<string> DiskUuids) AddVm(string name, params long[] diskSizes)
    {
        lock (_lock)
        {
            var vmRef = Add("VM", new JsonObject
            {
                ["name_label"] = name,
                ["name_description"] = "test vm",
                ["is_a_template"] = false,
                ["is_control_domain"] = false,
                ["is_a_snapshot"] = false,
                ["snapshot_of"] = "OpaqueRef:NULL",
                ["power_state"] = "Running",
                ["VCPUs_max"] = 2,
                ["memory_static_max"] = 2L << 30,
                ["resident_on"] = HostRef,
                ["VBDs"] = new JsonArray(),
                ["VIFs"] = new JsonArray(),
                ["other_config"] = new JsonObject(),
                ["HVM_boot_params"] = new JsonObject { ["firmware"] = "uefi" },
                ["platform"] = new JsonObject { ["secureboot"] = "false" },
                ["VTPMs"] = new JsonArray(),
            });
            var disks = new List<string>();
            for (var i = 0; i < diskSizes.Length; i++)
            {
                var vdiRef = Add("VDI", new JsonObject
                {
                    ["name_label"] = $"{name} disk {i}",
                    ["virtual_size"] = diskSizes[i],
                    ["SR"] = SrRef,
                    ["cbt_enabled"] = false,
                    ["type"] = "user",
                    ["is_a_snapshot"] = false,
                    ["snapshot_of"] = "OpaqueRef:NULL",
                    ["other_config"] = new JsonObject(),
                });
                _data[vdiRef] = InMemoryDisk.Random(diskSizes[i], seed: _nextRef).Content;
                AddVbd(vmRef, vdiRef, i.ToString(CultureInfo.InvariantCulture), bootable: i == 0);
                disks.Add(_tables["VDI"][vdiRef]["uuid"]!.GetValue<string>());
            }

            var vifRef = Add("VIF", new JsonObject { ["device"] = "0", ["network"] = NetworkRef, ["VM"] = vmRef, ["MAC"] = "aa:bb:cc:00:00:01", ["MTU"] = 1500 });
            ((JsonArray)_tables["VM"][vmRef]["VIFs"]!).Add(vifRef);
            return (_tables["VM"][vmRef]["uuid"]!.GetValue<string>(), disks);
        }
    }

    /// <summary>Writes into a live disk; the range is tracked for changed block tracking.</summary>
    public void WriteDisk(string vdiUuid, long offset, byte[] data)
    {
        lock (_lock)
        {
            var vdiRef = RefByUuid("VDI", vdiUuid);
            data.CopyTo(_data[vdiRef], offset);
            if (!_writes.TryGetValue(vdiRef, out var list))
            {
                _writes[vdiRef] = list = [];
            }

            list.Add((_generation + 1, new DiskExtent(offset, data.Length)));
        }
    }

    public byte[] DiskContent(string vdiUuid)
    {
        lock (_lock)
        {
            return (byte[])_data[RefByUuid("VDI", vdiUuid)].Clone();
        }
    }

    /// <summary>Simulates CBT being reset (e.g. after a host crash).</summary>
    public void DisableCbt(string vdiUuid)
    {
        lock (_lock)
        {
            _tables["VDI"][RefByUuid("VDI", vdiUuid)]["cbt_enabled"] = false;
        }
    }

    public HttpMessageHandler CreateHandler() => new Handler(this);

    public async ValueTask DisposeAsync()
    {
        List<InProcessNbdServer> servers;
        lock (_lock)
        {
            servers = [.. _nbd.Values];
            _nbd.Clear();
        }

        foreach (var server in servers)
        {
            await server.DisposeAsync();
        }
    }

    private static string CreateHostCertificatePem()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=xcp1", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return certificate.ExportCertificatePem();
    }

    private string AddSr(string name, bool shared)
    {
        var srRef = Add("SR", new JsonObject
        {
            ["name_label"] = name,
            ["type"] = shared ? "nfs" : "ext",
            ["content_type"] = "user",
            ["shared"] = shared,
            ["physical_size"] = 1L << 40,
            ["physical_utilisation"] = 1L << 30,
            ["PBDs"] = new JsonArray(),
        });
        var pbdRef = Add("PBD", new JsonObject { ["host"] = HostRef, ["SR"] = srRef, ["currently_attached"] = true });
        ((JsonArray)_tables["SR"][srRef]["PBDs"]!).Add(pbdRef);
        return srRef;
    }

    private string Add(string table, JsonObject record)
    {
        var reference = $"OpaqueRef:{Interlocked.Increment(ref _nextRef):x8}-0000-0000-0000-000000000000";
        record["uuid"] ??= Guid.NewGuid().ToString();
        _tables[table][reference] = record;
        return reference;
    }

    private string AddVbd(string vmRef, string vdiRef, string device, bool bootable)
    {
        var vbdRef = Add("VBD", new JsonObject { ["VM"] = vmRef, ["VDI"] = vdiRef, ["type"] = "Disk", ["userdevice"] = device, ["bootable"] = bootable });
        ((JsonArray)_tables["VM"][vmRef]["VBDs"]!).Add(vbdRef);
        return vbdRef;
    }

    private string RefByUuid(string table, string uuid) =>
        _tables[table].FirstOrDefault(kv => kv.Value["uuid"]!.GetValue<string>() == uuid).Key ?? throw new FakeXapiError("UUID_INVALID", table, uuid);

    private JsonObject Get(string table, string reference) =>
        _tables[table].TryGetValue(reference, out var record) ? record : throw new FakeXapiError("HANDLE_INVALID", table, reference);

    private string CompletedTask(string result)
    {
        return Add("task", new JsonObject { ["name_label"] = "task", ["status"] = "success", ["progress"] = 1.0, ["result"] = $"<value>{result}</value>" });
    }

    private JsonNode? Invoke(string method, JsonArray p)
    {
        string S(int i) => p[i]!.GetValue<string>();

        if (method == "session.login_with_password")
        {
            if (S(0) != Username || S(1) != Password)
            {
                throw new FakeXapiError("SESSION_AUTHENTICATION_FAILED", S(0), "Authentication failure");
            }

            var session = $"OpaqueRef:session-{Guid.NewGuid():N}";
            _sessions[session] = S(0);
            return session;
        }

        if (!_sessions.ContainsKey(S(0)))
        {
            throw new FakeXapiError("SESSION_INVALID", S(0));
        }

        var dot = method.IndexOf('.', StringComparison.Ordinal);
        var isAsync = method.StartsWith("Async.", StringComparison.Ordinal);
        var name = isAsync ? method[6..] : method;
        dot = name.IndexOf('.', StringComparison.Ordinal);
        var table = name[..dot];
        var call = name[(dot + 1)..];

        JsonNode? result = (table, call) switch
        {
            ("session", "logout") => Logout(S(0)),
            (_, "get_all_records") => new JsonObject(_tables[table].Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value.DeepClone()))),
            (_, "get_record") => Get(table, S(1)).DeepClone(),
            (_, "get_by_uuid") => RefByUuid(table, S(1)),
            (_, "add_to_other_config") => SetMap(Get(table, S(1)), "other_config", S(2), S(3)),
            (_, "remove_from_other_config") => SetMap(Get(table, S(1)), "other_config", S(2), null),
            ("host", "get_server_certificate") => HostCertificatePem,
            ("VM", "snapshot") => Snapshot(S(1), S(2)),
            ("VM", "destroy") => DestroyVm(S(1)),
            ("VM", "set_name_label") => Set(Get("VM", S(1)), "name_label", S(2)),
            ("VM", "set_affinity") => Set(Get("VM", S(1)), "affinity", S(2)),
            ("VM", "start" or "start_on") => Set(Get("VM", S(1)), "power_state", "Running"),
            ("VM", "clean_shutdown" or "hard_shutdown") => Set(Get("VM", S(1)), "power_state", "Halted"),
            ("VDI", "enable_cbt") => Set(Get("VDI", S(1)), "cbt_enabled", true),
            ("VDI", "list_changed_blocks") => ListChangedBlocks(S(1), S(2)),
            ("VDI", "data_destroy") => DataDestroy(S(1)),
            ("VDI", "destroy") => DestroyVdi(S(1)),
            ("VDI", "get_nbd_info") => NbdInfo(S(1)),
            ("VDI", "create") => CreateVdi((JsonObject)p[1]!),
            ("VIF", "destroy") => DestroyVif(S(1)),
            ("VIF", "create") => CreateVif((JsonObject)p[1]!),
            ("task", "create") => Add("task", new JsonObject { ["name_label"] = S(1), ["status"] = "pending", ["progress"] = 0.0, ["result"] = string.Empty }),
            ("task", "destroy") => Remove("task", S(1)),
            ("task", "cancel") => Set(Get("task", S(1)), "status", "cancelled"),
            _ => throw new FakeXapiError("MESSAGE_METHOD_UNKNOWN", method),
        };

        return isAsync ? CompletedTask(result?.ToString() ?? string.Empty) : result;
    }

    private JsonNode? Logout(string session)
    {
        _sessions.Remove(session);
        return null;
    }

    private static JsonNode? Set(JsonObject record, string field, JsonNode? value)
    {
        record[field] = value;
        return null;
    }

    private static JsonNode? SetMap(JsonObject record, string field, string key, string? value)
    {
        var map = record[field] as JsonObject ?? [];
        if (value is null)
        {
            map.Remove(key);
        }
        else
        {
            map[key] = value;
        }

        record[field] = map;
        return null;
    }

    private JsonNode? Remove(string table, string reference)
    {
        _tables[table].Remove(reference);
        return null;
    }

    private JsonNode? Snapshot(string vmRef, string name)
    {
        var vm = Get("VM", vmRef);
        _generation++;
        var snapRef = Add("VM", new JsonObject
        {
            ["name_label"] = name,
            ["name_description"] = string.Empty,
            ["is_a_template"] = false,
            ["is_control_domain"] = false,
            ["is_a_snapshot"] = true,
            ["snapshot_of"] = vmRef,
            ["power_state"] = "Halted",
            ["VCPUs_max"] = vm["VCPUs_max"]!.DeepClone(),
            ["memory_static_max"] = vm["memory_static_max"]!.DeepClone(),
            ["VBDs"] = new JsonArray(),
            ["VIFs"] = new JsonArray(),
            ["other_config"] = new JsonObject(),
        });
        foreach (var vbdRef in ((JsonArray)vm["VBDs"]!).Select(n => n!.GetValue<string>()))
        {
            var vbd = Get("VBD", vbdRef);
            var sourceRef = vbd["VDI"]!.GetValue<string>();
            var source = Get("VDI", sourceRef);
            var snapVdi = Add("VDI", new JsonObject
            {
                ["name_label"] = source["name_label"]!.DeepClone(),
                ["virtual_size"] = source["virtual_size"]!.DeepClone(),
                ["SR"] = source["SR"]!.DeepClone(),
                ["cbt_enabled"] = source["cbt_enabled"]!.DeepClone(),
                ["type"] = "user",
                ["is_a_snapshot"] = true,
                ["snapshot_of"] = sourceRef,
                ["other_config"] = new JsonObject(),
                ["cbt_generation"] = _generation,
            });
            _data[snapVdi] = (byte[])_data[sourceRef].Clone();
            AddVbd(snapRef, snapVdi, vbd["userdevice"]!.GetValue<string>(), vbd["bootable"]!.GetValue<bool>());
        }

        return snapRef;
    }

    private JsonNode? ListChangedBlocks(string fromRef, string toRef)
    {
        var from = Get("VDI", fromRef);
        var to = Get("VDI", toRef);
        var source = to["snapshot_of"]!.GetValue<string>();
        if (from["snapshot_of"]!.GetValue<string>() != source || from["cbt_generation"] is null)
        {
            throw new FakeXapiError("VDI_NOT_IN_SAME_CHAIN", fromRef, toRef);
        }

        if (!Get("VDI", source)["cbt_enabled"]!.GetValue<bool>())
        {
            throw new FakeXapiError("VDI_NO_CBT_METADATA", source);
        }

        var g1 = from["cbt_generation"]!.GetValue<long>();
        var g2 = to["cbt_generation"]!.GetValue<long>();
        var changed = _writes.GetValueOrDefault(source, []).Where(w => w.Generation > g1 && w.Generation <= g2).Select(w => w.Extent);
        return Convert.ToBase64String(CbtBitmap.Encode(changed, to["virtual_size"]!.GetValue<long>(), CbtBitOrder.MostSignificantBitFirst));
    }

    private JsonNode? DataDestroy(string vdiRef)
    {
        var vdi = Get("VDI", vdiRef);
        if (!vdi["cbt_enabled"]!.GetValue<bool>())
        {
            throw new FakeXapiError("VDI_NO_CBT_METADATA", vdiRef);
        }

        if (_nbd.TryGetValue(vdiRef, out var server))
        {
            if (server.ActiveConnections > 0)
            {
                throw new FakeXapiError("VDI_IN_USE", vdiRef, "data_destroy");
            }

            _nbd.Remove(vdiRef);
            _ = server.DisposeAsync().AsTask();
        }

        vdi["type"] = "cbt_metadata";
        _data.Remove(vdiRef);
        return null;
    }

    private JsonNode? DestroyVdi(string vdiRef)
    {
        Get("VDI", vdiRef);
        _tables["VDI"].Remove(vdiRef);
        _data.Remove(vdiRef);
        if (_nbd.Remove(vdiRef, out var server))
        {
            _ = server.DisposeAsync().AsTask();
        }

        return null;
    }

    private JsonNode? DestroyVm(string vmRef)
    {
        var vm = Get("VM", vmRef);
        foreach (var vbd in ((JsonArray)vm["VBDs"]!).Select(n => n!.GetValue<string>()))
        {
            _tables["VBD"].Remove(vbd);
        }

        foreach (var vif in ((JsonArray?)vm["VIFs"] ?? []).Select(n => n!.GetValue<string>()))
        {
            _tables["VIF"].Remove(vif);
        }

        _tables["VM"].Remove(vmRef);
        return null;
    }

    private JsonNode NbdInfo(string vdiRef)
    {
        var vdi = Get("VDI", vdiRef);
        if (vdi["type"]!.GetValue<string>() == "cbt_metadata")
        {
            throw new FakeXapiError("VDI_INCOMPATIBLE_TYPE", vdiRef, "cbt_metadata");
        }

        if (!NbdEnabled)
        {
            return new JsonArray();
        }

        if (!_nbd.TryGetValue(vdiRef, out var server))
        {
            server = new InProcessNbdServer(_data[vdiRef], new NbdTestServerOptions { RequireTls = true, ExportName = $"token-{vdiRef}" });
            _nbd[vdiRef] = server;
        }

        return new JsonArray(new JsonObject
        {
            ["address"] = "127.0.0.1",
            ["port"] = server.Port,
            ["cert"] = server.Certificate.ExportCertificatePem(),
            ["exportname"] = $"token-{vdiRef}",
            ["subject"] = "localhost",
        });
    }

    private JsonNode CreateVdi(JsonObject record)
    {
        var size = long.Parse(record["virtual_size"]!.ToString(), CultureInfo.InvariantCulture);
        var vdiRef = Add("VDI", new JsonObject
        {
            ["name_label"] = record["name_label"]!.DeepClone(),
            ["virtual_size"] = size,
            ["SR"] = record["SR"]!.DeepClone(),
            ["cbt_enabled"] = false,
            ["type"] = "user",
            ["is_a_snapshot"] = false,
            ["snapshot_of"] = "OpaqueRef:NULL",
            ["other_config"] = record["other_config"]?.DeepClone() ?? new JsonObject(),
        });
        _data[vdiRef] = new byte[size];
        return vdiRef;
    }

    private JsonNode? DestroyVif(string vifRef)
    {
        var vif = Get("VIF", vifRef);
        var vm = Get("VM", vif["VM"]!.GetValue<string>());
        var list = (JsonArray)vm["VIFs"]!;
        var item = list.First(n => n!.GetValue<string>() == vifRef);
        list.Remove(item);
        _tables["VIF"].Remove(vifRef);
        return null;
    }

    private JsonNode CreateVif(JsonObject record)
    {
        var mac = record["MAC"]!.GetValue<string>();
        if (string.IsNullOrEmpty(mac))
        {
            mac = string.Join(':', RandomNumberGenerator.GetBytes(6).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        var vifRef = Add("VIF", new JsonObject
        {
            ["device"] = record["device"]!.DeepClone(),
            ["network"] = record["network"]!.DeepClone(),
            ["VM"] = record["VM"]!.DeepClone(),
            ["MAC"] = mac,
            ["MTU"] = long.Parse(record["MTU"]!.ToString(), CultureInfo.InvariantCulture),
        });
        ((JsonArray)Get("VM", record["VM"]!.GetValue<string>())["VIFs"]!).Add(vifRef);
        return vifRef;
    }

    private byte[] ExportMetadata(string vmUuid)
    {
        var vmRef = RefByUuid("VM", vmUuid);
        var vm = Get("VM", vmRef);
        var export = new JsonObject
        {
            ["vm"] = vm.DeepClone(),
            ["vbds"] = new JsonArray(((JsonArray)vm["VBDs"]!).Select(n => Get("VBD", n!.GetValue<string>())).Select(vbd => (JsonNode?)new JsonObject
            {
                ["userdevice"] = vbd["userdevice"]!.DeepClone(),
                ["bootable"] = vbd["bootable"]!.DeepClone(),
                ["vdi_uuid"] = Get("VDI", vbd["VDI"]!.GetValue<string>())["uuid"]!.DeepClone(),
            }).ToArray()),
            ["vifs"] = new JsonArray(((JsonArray)vm["VIFs"]!).Select(n => Get("VIF", n!.GetValue<string>())).Select(vif => (JsonNode?)new JsonObject
            {
                ["device"] = vif["device"]!.DeepClone(),
                ["network_uuid"] = Get("network", vif["network"]!.GetValue<string>())["uuid"]!.DeepClone(),
                ["MAC"] = vif["MAC"]!.DeepClone(),
                ["MTU"] = vif["MTU"]!.DeepClone(),
            }).ToArray()),
        };
        return Encoding.UTF8.GetBytes(export.ToJsonString());
    }

    private string ImportMetadata(byte[] body, bool restore, IReadOnlyDictionary<string, string> vdiMap)
    {
        var export = JsonNode.Parse(body)!.AsObject();
        var vm = (JsonObject)export["vm"]!.DeepClone();
        var uuid = vm["uuid"]!.GetValue<string>();
        if (restore && _tables["VM"].Values.Any(v => v["uuid"]!.GetValue<string>() == uuid))
        {
            throw new FakeXapiError("DUPLICATE_VM", uuid);
        }

        vm["uuid"] = restore ? uuid : Guid.NewGuid().ToString();
        vm["power_state"] = "Halted";
        vm["VBDs"] = new JsonArray();
        vm["VIFs"] = new JsonArray();
        vm["other_config"] = new JsonObject();
        var vmRef = Add("VM", vm);
        foreach (var vbd in ((JsonArray)export["vbds"]!).OfType<JsonObject>())
        {
            var oldUuid = vbd["vdi_uuid"]!.GetValue<string>();
            var newUuid = vdiMap.TryGetValue(oldUuid, out var mapped) ? mapped : throw new FakeXapiError("VDI_MISSING", oldUuid);
            AddVbd(vmRef, RefByUuid("VDI", newUuid), vbd["userdevice"]!.GetValue<string>(), vbd["bootable"]!.GetValue<bool>());
        }

        foreach (var vif in ((JsonArray)export["vifs"]!).OfType<JsonObject>())
        {
            var vifRef = Add("VIF", new JsonObject
            {
                ["device"] = vif["device"]!.DeepClone(),
                ["network"] = RefByUuid("network", vif["network_uuid"]!.GetValue<string>()),
                ["VM"] = vmRef,
                ["MAC"] = vif["MAC"]!.DeepClone(),
                ["MTU"] = vif["MTU"]!.DeepClone(),
            });
            ((JsonArray)vm["VIFs"]!).Add(vifRef);
        }

        return vmRef;
    }

    private sealed class Handler(FakeXenPool pool) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            if (path == "jsonrpc")
            {
                return await JsonRpcAsync(request, cancellationToken);
            }

            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            lock (pool._lock)
            {
                if (query["session_id"] is not { } session || !pool._sessions.ContainsKey(session))
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }

                switch (path)
                {
                    case "export_metadata":
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pool.ExportMetadata(query["uuid"]!)) };

                    case "import_raw_vdi":
                        var vdi = pool.Get("VDI", query["vdi"]!);
                        if (body.LongLength != vdi["virtual_size"]!.GetValue<long>())
                        {
                            return new HttpResponseMessage(HttpStatusCode.BadRequest);
                        }

                        pool._data[query["vdi"]!] = body;
                        Complete(query["task_id"]!, string.Empty);
                        return new HttpResponseMessage(HttpStatusCode.OK);

                    case "import_metadata":
                        var map = query.AllKeys.Where(k => k is not null && k.StartsWith("vdi:", StringComparison.Ordinal))
                            .ToDictionary(k => k![4..], k => query[k]!);
                        try
                        {
                            var vmRef = pool.ImportMetadata(body, query["restore"] == "true", map);
                            Complete(query["task_id"]!, $"<array><data><value>{vmRef}</value></data></array>");
                        }
                        catch (FakeXapiError error)
                        {
                            var task = pool.Get("task", query["task_id"]!);
                            task["status"] = "failure";
                            task["error_info"] = new JsonArray(error.Code);
                        }

                        return new HttpResponseMessage(HttpStatusCode.OK);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private void Complete(string taskRef, string result)
        {
            var task = pool.Get("task", taskRef);
            task["status"] = "success";
            task["progress"] = 1.0;
            task["result"] = $"<value>{result}</value>";
        }

        private async Task<HttpResponseMessage> JsonRpcAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone() };
            try
            {
                lock (pool._lock)
                {
                    response["result"] = pool.Invoke(body["method"]!.GetValue<string>(), body["params"]!.AsArray())?.DeepClone();
                }
            }
            catch (FakeXapiError error)
            {
                response["error"] = new JsonObject
                {
                    ["code"] = 1,
                    ["message"] = error.Code,
                    ["data"] = new JsonArray(error.Parameters.Select(p => (JsonNode?)p).ToArray()),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
