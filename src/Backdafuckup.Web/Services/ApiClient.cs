using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backdafuckup.Contracts;

namespace Backdafuckup.Web.Services;

public static class ApiHeadersValue
{
    public const string Name = ApiHeaders.Csrf;
}

/// <summary>An API call failed; <see cref="Message"/> is the problem title/detail suitable for the user.</summary>
public sealed class ApiException(string message, HttpStatusCode status, string? body) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    public string? Body { get; } = body;
}

/// <summary>Typed access to the backdafuckup REST API (same origin, cookie auth, CSRF header set on the HttpClient).</summary>
public sealed class ApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Raised when the server answers 401 (session expired).</summary>
    public event Action? Unauthorized;

    public async Task<T> GetAsync<T>(string url)
    {
        using var response = await http.GetAsync(url);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    /// <summary>A text resource (e.g. a manifest), as it is.</summary>
    public async Task<string> GetStringAsync(string url)
    {
        using var response = await http.GetAsync(url);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<T> PostAsync<T>(string url, object? body = null)
    {
        using var response = await http.PostAsJsonAsync(url, body, Json);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public async Task PostAsync(string url, object? body = null)
    {
        using var response = await http.PostAsJsonAsync(url, body, Json);
        await EnsureSuccessAsync(response);
    }

    /// <summary>POST that returns the raw response (caller handles e.g. 409 with a body).</summary>
    public Task<HttpResponseMessage> PostRawAsync(string url, object? body) => http.PostAsJsonAsync(url, body, Json);

    public async Task<T> PutAsync<T>(string url, object body)
    {
        using var response = await http.PutAsJsonAsync(url, body, Json);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public async Task PutAsync(string url, object body)
    {
        using var response = await http.PutAsJsonAsync(url, body, Json);
        await EnsureSuccessAsync(response);
    }

    /// <summary>PUT or POST whose success may carry a note (<c>{ "message": … }</c>, e.g. 202 "waiting for …"); null without one.</summary>
    public async Task<string?> SendForMessageAsync(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body, options: Json) };
        using var response = await http.SendAsync(request);
        await EnsureSuccessAsync(response);
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var message) ? message.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string url)
    {
        using var response = await http.DeleteAsync(url);
        await EnsureSuccessAsync(response);
    }

    public static async Task<string> ReadProblemAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                return string.Join(" ", errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetString())));
            }

            if (root.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } d)
            {
                return d;
            }

            if (root.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } t)
            {
                return t;
            }
        }
        catch (JsonException)
        {
        }

        return $"Request failed ({(int)response.StatusCode} {response.ReasonPhrase}).";
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            Unauthorized?.Invoke();
        }

        throw new ApiException(await ReadProblemAsync(response), response.StatusCode, null);
    }
}
