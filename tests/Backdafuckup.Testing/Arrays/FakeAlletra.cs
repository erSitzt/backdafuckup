using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backdafuckup.Testing.Arrays;

/// <summary>How a fake list answer is wrapped (the OpenAPI description does not say; all three are accepted).</summary>
public enum FakeListShape
{
    MembersKeyed,
    MembersArray,
    PlainArray,
}

/// <summary>
/// An in-memory HPE Alletra MP WSAPI v3 for tests, shaped after the public OpenAPI description: sessions with bearer keys,
/// volumes and snapshots, VLUNs, hosts and host paths, ports, tasks for asynchronous calls. Every request is recorded.
/// </summary>
public sealed class FakeAlletra
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _tasks = new(StringComparer.Ordinal);
    private int _next = 100;

    public string User { get; set; } = "bdfu-wsapi";

    public string Password { get; set; } = "secret";

    /// <summary>Snapshots, deletes and edits answer 202 with a task instead of 201/200.</summary>
    public bool AsyncCalls { get; set; }

    public FakeListShape ListShape { get; set; } = FakeListShape.MembersKeyed;

    /// <summary>The next task ends in STATE_FAILED with this message.</summary>
    public string? FailNextTask { get; set; }

    /// <summary>
    /// Asked with "METHOD path" before each request (the sign-in too): a status and message to answer instead (busy,
    /// too many sessions, no rights), or null to carry it out.
    /// </summary>
    public Func<string, (HttpStatusCode Status, string Message)?>? Refuse { get; set; }

    /// <summary>The sign-in's <c>arrayPrivileges</c>.</summary>
    public JsonArray Privileges { get; set; } = new("edit");

    public List<JsonObject> DomainSets { get; } = [];

    /// <summary><c>licenses.consolidatedFeatures</c> of <c>/systems</c>.</summary>
    public JsonArray Licenses { get; set; } = new(new JsonObject { ["name"] = "Virtual Copy" }, new JsonObject { ["name"] = "Remote Copy", ["expiryDate"] = "2027-01-31T00:00:00Z" });

    public Dictionary<string, JsonObject> Volumes { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, JsonObject> Vluns { get; } = new(StringComparer.Ordinal);

    public List<JsonObject> Hosts { get; } = [];

    public List<JsonObject> HostPaths { get; } = [];

    public List<JsonObject> Ports { get; } = [];

    /// <summary>"METHOD path body" of every request after the sign-in.</summary>
    public List<string> Requests { get; } = [];

    public int Logins { get; private set; }

    public HttpMessageHandler CreateHandler() => new Handler(this);

    /// <summary>Forgets every session key (the next call gets 401, as after the idle timeout).</summary>
    public void ExpireSessions()
    {
        lock (_lock)
        {
            _keys.Clear();
        }
    }

    public JsonObject AddVolume(string name, string wwn, long sizeMiB)
    {
        lock (_lock)
        {
            long id = _next++;
            var volume = new JsonObject
            {
                ["uid"] = $"vv{id:x8}", ["id"] = id, ["name"] = name, ["wwn"] = wwn, ["sizeMiB"] = sizeMiB, ["type"] = "VVTYPE_BASE", ["readOnly"] = false,
                ["domain"] = "bdfu-test",
            };
            Volumes[volume["uid"]!.GetValue<string>()] = volume;
            return volume;
        }
    }

    public void AddHost(string name, params string[] iqns)
    {
        lock (_lock)
        {
            Hosts.Add(new JsonObject { ["uid"] = $"h{_next++:x8}", ["name"] = name, ["persona"] = new JsonObject { ["name"] = "VMware" }, ["domain"] = "bdfu-test" });
            foreach (var iqn in iqns)
            {
                HostPaths.Add(new JsonObject { ["uid"] = $"hp{_next++:x8}", ["hostName"] = name, ["address"] = iqn });
            }
        }
    }

    public void AddIscsiPort(string name, string ip, string targetIqn)
    {
        lock (_lock)
        {
            Ports.Add(new JsonObject
            {
                ["uid"] = $"p{_next++:x8}", ["name"] = name, ["protocol"] = new JsonObject { ["key"] = "ISCSI" },
                ["iscsiData"] = new JsonObject { ["ipAddress"] = ip, ["iscsiName"] = targetIqn },
            });
        }
    }

    private (HttpStatusCode Status, JsonNode? Body, string? Location) Dispatch(HttpMethod method, string path, string? authorization, JsonNode? body)
    {
        lock (_lock)
        {
            if (Refuse?.Invoke($"{method.Method} {path}") is var (refusedStatus, refusedMessage))
            {
                Requests.Add($"{method.Method} {path} (refused: {(int)refusedStatus})");
                // Localised as the B10000 sends its errors: the sentence in "default".
                return (refusedStatus, new JsonObject { ["message"] = new JsonObject { ["key"] = "REFUSED", ["default"] = refusedMessage }, ["resourceUri"] = string.Empty, ["data"] = null }, null);
            }

            if (method == HttpMethod.Post && path == "/api/v3/credentials")
            {
                if (body?["user"]?.GetValue<string>() != User || body?["password"]?.GetValue<string>() != Password)
                {
                    return (HttpStatusCode.Unauthorized, new JsonObject { ["message"] = "Invalid credentials" }, null);
                }

                var key = Guid.NewGuid().ToString("N");
                _keys.Add(key);
                Logins++;
                return (HttpStatusCode.Created, new JsonObject { ["key"] = key, ["sessionId"] = $"s{Logins}", ["username"] = User, ["arrayPrivileges"] = Privileges.DeepClone() },
                    $"/api/v3/credentials/{key}");
            }

            if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal) || !_keys.Contains(authorization[7..]))
            {
                return (HttpStatusCode.Unauthorized, new JsonObject { ["message"] = "Session expired or invalid" }, null);
            }

            Requests.Add($"{method.Method} {path} {body?.ToJsonString()}".TrimEnd());
            var parts = path.Trim('/').Split('/');
            switch (method.Method, parts.Length > 2 ? parts[2] : parts[^1], parts.Length)
            {
                case ("DELETE", "credentials", _):
                    _keys.Remove(parts[^1]);
                    return (HttpStatusCode.OK, null, null);
                case ("GET", "info", _):
                    return (HttpStatusCode.OK, new JsonObject
                    {
                        ["systemName"] = "alletra-test", ["systemModel"] = "HPE Alletra Storage MP B10000", ["systemSerial"] = "CZ0000TEST",
                        ["systemWwn"] = "2FF70002AC000001", ["osVersion"] = new JsonObject { ["display"] = "10.4.2" }, ["timeout"] = 30,
                    }, null);
                case ("GET", "systems", 3):
                    return (HttpStatusCode.OK, List([new JsonObject
                    {
                        ["uid"] = "sys1", ["name"] = "alletra-test", ["systemWWN"] = "2FF70002AC000001", ["version"] = new JsonObject { ["display"] = "10.4.2" },
                        ["licenses"] = new JsonObject { ["consolidatedFeatures"] = Licenses.DeepClone() },
                    }]), null);
                case ("GET", "volumes", 3):
                    return (HttpStatusCode.OK, List(Volumes.Values), null);
                case ("GET", "volumes", 4):
                    return Volumes.TryGetValue(parts[3], out var volume) ? (HttpStatusCode.OK, volume.DeepClone(), null) : (HttpStatusCode.NotFound, null, null);
                case ("POST", "volumes", 4):
                    return Snapshot(parts[3], body!);
                case ("PATCH", "volumes", 4):
                    if (!Volumes.TryGetValue(parts[3], out var edited))
                    {
                        return (HttpStatusCode.NotFound, null, null);
                    }

                    foreach (var (key, value) in body!.AsObject())
                    {
                        edited[key] = value?.DeepClone();
                    }

                    return Done(HttpStatusCode.OK, null);
                case ("DELETE", "volumes", 4):
                    if (!Volumes.TryGetValue(parts[3], out var gone))
                    {
                        return (HttpStatusCode.NotFound, null, null);
                    }

                    if (Vluns.Values.Any(v => v["volumeName"]?.GetValue<string>() == gone["name"]!.GetValue<string>()))
                    {
                        return (HttpStatusCode.Conflict, new JsonObject { ["message"] = $"Volume {gone["name"]} is exported" }, null);
                    }

                    Volumes.Remove(parts[3]);
                    return Done(HttpStatusCode.OK, null);
                case ("GET", "vluns", 3):
                    return (HttpStatusCode.OK, List(Vluns.Values), null);
                case ("GET", "vluns", 4):
                    return Vluns.TryGetValue(parts[3], out var vlun) ? (HttpStatusCode.OK, vlun.DeepClone(), null) : (HttpStatusCode.NotFound, null, null);
                case ("POST", "vluns", 3):
                    var volumeName = body!["volumeName"]!.GetValue<string>();
                    var target = Volumes.Values.FirstOrDefault(v => v["name"]!.GetValue<string>() == volumeName);
                    if (target is null)
                    {
                        return (HttpStatusCode.NotFound, new JsonObject { ["message"] = $"Volume {volumeName} does not exist" }, null);
                    }

                    var hostName = body["hostName"]!.GetValue<string>();
                    var host = Hosts.FirstOrDefault(h => h["name"]!.GetValue<string>() == hostName);
                    if (host is null)
                    {
                        return (HttpStatusCode.NotFound, new JsonObject { ["message"] = $"Host {hostName} does not exist" }, null);
                    }

                    // A volume in a domain goes only to a host in that domain, or in a domain set that holds it.
                    var (volumeDomain, hostDomain) = (target["domain"]?.GetValue<string>(), host["domain"]?.GetValue<string>());
                    if (volumeDomain != hostDomain && !DomainSets.Any(s => s["name"]!.GetValue<string>() == hostDomain && s["members"]!.AsArray().Any(m => m!.GetValue<string>() == volumeDomain)))
                    {
                        return (HttpStatusCode.Conflict, new JsonObject { ["message"] = $"Host {hostName} and volume {volumeName} are in different domains" }, null);
                    }

                    var uid = $"vl{_next++:x8}";
                    Vluns[uid] = new JsonObject
                    {
                        ["uid"] = uid, ["volumeName"] = volumeName, ["hostName"] = body["hostName"]!.GetValue<string>(), ["lun"] = Vluns.Count + 1,
                        ["volumeWWN"] = target["wwn"]!.GetValue<string>(), ["active"] = true,
                    };
                    return (HttpStatusCode.Created, null, $"/api/v3/vluns/{uid}");
                case ("DELETE", "vluns", 4):
                    return Vluns.Remove(parts[3]) ? (HttpStatusCode.OK, null, null) : (HttpStatusCode.NotFound, null, null);
                case ("GET", "hosts", 3):
                    return (HttpStatusCode.OK, List(Hosts), null);
                case ("GET", "domainsets", 3):
                    return (HttpStatusCode.OK, List(DomainSets), null);
                case ("GET", "hostpaths", 3):
                    return (HttpStatusCode.OK, List(HostPaths), null);
                case ("GET", "ports", 3):
                    return (HttpStatusCode.OK, List(Ports), null);
                case ("GET", "tasks", 4):
                    return _tasks.TryGetValue(parts[3], out var task) ? (HttpStatusCode.OK, task.DeepClone(), null) : (HttpStatusCode.NotFound, null, null);
                default:
                    return (HttpStatusCode.NotFound, new JsonObject { ["message"] = $"No such resource {path}" }, null);
            }
        }
    }

    private (HttpStatusCode, JsonNode?, string?) Snapshot(string sourceUid, JsonNode body)
    {
        if (!Volumes.TryGetValue(sourceUid, out var source))
        {
            return (HttpStatusCode.NotFound, null, null);
        }

        if (body["action"]?.GetValue<string>() != "CREATE_SNAPSHOT_VOLUME")
        {
            return (HttpStatusCode.BadRequest, new JsonObject { ["message"] = "Unknown action" }, null);
        }

        var parameters = body["parameters"]!;
        var name = parameters["customName"]!.GetValue<string>();
        if (name.Length > 31)
        {
            return (HttpStatusCode.BadRequest, new JsonObject { ["message"] = $"Name {name} is longer than 31 characters" }, null);
        }

        if (Volumes.Values.Any(v => v["name"]!.GetValue<string>() == name))
        {
            return (HttpStatusCode.Conflict, new JsonObject { ["message"] = $"Volume {name} already exists" }, null);
        }

        long id = _next++;
        var uid = $"vv{id:x8}";
        Volumes[uid] = new JsonObject
        {
            ["uid"] = uid, ["id"] = id, ["name"] = name, ["wwn"] = $"60002AC00000000000000{id:X3}00019C45"[..32], ["sizeMiB"] = source["sizeMiB"]!.GetValue<long>(),
            ["type"] = "VVTYPE_SNAPSHOT", ["copyOfID"] = source["id"]!.GetValue<long>(), ["comment"] = parameters["comment"]?.GetValue<string>(),
            ["readOnly"] = parameters["readOnly"]?.GetValue<bool>() ?? false, ["expireSecs"] = parameters["expireSecs"]?.GetValue<long>(), ["domain"] = source["domain"]?.DeepClone(),
        };
        return Done(HttpStatusCode.Created, uid, $"/api/v3/volumes/{uid}");
    }

    /// <summary>A finished change: 201/200 as given, or with <see cref="AsyncCalls"/> 202 and a task (failing if asked).</summary>
    private (HttpStatusCode, JsonNode?, string?) Done(HttpStatusCode status, string? createdUid, string? location = null)
    {
        if (!AsyncCalls && FailNextTask is null)
        {
            return (status, null, location);
        }

        var uid = $"t{_next++:x8}";
        var failed = FailNextTask;
        FailNextTask = null;
        _tasks[uid] = new JsonObject
        {
            ["uid"] = uid, ["status"] = failed is null ? "STATE_FINISHED" : "STATE_FAILED",
            ["createdUids"] = createdUid is null ? new JsonArray() : new JsonArray(createdUid),
            // As the B10000 gives it: numbered entries, the last one with the outcome.
            ["detailsMap"] = new JsonObject
            {
                ["0"] = new JsonObject { ["time"] = new JsonObject { ["ms"] = 1795100000000, ["tz"] = "+0100" }, ["message"] = new JsonObject { ["key"] = "TASK_CREATED", ["default"] = "Created" } },
                ["1"] = new JsonObject { ["message"] = new JsonObject { ["key"] = failed is null ? "TASK_COMPLETED" : "TASK_FAILED", ["default"] = failed ?? "Completed" } },
            },
        };
        return (HttpStatusCode.Accepted, null, $"/api/v3/tasks/{uid}");
    }

    private JsonNode List(IEnumerable<JsonObject> items) => ListShape switch
    {
        FakeListShape.PlainArray => new JsonArray([.. items.Select(i => i.DeepClone())]),
        FakeListShape.MembersArray => new JsonObject { ["members"] = new JsonArray([.. items.Select(i => i.DeepClone())]), ["total"] = items.Count() },
        _ => new JsonObject { ["members"] = new JsonObject(items.Select(i => new KeyValuePair<string, JsonNode?>(i["uid"]!.GetValue<string>(), i.DeepClone()))), ["total"] = items.Count() },
    };

    private sealed class Handler(FakeAlletra array) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var body = string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text);
            // The B10000 (OS 10.5) refuses any /api/v3 request without a media type, a GET too.
            var (status, answer, location) = request.RequestUri!.AbsolutePath.StartsWith("/api/v3/", StringComparison.Ordinal) && request.Content?.Headers.ContentType is null
                ? (HttpStatusCode.BadRequest, new JsonObject { ["message"] = new JsonObject { ["key"] = "MISSING_MEDIA_TYPE_HEADER", ["default"] = "Missing media type header" }, ["resourceUri"] = string.Empty, ["data"] = null }, (string?)null)
                : array.Dispatch(request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body);
            var response = new HttpResponseMessage(status)
            {
                Content = answer is null ? new StringContent(string.Empty) : new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (location is not null)
            {
                response.Headers.Location = new Uri(location, UriKind.Relative);
            }

            return response;
        }
    }
}
