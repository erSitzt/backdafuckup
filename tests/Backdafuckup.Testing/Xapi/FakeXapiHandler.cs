using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Backdafuckup.Testing.Xapi;

/// <summary>Thrown by a fake method implementation to produce a JSON-RPC XAPI error.</summary>
public sealed class FakeXapiError(string code, params string[] parameters) : Exception(code)
{
    public string Code { get; } = code;

    public string[] Parameters { get; } = parameters;
}

/// <summary>
/// In-memory XAPI JSON-RPC endpoint for tests. Methods are registered per host:
/// <c>handler.On("master", "VM.get_all_records", p =&gt; ...)</c>. Unknown methods fail with MESSAGE_METHOD_UNKNOWN.
/// </summary>
public sealed class FakeXapiHandler : HttpMessageHandler
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Host, string Method), Func<JsonArray, JsonNode?>> _methods = [];
    private readonly List<(string Host, string Method)> _calls = [];

    public Func<HttpRequestMessage, HttpResponseMessage?>? HttpHandler { get; set; }

    public IReadOnlyList<(string Host, string Method)> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    public FakeXapiHandler On(string host, string method, Func<JsonArray, JsonNode?> implementation)
    {
        lock (_lock)
        {
            _methods[(host, method)] = implementation;
        }

        return this;
    }

    public int CountCalls(string method) => Calls.Count(c => c.Method == method);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri!.Host;
        if (request.RequestUri.AbsolutePath != "/jsonrpc")
        {
            return HttpHandler?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        var method = body["method"]!.GetValue<string>();
        var parameters = body["params"]!.AsArray();
        Func<JsonArray, JsonNode?>? implementation;
        lock (_lock)
        {
            _calls.Add((host, method));
            _methods.TryGetValue((host, method), out implementation);
        }

        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone() };
        try
        {
            if (implementation is null)
            {
                throw new FakeXapiError("MESSAGE_METHOD_UNKNOWN", method);
            }

            response["result"] = implementation(parameters)?.DeepClone();
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

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }
}
