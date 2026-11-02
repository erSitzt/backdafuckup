using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Polly;
using Polly.Retry;

namespace Backdafuckup.Xen.Xapi;

public sealed record XapiLoginOptions
{
    public required string Address { get; init; }

    public required string Username { get; init; }

    public required string Password { get; init; }

    /// <summary>Creates a transport to a given host (applies the host's certificate pin).</summary>
    public required Func<string, XapiTransport> TransportFactory { get; init; }

    /// <summary>Other known pool hosts to try when the given address is unreachable (master failover).</summary>
    public IReadOnlyList<string> FallbackAddresses { get; init; } = [];

    public string Originator { get; init; } = "backdafuckup";

    public int RetryAttempts { get; init; } = 10;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// A logged-in XAPI session on the pool master. Follows HOST_IS_SLAVE redirects, re-logs in on SESSION_INVALID,
/// and retries idempotent calls on transient errors (never creates or snapshots).
/// </summary>
public sealed partial class XapiSession : IAsyncDisposable
{
    private static readonly HashSet<string> RetriableMutations = new(StringComparer.Ordinal)
    {
        "VDI.destroy", "VDI.data_destroy", "VM.destroy", "task.destroy", "task.cancel", "VDI.enable_cbt",
        "VM.add_to_other_config", "VM.remove_from_other_config", "VDI.add_to_other_config", "VDI.remove_from_other_config",
        "session.logout",
    };

    private readonly XapiLoginOptions _options;
    private readonly ResiliencePipeline _retry;
    private readonly SemaphoreSlim _reloginLock = new(1, 1);
    private XapiTransport _transport;
    private string _sessionRef;

    private XapiSession(XapiLoginOptions options, XapiTransport transport, string sessionRef)
    {
        _options = options;
        _transport = transport;
        _sessionRef = sessionRef;
        _retry = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>().Handle<IOException>().Handle<SocketException>()
                    .Handle<TaskCanceledException>(ex => ex.InnerException is TimeoutException),
                MaxRetryAttempts = Math.Max(0, options.RetryAttempts),
                Delay = options.RetryDelay,
                BackoffType = DelayBackoffType.Constant,
            })
            .Build();
    }

    public string MasterAddress => _transport.Host;

    public XapiTransport Transport => _transport;

    /// <summary>The opaque session reference (needed for HTTP handlers and NBD). Never log it.</summary>
    public string SessionRef => _sessionRef;

    public static async Task<XapiSession> LoginAsync(XapiLoginOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var (transport, sessionRef) = await LoginCoreAsync(options, cancellationToken).ConfigureAwait(false);
        return new XapiSession(options, transport, sessionRef);
    }

    /// <summary>Calls a session-scoped XAPI method (the session reference is prepended automatically).</summary>
    public async Task<JsonNode?> CallAsync(string method, CancellationToken cancellationToken, params JsonNode?[] args)
    {
        var retriable = IsRetriable(method);
        for (var relogin = 0; ; relogin++)
        {
            try
            {
                var session = _sessionRef;
                var parameters = new JsonNode?[args.Length + 1];
                parameters[0] = session;
                args.CopyTo(parameters, 1);
                return retriable
                    ? await _retry.ExecuteAsync(async ct => await _transport.CallAsync(method, parameters, ct).ConfigureAwait(false), cancellationToken).ConfigureAwait(false)
                    : await _transport.CallAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (XapiException ex) when (ex.Code == "SESSION_INVALID" && relogin == 0)
            {
                await ReloginAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<string> CallStringAsync(string method, CancellationToken cancellationToken, params JsonNode?[] args) =>
        (await CallAsync(method, cancellationToken, args).ConfigureAwait(false))?.GetValue<string>() ?? string.Empty;

    public async Task<Dictionary<string, JsonObject>> GetAllRecordsAsync(string xapiClass, CancellationToken cancellationToken)
    {
        var result = await CallAsync($"{xapiClass}.get_all_records", cancellationToken).ConfigureAwait(false) as JsonObject ?? [];
        return result.Where(kv => kv.Value is JsonObject).ToDictionary(kv => kv.Key, kv => (JsonObject)kv.Value!);
    }

    public async Task<JsonObject> GetRecordAsync(string xapiClass, string reference, CancellationToken cancellationToken) =>
        await CallAsync($"{xapiClass}.get_record", cancellationToken, reference).ConfigureAwait(false) as JsonObject
        ?? throw new XapiException("HANDLE_INVALID", [xapiClass, reference], $"{xapiClass}.get_record");

    /// <summary>Returns the reference for a UUID, or null if the object does not exist.</summary>
    public async Task<string?> TryGetByUuidAsync(string xapiClass, string uuid, CancellationToken cancellationToken)
    {
        try
        {
            return await CallStringAsync($"{xapiClass}.get_by_uuid", cancellationToken, uuid).ConfigureAwait(false);
        }
        catch (XapiException ex) when (ex.Code == "UUID_INVALID")
        {
            return null;
        }
    }

    /// <summary>Starts an Async.* call and waits for the task; returns the task result (decoded reference or raw value).</summary>
    public Task<string> RunTaskAsync(string method, Func<string, Task>? onTaskCreated, TimeSpan timeout, CancellationToken cancellationToken, params JsonNode?[] args) =>
        RunTaskAsync(method, onTaskCreated, timeout, DefaultTaskPollInterval, cancellationToken, args);

    /// <summary>
    /// As above, polling the task every <paramref name="pollInterval"/>: short while every second counts (the VM snapshot
    /// inside the guest freeze, where VSS holds the writes for at most 10 s).
    /// </summary>
    public async Task<string> RunTaskAsync(string method, Func<string, Task>? onTaskCreated, TimeSpan timeout, TimeSpan pollInterval, CancellationToken cancellationToken, params JsonNode?[] args)
    {
        var taskRef = await CallStringAsync($"Async.{method}", cancellationToken, args).ConfigureAwait(false);
        if (onTaskCreated is not null)
        {
            await onTaskCreated(taskRef).ConfigureAwait(false);
        }

        try
        {
            return await WaitForTaskAsync(taskRef, timeout, null, cancellationToken, pollInterval).ConfigureAwait(false);
        }
        finally
        {
            await TryDestroyTaskAsync(taskRef).ConfigureAwait(false);
        }
    }

    public static readonly TimeSpan DefaultTaskPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Polls a task until it finishes. Returns the decoded result; throws XapiException on failure.</summary>
    public async Task<string> WaitForTaskAsync(string taskRef, TimeSpan timeout, IProgress<double>? progress, CancellationToken cancellationToken, TimeSpan? pollInterval = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        while (true)
        {
            JsonObject record;
            try
            {
                record = await GetRecordAsync("task", taskRef, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"XAPI task did not finish within {timeout}.");
            }

            var status = record.Str("status");
            progress?.Report(record.Double("progress"));
            switch (status)
            {
                case "success":
                    return DecodeTaskResult(record.Str("result"));
                case "failure":
                    var info = record["error_info"] as JsonArray ?? [];
                    var values = info.Select(i => i?.ToString() ?? string.Empty).ToList();
                    throw new XapiException(values.Count > 0 ? values[0] : "TASK_FAILED", values.Skip(1).ToList(), $"task {record.Str("name_label")}");
                case "cancelled":
                    throw new OperationCanceledException($"XAPI task {record.Str("name_label")} was cancelled.");
            }

            try
            {
                await Task.Delay(pollInterval ?? DefaultTaskPollInterval, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"XAPI task did not finish within {timeout}.");
            }
        }
    }

    public async Task<string> CreateTaskAsync(string label, CancellationToken cancellationToken) =>
        await CallStringAsync("task.create", cancellationToken, label, "backdafuckup").ConfigureAwait(false);

    public async Task TryDestroyTaskAsync(string taskRef)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await CallAsync("task.destroy", timeout.Token, taskRef).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is XapiException or HttpRequestException or IOException or OperationCanceledException)
        {
            // Finished tasks are garbage-collected by XAPI eventually.
        }
    }

    /// <summary>
    /// Task results are XML-RPC encoded (e.g. <c>&lt;value&gt;OpaqueRef:...&lt;/value&gt;</c>).
    /// Returns the first OpaqueRef if present, otherwise the text content.
    /// </summary>
    public static string DecodeTaskResult(string? result)
    {
        if (string.IsNullOrEmpty(result))
        {
            return string.Empty;
        }

        var reference = OpaqueRefRegex().Match(result);
        if (reference.Success)
        {
            return reference.Value;
        }

        return TagRegex().Replace(result, string.Empty).Trim();
    }

    /// <summary>All OpaqueRefs contained in a task result (e.g. the VMs created by import_metadata).</summary>
    public static IReadOnlyList<string> DecodeTaskResultRefs(string? result) =>
        string.IsNullOrEmpty(result) ? [] : OpaqueRefRegex().Matches(result).Select(m => m.Value).Distinct().ToList();

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _transport.CallAsync("session.logout", [_sessionRef], timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is XapiException or HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            // Sessions expire on their own.
        }

        _transport.Dispose();
        _reloginLock.Dispose();
    }

    private async Task ReloginAsync(CancellationToken cancellationToken)
    {
        await _reloginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (transport, sessionRef) = await LoginCoreAsync(_options with { Address = _transport.Host }, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(transport, _transport))
            {
                var old = _transport;
                _transport = transport;
                old.Dispose();
            }

            _sessionRef = sessionRef;
        }
        finally
        {
            _reloginLock.Release();
        }
    }

    private static async Task<(XapiTransport Transport, string SessionRef)> LoginCoreAsync(XapiLoginOptions options, CancellationToken cancellationToken)
    {
        var candidates = new List<string> { options.Address };
        candidates.AddRange(options.FallbackAddresses.Where(a => !string.Equals(a, options.Address, StringComparison.OrdinalIgnoreCase)));
        Exception? last = null;

        foreach (var candidate in candidates)
        {
            var address = candidate;
            for (var redirects = 0; redirects < 3; redirects++)
            {
                var transport = options.TransportFactory(address);
                try
                {
                    var result = await transport.CallAsync(
                        "session.login_with_password",
                        [options.Username, options.Password, "1.0", options.Originator],
                        cancellationToken).ConfigureAwait(false);
                    return (transport, result?.GetValue<string>() ?? throw new XapiException("SESSION_AUTHENTICATION_FAILED", [], "session.login_with_password"));
                }
                catch (XapiException ex) when (ex.Code == "HOST_IS_SLAVE" && ex.Parameters.Count > 0)
                {
                    transport.Dispose();
                    address = ex.Parameters[0];
                    last = ex;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    transport.Dispose();
                    last = ex;
                    break; // try the next known host
                }
                catch
                {
                    transport.Dispose();
                    throw;
                }
            }
        }

        throw last switch
        {
            XapiException xe => xe,
            null => new InvalidOperationException("No XAPI host to connect to."),
            _ => new HttpRequestException($"Could not reach any XAPI host ({string.Join(", ", candidates)}): {last.Message}", last),
        };
    }

    private static bool IsRetriable(string method)
    {
        var name = method.Contains('.', StringComparison.Ordinal) ? method[(method.IndexOf('.', StringComparison.Ordinal) + 1)..] : method;
        return name.StartsWith("get_", StringComparison.Ordinal)
            || name is "list_changed_blocks" or "get_nbd_info" or "get_server_certificate"
            || RetriableMutations.Contains(method);
    }

    [GeneratedRegex("OpaqueRef:[0-9a-fA-F-]+")]
    private static partial Regex OpaqueRefRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();
}
