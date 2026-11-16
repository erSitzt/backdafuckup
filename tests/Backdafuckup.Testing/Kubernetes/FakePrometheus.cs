using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Backdafuckup.Testing.Kubernetes;

/// <summary>
/// A Prometheus query API (<c>POST /api/v1/query</c>) for tests: each query is answered with the series set for the first
/// metric name it contains; it remembers the queries and the Authorization header it got.
/// </summary>
public sealed class FakePrometheus
{
    private readonly ConcurrentDictionary<string, List<(Dictionary<string, string> Labels, double Value)>> _series = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Queries { get; } = new();

    public string? Authorization { get; private set; }

    /// <summary>Queries containing this text are refused with 400 and this error.</summary>
    public (string Text, string Error)? Refuse { get; set; }

    public void Set(string metric, double value, params (string Name, string Value)[] labels)
    {
        _series.GetOrAdd(metric, _ => []).Add((labels.ToDictionary(l => l.Name, l => l.Value, StringComparer.Ordinal), value));
    }

    public HttpMessageHandler CreateHandler() => new Handler(this);

    private sealed class Handler(FakePrometheus owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.Authorization = request.Headers.Authorization?.ToString();
            if (request.Method != HttpMethod.Post || !request.RequestUri!.AbsolutePath.EndsWith("/api/v1/query", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404 page not found") };
            }

            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            var query = WebUtility.UrlDecode(form.Split('&').Select(p => p.Split('=', 2)).First(p => p[0] == "query")[1]);
            owner.Queries.Enqueue(query);
            if (owner.Refuse is { } refuse && query.Contains(refuse.Text, StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.BadRequest, new JsonObject { ["status"] = "error", ["errorType"] = "bad_data", ["error"] = refuse.Error });
            }

            var result = new JsonArray();
            foreach (var (metric, series) in owner._series.OrderByDescending(s => s.Key.Length))
            {
                if (!query.Contains(metric, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var (labels, value) in series)
                {
                    var labelObject = new JsonObject();
                    foreach (var (name, text) in labels)
                    {
                        labelObject[name] = text;
                    }

                    result.Add(new JsonObject { ["metric"] = labelObject, ["value"] = new JsonArray(1700000000.0, value.ToString(CultureInfo.InvariantCulture)) });
                }

                break;
            }

            return Json(HttpStatusCode.OK, new JsonObject { ["status"] = "success", ["data"] = new JsonObject { ["resultType"] = "vector", ["result"] = result } });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) =>
            new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
