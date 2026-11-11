using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Backdafuckup.Testing.Kubernetes;

/// <summary>
/// An in-memory Kubernetes API for tests, plugged into the client as its outermost handler so no request reaches the
/// network. It serves <c>/version</c> and lists and reads of the objects added to it, core and grouped, cluster-scoped and
/// namespaced, by the API's URL layout.
/// </summary>
public sealed class FakeKubernetesApi
{
    private readonly Lock _lock = new();
    private readonly List<(string ApiPath, string Plural, JsonObject Item)> _objects = [];

    /// <summary>API groups every cluster serves; others (e.g. snapshot.storage.k8s.io) only once an object of theirs was added.</summary>
    private static readonly string[] BuiltInApis = ["api/v1", "apis/apps/v1", "apis/storage.k8s.io/v1"];

    public string GitVersion { get; set; } = "v1.32.13+rke2r1";

    /// <summary>When set, every request is answered with this status (e.g. 401 for a refused token).</summary>
    public HttpStatusCode? FailWith { get; set; }

    /// <summary>The paths requested, for asserting what the client asked.</summary>
    public List<string> Requests { get; } = [];

    public FakeKubernetesApi()
    {
        Add("api/v1", "namespaces", Namespace("kube-system", "uid-kube-system"));
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

        if (FailWith is { } status)
        {
            return Json(status, new JsonObject { ["kind"] = "Status", ["apiVersion"] = "v1", ["status"] = "Failure", ["message"] = $"fake: {(int)status}", ["code"] = (int)status });
        }

        if (request.Method != HttpMethod.Get)
        {
            return Json(HttpStatusCode.MethodNotAllowed, new JsonObject { ["kind"] = "Status", ["message"] = $"fake: {request.Method} not supported" });
        }

        if (path == "version")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["major"] = "1", ["minor"] = "32", ["gitVersion"] = GitVersion });
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

        // rest: [plural] or [plural, name] (namespaces/<name> alone is a read of a namespace).
        var plural = ns is not null && rest.Length == 0 ? "namespaces" : rest[0];
        var name = ns is not null && rest.Length == 0 ? ns : rest.Length > 1 ? rest[1] : null;
        if (ns is not null && rest.Length == 0)
        {
            ns = null;
        }

        List<JsonObject> matches;
        bool served;
        lock (_lock)
        {
            served = BuiltInApis.Contains(apiPath) || _objects.Any(o => o.ApiPath == apiPath);
            matches = _objects
                .Where(o => o.ApiPath == apiPath && o.Plural == plural)
                .Where(o => ns is null || (string?)o.Item["metadata"]?["namespace"] == ns)
                .Where(o => name is null || (string?)o.Item["metadata"]?["name"] == name)
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

    private static HttpResponseMessage NotFound(string path) =>
        Json(HttpStatusCode.NotFound, new JsonObject { ["kind"] = "Status", ["apiVersion"] = "v1", ["status"] = "Failure", ["message"] = $"fake: /{path} not found", ["code"] = 404 });

    private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private sealed class Handler(FakeKubernetesApi api) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = api.Answer(request);
            response.RequestMessage = request; // The client reads the request back from the response.
            return Task.FromResult(response);
        }
    }
}
