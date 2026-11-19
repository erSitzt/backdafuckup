using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Backdafuckup.Arrays.Alletra;

/// <summary>An error the array answered with (its own message), or a task that ended in failure.</summary>
public sealed class AlletraException(string message, HttpStatusCode? status = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;

    /// <summary>The array refused for lack of rights (HTTP 403, or a message saying so).</summary>
    public bool PermissionDenied { get; init; }

    /// <summary>The array's own words (HTTP status and message), without our context.</summary>
    public string? ArrayMessage { get; init; }
}

/// <summary>
/// The HPE Alletra Storage MP WSAPI v3 over HTTPS, written from the public OpenAPI description: a session key from
/// <c>POST /api/v3/credentials</c> as a bearer token (one new sign-in when it answers 401), asynchronous calls that answer
/// 201 with the new resource's location or 202 with a task to poll, and lists in any of the shapes seen (an array, or
/// <c>members</c> as an array or keyed by uid).
/// </summary>
internal sealed partial class AlletraClient : IAsyncDisposable
{
    /// <summary>Pauses between tries while the array says it is busy: about 90 s in all.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)];

    private static readonly TimeSpan TaskPoll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);
    private readonly HttpClient _http;
    private readonly string _password;
    private readonly SemaphoreSlim _login = new(1, 1);
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly ConditionalWeakTable<HttpResponseMessage, string> _retried = [];
    private string? _key;

    public AlletraClient(HttpMessageHandler handler, string address, int port, string user, string password, TimeSpan? taskTimeout = null, IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        Endpoint = port == 443 ? address : $"{address}:{port.ToString(CultureInfo.InvariantCulture)}";
        _http = new HttpClient(handler, disposeHandler: true) { BaseAddress = new Uri($"https://{Endpoint}/"), Timeout = TimeSpan.FromMinutes(2) };
        User = user;
        _password = password;
        TaskTimeout = taskTimeout ?? TimeSpan.FromMinutes(10);
        _retryDelays = retryDelays ?? DefaultRetryDelays;
    }

    /// <summary>"array01.corp.example" (with ":port" when it is not 443), for messages.</summary>
    public string Endpoint { get; }

    public TimeSpan TaskTimeout { get; }

    /// <summary>The account the session signs in as.</summary>
    public string User { get; }

    /// <summary>The account's privileges from the sign-in (<c>arrayPrivileges</c>), e.g. "edit in domain bdfu-test".</summary>
    public IReadOnlyList<string> Privileges { get; private set; } = [];

    /// <summary>The privileges for a message: "edit in domain bdfu-test, browse in all domains", or that the array named none.</summary>
    public string PrivilegeSummary => Privileges.Count == 0 ? "no privileges the array named at sign-in" : string.Join(", ", Privileges);

    /// <summary>One entry of <c>arrayPrivileges</c>: a plain role, or an object with role and domain.</summary>
    internal static string? Privilege(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject o when (o["role"]?.GetValue<string>() ?? Text(o["localizedRole"])) is { Length: > 0 } role =>
            $"{role} in {(o["domain"]?.GetValue<string>() is { Length: > 0 } domain && domain is not ("-" or "all") ? $"domain {domain}" : "all domains")}",
        JsonObject o => o.ToJsonString(),
        _ => null,
    };

    public async Task LoginAsync(CancellationToken cancellationToken)
    {
        await _login.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var response = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Post, "api/v3/credentials") { Content = JsonContent.Create(new { user = User, password = _password }) }, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new AlletraException($"Array {Endpoint}: sign-in as {User} refused (wrong user name or password, or the account may not use the WSAPI).", response.StatusCode);
            }

            await EnsureSuccessAsync(response, "POST", "api/v3/credentials", cancellationToken).ConfigureAwait(false);
            var body = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            _key = body?["key"]?.GetValue<string>() ?? LastSegment(response.Headers.Location)
                ?? throw new AlletraException($"Array {Endpoint}: the sign-in returned no session key.");
            Privileges = body?["arrayPrivileges"] is JsonArray privileges ? [.. privileges.Select(Privilege).OfType<string>()] : [];
        }
        finally
        {
            _login.Release();
        }
    }

    public async Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "GET", path, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The members of a list, whatever its shape.</summary>
    public async Task<IReadOnlyList<JsonObject>> ListAsync(string path, CancellationToken cancellationToken) => Members(await GetAsync(path, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// A call that creates or changes something: on 201 the uid from the Location header, on 202 the task is awaited and
    /// its first created uid returned (or null).
    /// </summary>
    public async Task<string?> ChangeAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, path, body, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, method.Method, path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted && response.Headers.Location is { } taskUri)
        {
            var task = await WaitForTaskAsync(taskUri, method.Method, path, cancellationToken).ConfigureAwait(false);
            return task["createdUids"] is JsonArray created && created.Count > 0 ? created[0]?.GetValue<string>() : null;
        }

        return LastSegment(response.Headers.Location);
    }

    public async ValueTask DisposeAsync()
    {
        if (_key is { } key)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/v3/credentials/{Uri.EscapeDataString(key)}") { Content = EmptyJson() };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // The session times out on the array anyway.
            }
        }

        _http.Dispose();
        _login.Dispose();
    }

    internal static IReadOnlyList<JsonObject> Members(JsonNode? node) => node switch
    {
        JsonArray array => [.. array.OfType<JsonObject>()],
        JsonObject { } o when o["members"] is JsonArray members => [.. members.OfType<JsonObject>()],
        JsonObject { } o when o["members"] is JsonObject keyed => [.. keyed.Select(kv => kv.Value).OfType<JsonObject>()],
        JsonObject { } o when o.ContainsKey("uid") => [o],
        JsonObject { } o => [.. o.Select(kv => kv.Value).OfType<JsonObject>().Where(v => v.ContainsKey("uid"))],
        _ => [],
    };

    /// <summary>A number in whatever form it came (integer, decimal, or a string of digits).</summary>
    internal static long? Long(JsonNode? node) => node is JsonValue v
        ? v.TryGetValue<long>(out var l) ? l
        : v.TryGetValue<int>(out var i) ? i
        : v.TryGetValue<double>(out var d) ? (long)d
        : v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed
        : null
        : null;

    internal static double? Double(JsonNode? node) => node is JsonValue v
        ? v.TryGetValue<double>(out var d) ? d : Long(node)
        : null;

    internal static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>A localised text for people: its <c>default</c> (the sentence), else its <c>key</c>, or a plain string.</summary>
    internal static string? Readable(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject o => (o["default"] ?? o["key"]) is JsonValue inner && inner.TryGetValue<string>(out var t) ? t : null,
        _ => null,
    };

    /// <summary>A localised value (<c>{"key": "…", "default": "…"}</c>) or a plain string.</summary>
    internal static string? Text(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject o => (o["key"] ?? o["default"]) is JsonValue inner && inner.TryGetValue<string>(out var t) ? t : null,
        _ => null,
    };

    private async Task<JsonObject> WaitForTaskAsync(Uri taskUri, string method, string path, CancellationToken cancellationToken)
    {
        var taskPath = taskUri.IsAbsoluteUri ? taskUri.PathAndQuery.TrimStart('/') : taskUri.OriginalString.TrimStart('/');
        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            var task = await GetAsync(taskPath, cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new AlletraException($"Array {Endpoint}: the task of {method} {path} ({taskPath}) disappeared.");
            switch (task["status"]?.GetValue<string>())
            {
                case "STATE_FINISHED":
                case "STATE_WARNING":
                    return task;
                case "STATE_FAILED":
                case "STATE_CANCELED":
                    throw new AlletraException($"Array {Endpoint}: {method} {path} failed: {TaskMessage(task)} ({task["status"]}).");
            }

            if (DateTimeOffset.UtcNow - started > TaskTimeout)
            {
                throw new AlletraException($"Array {Endpoint}: {method} {path} did not finish within {TaskTimeout.TotalMinutes:0} minutes (task {taskPath}).");
            }

            await Task.Delay(TaskPoll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The task's last message: <c>detailsMap</c> holds numbered entries (<c>{"0": {"time", "message"}, "1": …}</c>),
    /// the last one says why it failed.
    /// </summary>
    private static string TaskMessage(JsonObject task) =>
        (task["detailsMap"] is JsonObject details
            ? details.Where(kv => kv.Value is JsonObject).OrderBy(kv => int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : int.MaxValue)
                .Select(kv => Readable(kv.Value!["message"])).LastOrDefault(m => m is not null) ?? Readable(details["message"])
            : null)
        ?? (task["state"]?["detailed"] is JsonArray detailed ? Readable(detailed.LastOrDefault()) : null)
        ?? "no message";

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        if (_key is null)
        {
            await LoginAsync(cancellationToken).ConfigureAwait(false);
        }

        var response = await SendWithRetryAsync(() => Request(method, path, body), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // The session expired (idle timeout): one new sign-in, then the call again.
        response.Dispose();
        await LoginAsync(cancellationToken).ConfigureAwait(false);
        return await SendWithRetryAsync(() => Request(method, path, body), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends, and again after a pause while the array says it is busy: HTTP 503 or 429, or an answer whose message says
    /// "busy", "too many sessions" and the like (the array did nothing then). A read is also tried again after a network
    /// error (a controller taking over the management address). The pause is the array's Retry-After where it gives one.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> request, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            var message = request();
            var isRead = message.Method == HttpMethod.Get;
            try
            {
                response = await SendRawAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (AlletraException ex) when (isRead && attempt < _retryDelays.Count && ex.InnerException is HttpRequestException)
            {
                waited += await PauseAsync(_retryDelays[attempt], cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response.IsSuccessStatusCode || !await IsBusyAsync(response, cancellationToken).ConfigureAwait(false))
            {
                return response;
            }

            if (attempt >= _retryDelays.Count)
            {
                _retried.AddOrUpdate(response, $" (still busy after {attempt} {(attempt == 1 ? "retry" : "retries")} over {waited.TotalSeconds:0} s)");
                return response;
            }

            var pause = response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero ? (delta < MaxRetryAfter ? delta : MaxRetryAfter) : _retryDelays[attempt];
            response.Dispose();
            waited += await PauseAsync(pause, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<TimeSpan> PauseAsync(TimeSpan pause, CancellationToken cancellationToken)
    {
        if (pause > TimeSpan.Zero)
        {
            await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
        }

        return pause;
    }

    /// <summary>Whether the answer says "busy, try later" (the body is kept for reading again).</summary>
    private static async Task<bool> IsBusyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
        {
            return false;
        }

        await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        return Busy().IsMatch(MessageOf(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) ?? string.Empty);
    }

    [GeneratedRegex(@"\bbusy\b|too many (sessions|requests|connections)|maximum (number of )?(sessions|connections)|session limit|try (it )?(again )?later|temporarily unavailable", RegexOptions.IgnoreCase)]
    private static partial Regex Busy();

    [GeneratedRegex(@"permission|privilege|not authori[sz]ed|insufficient (rights|privileges)|access (is )?denied|perm_denied", RegexOptions.IgnoreCase)]
    private static partial Regex Denied();

    /// <summary>The message of an error body ("message", "desc", …), or null.</summary>
    private static string? MessageOf(string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            return Readable(node?["message"]) ?? Readable(node?["desc"]) ?? Readable(node?["description"]) ?? Readable(node?["detail"]);
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
    }

    /// <summary>
    /// An empty body that carries the media type: the array refuses any /api/v3 request without one, a GET too
    /// (HTTP 400 MISSING_MEDIA_TYPE_HEADER, seen on a B10000 with OS 10.5).
    /// </summary>
    private static ByteArrayContent EmptyJson()
    {
        var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? EmptyJson() : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            try
            {
                return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new AlletraException($"Array {Endpoint}: {request.Method} {request.RequestUri} failed: {ex.Message}", null, ex);
            }
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string method, string path, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = MessageOf(text) ?? response.ReasonPhrase ?? "no message";
        var said = $"HTTP {(int)response.StatusCode}: {message}";
        var retried = _retried.TryGetValue(response, out var note) ? note : string.Empty;
        if (response.StatusCode == HttpStatusCode.Forbidden || Denied().IsMatch(message))
        {
            throw new AlletraException($"Array {Endpoint}: {method} {path} was refused for lack of rights ({said}); the account {User} has {PrivilegeSummary}.", response.StatusCode)
            {
                PermissionDenied = true,
                ArrayMessage = said,
            };
        }

        throw new AlletraException($"Array {Endpoint}: {method} {path} answered {said}{retried}", response.StatusCode) { ArrayMessage = said };
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static string? LastSegment(Uri? location) =>
        location is null ? null : (location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString).TrimEnd('/').Split('/')[^1] is { Length: > 0 } last ? last : null;
}
