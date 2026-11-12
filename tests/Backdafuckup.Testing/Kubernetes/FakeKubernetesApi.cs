using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Backdafuckup.Testing.Kubernetes;

/// <summary>
/// An in-memory Kubernetes API for tests, plugged into the client as its outermost handler so no request reaches the
/// network. It serves <c>/version</c>, lists (with equality label and field selectors), reads, creates, replaces, merge-patches
/// and deletes of objects, core and grouped, cluster-scoped and namespaced, by the API's URL layout, and pod logs.
/// <see cref="Created"/> stands in for the cluster's controllers (a snapshot becomes ready, a pod runs).
/// </summary>
public sealed class FakeKubernetesApi
{
    private readonly Lock _lock = new();
    private readonly List<(string ApiPath, string Plural, JsonObject Item)> _objects = [];

    /// <summary>API groups every cluster serves; others (e.g. snapshot.storage.k8s.io) only once an object of theirs was added.</summary>
    private static readonly string[] BuiltInApis = ["api/v1", "apis/apps/v1", "apis/storage.k8s.io/v1"];

    public string GitVersion { get; set; } = "v1.32.13+rke2r1";

    /// <summary>API groups served without objects of them (e.g. an aggregated API), for discovery.</summary>
    public HashSet<string> ExtraApiGroups { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, every request is answered with this status (e.g. 401 for a refused token).</summary>
    public HttpStatusCode? FailWith { get; set; }

    /// <summary>The next this many requests are answered with 429 "storage is (re)initializing" and Retry-After: 1.</summary>
    public int Busy { get; set; }

    /// <summary>The paths requested, for asserting what the client asked.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Called after an object was created, with its plural and the stored object (change it inside <see cref="Mutate"/>).</summary>
    public Action<string, JsonObject>? Created { get; set; }

    /// <summary>Called after an object was replaced or patched, with its plural and the stored object.</summary>
    public Action<string, JsonObject>? Patched { get; set; }

    /// <summary>Called after an object was deleted, with its plural and the object.</summary>
    public Action<string, JsonObject>? Deleted { get; set; }

    /// <summary>What a SelfSubjectAccessReview answers for (verb, resource); everything by default.</summary>
    public Func<string, string, bool> Allowed { get; set; } = (_, _) => true;

    /// <summary>When set, requests without <c>Authorization: Bearer</c> this token are answered with 401.</summary>
    public string? RequiredToken { get; set; }

    /// <summary>The log of each pod by "namespace/name".</summary>
    public Dictionary<string, string> PodLogs { get; } = [];

    /// <param name="kubeSystemUid">The uid of kube-system, the cluster's platform id: another one for a second cluster.</param>
    public FakeKubernetesApi(string kubeSystemUid = "uid-kube-system")
    {
        Add("api/v1", "namespaces", Namespace("kube-system", kubeSystemUid));
    }

    public DelegatingHandler CreateHandler() => new Handler(this);

    /// <param name="apiPath"><c>api/v1</c> or <c>apis/&lt;group&gt;/&lt;version&gt;</c>.</param>
    public JsonObject Add(string apiPath, string plural, JsonObject item)
    {
        lock (_lock)
        {
            _objects.Add((apiPath, plural, item));
        }

        return item;
    }

    /// <summary>Changes stored objects consistently with requests running at the same time.</summary>
    public void Mutate(Action change)
    {
        lock (_lock)
        {
            change();
        }
    }

    /// <summary>The stored object (change it inside <see cref="Mutate"/>), or null.</summary>
    public JsonObject? Find(string plural, string? ns, string name)
    {
        lock (_lock)
        {
            return _objects.FirstOrDefault(o => o.Plural == plural && Matches(o.Item, ns, name)).Item;
        }
    }

    /// <summary>Removes an object as a controller would (no request); false when there is none.</summary>
    public bool Remove(string plural, string? ns, string name)
    {
        lock (_lock)
        {
            return _objects.RemoveAll(o => o.Plural == plural && Matches(o.Item, ns, name)) > 0;
        }
    }

    /// <summary>Copies of the stored objects of a kind.</summary>
    public IReadOnlyList<JsonObject> All(string plural)
    {
        lock (_lock)
        {
            return [.. _objects.Where(o => o.Plural == plural).Select(o => (JsonObject)o.Item.DeepClone())];
        }
    }

    public static JsonObject Namespace(string name, string? uid = null) => new()
    {
        ["apiVersion"] = "v1",
        ["kind"] = "Namespace",
        ["metadata"] = new JsonObject { ["name"] = name, ["uid"] = uid ?? $"uid-{name}" },
        ["status"] = new JsonObject { ["phase"] = "Active" },
    };

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath.Trim('/');
        lock (_lock)
        {
            Requests.Add($"{request.Method} /{path}");
        }

        lock (_lock)
        {
            if (Busy > 0)
            {
                Busy--;
                var busy = Json(HttpStatusCode.TooManyRequests, new JsonObject
                {
                    ["kind"] = "Status", ["apiVersion"] = "v1", ["status"] = "Failure", ["message"] = "storage is (re)initializing", ["reason"] = "TooManyRequests", ["code"] = 429,
                });
                busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return busy;
            }
        }

        if (FailWith is { } status)
        {
            return Json(status, new JsonObject { ["kind"] = "Status", ["apiVersion"] = "v1", ["status"] = "Failure", ["message"] = $"fake: {(int)status}", ["code"] = (int)status });
        }

        if (path == "version")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["major"] = "1", ["minor"] = "32", ["gitVersion"] = GitVersion });
        }

        if (path == "apis")
        {
            // Discovery: the built-in groups, the groups of the objects added, and those served without objects.
            List<string> versions;
            lock (_lock)
            {
                versions = [.. BuiltInApis.Concat(_objects.Select(o => o.ApiPath)).Concat(ExtraApiGroups.Select(g => $"apis/{g}/v1"))
                    .Where(a => a.StartsWith("apis/", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)];
            }

            var groups = new JsonArray();
            foreach (var group in versions.Select(a => a.Split('/')).GroupBy(a => a[1], StringComparer.Ordinal))
            {
                var preferred = new JsonObject { ["groupVersion"] = $"{group.Key}/{group.First()[2]}", ["version"] = group.First()[2] };
                groups.Add(new JsonObject
                {
                    ["name"] = group.Key,
                    ["versions"] = new JsonArray([.. group.Select(a => (JsonNode)new JsonObject { ["groupVersion"] = $"{a[1]}/{a[2]}", ["version"] = a[2] })]),
                    ["preferredVersion"] = preferred,
                });
            }

            return Json(HttpStatusCode.OK, new JsonObject { ["kind"] = "APIGroupList", ["apiVersion"] = "v1", ["groups"] = groups });
        }

        var parts = path.Split('/');
        var apiLength = parts[0] == "api" ? 2 : parts[0] == "apis" ? 3 : 0;
        if (apiLength == 0 || parts.Length <= apiLength)
        {
            return NotFound(path);
        }

        var apiPath = string.Join('/', parts[..apiLength]);
        var rest = parts[apiLength..];
        string? ns = null;
        if (rest.Length >= 3 && rest[0] == "namespaces")
        {
            ns = rest[1];
            rest = rest[2..];
        }

        // rest: [plural], [plural, name] or [plural, name, subresource] (namespaces/<name> alone is the namespace itself).
        var plural = ns is not null && rest.Length == 0 ? "namespaces" : rest[0];
        var name = ns is not null && rest.Length == 0 ? ns : rest.Length > 1 ? rest[1] : null;
        var subresource = rest.Length > 2 ? rest[2] : null;
        if (ns is not null && rest.Length == 0)
        {
            ns = null;
        }

        if (subresource == "log" && request.Method == HttpMethod.Get)
        {
            lock (_lock)
            {
                return PodLogs.TryGetValue($"{ns}/{name}", out var text)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") }
                    : NotFound(path);
            }
        }

        if (request.Method == HttpMethod.Post && name is null)
        {
            return Create(apiPath, plural, ns, request);
        }

        if (request.Method == HttpMethod.Delete && name is not null)
        {
            JsonObject removed;
            lock (_lock)
            {
                var index = _objects.FindIndex(o => o.ApiPath == apiPath && o.Plural == plural && Matches(o.Item, ns, name));
                if (index < 0)
                {
                    return NotFound(path);
                }

                removed = _objects[index].Item;
                _objects.RemoveAt(index);
            }

            Deleted?.Invoke(plural, removed);
            return Json(HttpStatusCode.OK, (JsonObject)removed.DeepClone());
        }

        if ((request.Method == HttpMethod.Put || request.Method == HttpMethod.Patch) && name is not null)
        {
            var parsed = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            if (parsed is JsonArray operations)
            {
                return JsonPatch(apiPath, plural, ns, name, path, operations);
            }

            var body = parsed.AsObject();
            JsonObject changed;
            lock (_lock)
            {
                var index = _objects.FindIndex(o => o.ApiPath == apiPath && o.Plural == plural && Matches(o.Item, ns, name));
                if (index < 0)
                {
                    return NotFound(path);
                }

                var stored = _objects[index].Item;
                if (request.Method == HttpMethod.Put)
                {
                    body["metadata"]!["uid"] = stored["metadata"]?["uid"]?.DeepClone();
                    _objects[index] = (apiPath, plural, body);
                    stored = body;
                }
                else
                {
                    MergePatch(stored, body);
                }

                changed = stored;
            }

            Patched?.Invoke(plural, changed);
            lock (_lock)
            {
                return Json(HttpStatusCode.OK, (JsonObject)changed.DeepClone());
            }
        }

        if (request.Method != HttpMethod.Get)
        {
            return Json(HttpStatusCode.MethodNotAllowed, new JsonObject { ["kind"] = "Status", ["message"] = $"fake: {request.Method} /{path} not supported" });
        }

        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var selector = query["labelSelector"]?
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.Split('=', 2))
            .ToList() ?? [];

        // Field selectors with equality on dotted paths, e.g. metadata.name=x or spec.nodeName=n.
        var fields = query["fieldSelector"]?
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.Split('=', 2))
            .ToList() ?? [];
        static string? Field(JsonObject item, string path) =>
            path.Split('.').Aggregate((JsonNode?)item, (node, key) => node?[key]) is JsonValue value ? value.ToString() : null;
        List<JsonObject> matches;
        bool served;
        lock (_lock)
        {
            served = BuiltInApis.Contains(apiPath) || _objects.Any(o => o.ApiPath == apiPath);
            matches = _objects
                .Where(o => o.ApiPath == apiPath && o.Plural == plural)
                .Where(o => ns is null || (string?)o.Item["metadata"]?["namespace"] == ns)
                .Where(o => name is null || (string?)o.Item["metadata"]?["name"] == name)
                .Where(o => selector.All(term => (string?)o.Item["metadata"]?["labels"]?[term[0]] == term[^1]))
                .Where(o => fields.All(term => Field(o.Item, term[0]) == term[^1]))
                .Select(o => (JsonObject)o.Item.DeepClone())
                .ToList();
        }

        if (name is not null)
        {
            return matches.Count == 1 ? Json(HttpStatusCode.OK, matches[0]) : NotFound(path);
        }

        if (!served)
        {
            return NotFound(path); // The group is not served (e.g. no snapshot CRDs).
        }

        var list = new JsonArray();
        foreach (var match in matches)
        {
            list.Add(match);
        }

        return Json(HttpStatusCode.OK, new JsonObject { ["kind"] = "List", ["apiVersion"] = "v1", ["metadata"] = new JsonObject(), ["items"] = list });
    }

    private HttpResponseMessage Create(string apiPath, string plural, string? ns, HttpRequestMessage request)
    {
        var item = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
        if (plural == "selfsubjectaccessreviews")
        {
            // The authorizer: the account may do what Allowed says.
            var attributes = item["spec"]?["resourceAttributes"];
            item["status"] = new JsonObject { ["allowed"] = Allowed((string?)attributes?["verb"] ?? string.Empty, (string?)attributes?["resource"] ?? string.Empty) };
            return Json(HttpStatusCode.Created, item);
        }

        var metadata = item["metadata"]?.AsObject() ?? throw new InvalidOperationException("fake: an object without metadata");
        var name = (string?)metadata["name"];
        if (ns is not null)
        {
            metadata["namespace"] = ns;
        }

        lock (_lock)
        {
            // As a real API server: an object goes into an existing namespace only.
            if (ns is not null && !_objects.Any(o => o.Plural == "namespaces" && (string?)o.Item["metadata"]?["name"] == ns))
            {
                return Json(HttpStatusCode.NotFound, new JsonObject
                {
                    ["kind"] = "Status", ["apiVersion"] = "v1", ["status"] = "Failure", ["reason"] = "NotFound", ["code"] = 404,
                    ["message"] = $"fake: namespaces \"{ns}\" not found",
                });
            }

            if (_objects.Any(o => o.ApiPath == apiPath && o.Plural == plural && Matches(o.Item, ns, name!)))
            {
                return Json(HttpStatusCode.Conflict, new JsonObject
                {
                    ["kind"] = "Status", ["apiVersion"] = "v1", ["status"] = "Failure", ["reason"] = "AlreadyExists", ["code"] = 409,
                    ["message"] = $"fake: {plural} \"{name}\" already exists",
                });
            }

            metadata["uid"] = $"uid-{plural}-{name}";
            metadata["creationTimestamp"] = "2026-11-11T12:00:00Z";
            _objects.Add((apiPath, plural, item));
        }

        Created?.Invoke(plural, item);
        lock (_lock)
        {
            return Json(HttpStatusCode.Created, (JsonObject)item.DeepClone());
        }
    }

    private static bool Matches(JsonObject item, string? ns, string name) =>
        (string?)item["metadata"]?["name"] == name && (ns is null || (string?)item["metadata"]?["namespace"] == ns);

    /// <summary>JSON patch (RFC 6902) with test, replace and add on object members and array items; a failed test is 422.</summary>
    private HttpResponseMessage JsonPatch(string apiPath, string plural, string? ns, string name, string path, JsonArray operations)
    {
        JsonObject changed;
        lock (_lock)
        {
            var index = _objects.FindIndex(o => o.ApiPath == apiPath && o.Plural == plural && Matches(o.Item, ns, name));
            if (index < 0)
            {
                return NotFound(path);
            }

            var copy = (JsonObject)_objects[index].Item.DeepClone();
            foreach (var operation in operations.Select(o => o!.AsObject()))
            {
                var pointer = ((string)operation["path"]!).Split('/', StringSplitOptions.RemoveEmptyEntries);
                JsonNode parent = copy;
                foreach (var step in pointer[..^1])
                {
                    parent = parent is JsonArray array ? array[int.Parse(step, System.Globalization.CultureInfo.InvariantCulture)]! : parent[step]!;
                }

                var last = pointer[^1];
                var value = operation["value"]?.DeepClone();
                switch ((string)operation["op"]!)
                {
                    case "test" when !JsonNode.DeepEquals(parent is JsonArray items ? items[int.Parse(last, System.Globalization.CultureInfo.InvariantCulture)] : parent[last], value):
                        return Json((HttpStatusCode)422, new JsonObject { ["kind"] = "Status", ["status"] = "Failure", ["message"] = $"test failed at {operation["path"]}", ["code"] = 422 });
                    case "test":
                        break;
                    case "replace" or "add" when parent is JsonArray items:
                        items[int.Parse(last, System.Globalization.CultureInfo.InvariantCulture)] = value;
                        break;
                    case "replace" or "add":
                        parent[last] = value;
                        break;
                }
            }

            _objects[index] = (apiPath, plural, copy);
            changed = copy;
        }

        Patched?.Invoke(plural, changed);
        lock (_lock)
        {
            return Json(HttpStatusCode.OK, (JsonObject)changed.DeepClone());
        }
    }

    /// <summary>JSON merge patch (RFC 7386): objects merge, null removes, anything else replaces.</summary>
    private static void MergePatch(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch.ToList())
        {
            if (value is null)
            {
                target.Remove(key);
            }
            else if (value is JsonObject child && target[key] is JsonObject existing)
            {
                MergePatch(existing, child);
            }
            else
            {
                target[key] = value.DeepClone();
            }
        }
    }

    private static HttpResponseMessage NotFound(string path) =>
        Json(HttpStatusCode.NotFound, new JsonObject { ["kind"] = "Status", ["apiVersion"] = "v1", ["status"] = "Failure", ["message"] = $"fake: /{path} not found", ["code"] = 404 });

    private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private sealed class Handler(FakeKubernetesApi api) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = api.RequiredToken is { } token && request.Headers.Authorization?.ToString() != "Bearer " + token
                ? Json(HttpStatusCode.Unauthorized, new JsonObject { ["kind"] = "Status", ["apiVersion"] = "v1", ["status"] = "Failure", ["message"] = "Unauthorized", ["code"] = 401 })
                : api.Answer(request);
            response.RequestMessage = request; // The client reads the request back from the response.
            return Task.FromResult(response);
        }
    }
}
