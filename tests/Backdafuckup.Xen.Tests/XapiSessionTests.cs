using System.Net;
using System.Text.Json.Nodes;
using Backdafuckup.Testing.Xapi;
using Backdafuckup.Xen.Xapi;

namespace Backdafuckup.Xen.Tests;

[Trait("Category", "Unit")]
public sealed class XapiSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static XapiLoginOptions Options(FakeXapiHandler handler, string address = "master", params string[] fallbacks) => new()
    {
        Address = address,
        Username = "root",
        Password = "secret",
        TransportFactory = host => new XapiTransport(new Uri($"https://{host}/"), new NonDisposingHandler(handler)),
        FallbackAddresses = fallbacks,
        RetryAttempts = 2,
        RetryDelay = TimeSpan.FromMilliseconds(10),
    };

    [Fact]
    public async Task Logs_in_with_originator_and_prepends_the_session_to_calls()
    {
        JsonArray? loginParams = null;
        var handler = new FakeXapiHandler()
            .On("master", "session.login_with_password", p => { loginParams = p; return "OpaqueRef:session-1"; })
            .On("master", "VM.get_by_uuid", p => p[0]!.GetValue<string>() == "OpaqueRef:session-1" ? "OpaqueRef:vm" : throw new FakeXapiError("SESSION_INVALID"))
            .On("master", "session.logout", _ => null);

        await using (var session = await XapiSession.LoginAsync(Options(handler), Ct))
        {
            Assert.Equal("OpaqueRef:vm", await session.TryGetByUuidAsync("VM", "uuid", Ct));
            Assert.Equal("master", session.MasterAddress);
        }

        Assert.Equal(["root", "secret", "1.0", "backdafuckup"], loginParams!.Select(n => n!.GetValue<string>()));
        Assert.Equal(1, handler.CountCalls("session.logout"));
    }

    [Fact]
    public async Task Follows_host_is_slave_to_the_master()
    {
        var handler = new FakeXapiHandler()
            .On("member", "session.login_with_password", _ => throw new FakeXapiError("HOST_IS_SLAVE", "master"))
            .On("master", "session.login_with_password", _ => "OpaqueRef:s")
            .On("master", "pool.get_all_records", _ => new JsonObject());

        await using var session = await XapiSession.LoginAsync(Options(handler, "member"), Ct);

        Assert.Equal("master", session.MasterAddress);
        Assert.Empty(await session.GetAllRecordsAsync("pool", Ct));
    }

    [Fact]
    public async Task Fails_over_to_another_known_host_when_the_address_is_unreachable()
    {
        var handler = new FakeXapiHandler()
            .On("new-master", "session.login_with_password", _ => "OpaqueRef:s");
        var options = Options(handler, "dead", "new-master") with
        {
            TransportFactory = host => host == "dead"
                ? new XapiTransport(new Uri("https://dead/"), new ThrowingHandler())
                : new XapiTransport(new Uri($"https://{host}/"), new NonDisposingHandler(handler)),
        };

        await using var session = await XapiSession.LoginAsync(options, Ct);
        Assert.Equal("new-master", session.MasterAddress);
    }

    [Fact]
    public async Task Relogs_in_once_when_the_session_expired()
    {
        var logins = 0;
        var handler = new FakeXapiHandler()
            .On("master", "session.login_with_password", _ => $"OpaqueRef:s{++logins}")
            .On("master", "host.get_all_records", p => p[0]!.GetValue<string>() == "OpaqueRef:s1" ? throw new FakeXapiError("SESSION_INVALID", "OpaqueRef:s1") : new JsonObject());

        await using var session = await XapiSession.LoginAsync(Options(handler), Ct);
        await session.GetAllRecordsAsync("host", Ct);

        Assert.Equal(2, logins);
    }

    [Fact]
    public async Task Surfaces_api_errors_with_code_and_parameters()
    {
        var handler = new FakeXapiHandler()
            .On("master", "session.login_with_password", _ => "OpaqueRef:s")
            .On("master", "VDI.list_changed_blocks", _ => throw new FakeXapiError("VDI_NO_CBT_METADATA", "OpaqueRef:vdi"));

        await using var session = await XapiSession.LoginAsync(Options(handler), Ct);
        var ex = await Assert.ThrowsAsync<XapiException>(() => session.CallAsync("VDI.list_changed_blocks", Ct, "a", "b"));

        Assert.Equal("VDI_NO_CBT_METADATA", ex.Code);
        Assert.Equal(["OpaqueRef:vdi"], ex.Parameters);
    }

    [Fact]
    public async Task Retries_idempotent_calls_but_never_creates()
    {
        var getCalls = 0;
        var createCalls = 0;
        var handler = new FakeXapiHandler()
            .On("master", "session.login_with_password", _ => "OpaqueRef:s");
        var flaky = new FlakyHandler(handler, req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("VM.get_record", StringComparison.Ordinal) && ++getCalls == 1)
            {
                return true;
            }

            if (body.Contains("VDI.create", StringComparison.Ordinal))
            {
                createCalls++;
                return true;
            }

            return false;
        });
        handler.On("master", "VM.get_record", _ => new JsonObject { ["uuid"] = "u" });
        var options = Options(handler) with { TransportFactory = host => new XapiTransport(new Uri($"https://{host}/"), flaky) };

        await using var session = await XapiSession.LoginAsync(options, Ct);
        var record = await session.GetRecordAsync("VM", "OpaqueRef:vm", Ct);
        Assert.Equal("u", record.Str("uuid"));
        Assert.Equal(2, getCalls);

        await Assert.ThrowsAsync<HttpRequestException>(() => session.CallAsync("VDI.create", Ct, new JsonObject()));
        Assert.Equal(1, createCalls);
    }

    [Fact]
    public async Task Waits_for_tasks_and_decodes_xml_rpc_results()
    {
        var polls = 0;
        var handler = new FakeXapiHandler()
            .On("master", "session.login_with_password", _ => "OpaqueRef:s")
            .On("master", "Async.VM.snapshot", _ => "OpaqueRef:task")
            .On("master", "task.get_record", _ => ++polls < 2
                ? new JsonObject { ["status"] = "pending", ["progress"] = 0.5 }
                : new JsonObject { ["status"] = "success", ["result"] = "<value>OpaqueRef:1234-abcd</value>" })
            .On("master", "task.destroy", _ => null);

        await using var session = await XapiSession.LoginAsync(Options(handler), Ct);
        string? createdTask = null;
        var result = await session.RunTaskAsync("VM.snapshot", t => { createdTask = t; return Task.CompletedTask; }, TimeSpan.FromMinutes(1), Ct, "OpaqueRef:vm", "snap");

        Assert.Equal("OpaqueRef:1234-abcd", result);
        Assert.Equal("OpaqueRef:task", createdTask);
        Assert.Equal(1, handler.CountCalls("task.destroy"));
    }

    [Fact]
    public async Task Failed_tasks_raise_their_error_info()
    {
        var handler = new FakeXapiHandler()
            .On("master", "session.login_with_password", _ => "OpaqueRef:s")
            .On("master", "task.get_record", _ => new JsonObject
            {
                ["status"] = "failure",
                ["name_label"] = "snapshot",
                ["error_info"] = new JsonArray("SR_FULL", "OpaqueRef:sr"),
            });

        await using var session = await XapiSession.LoginAsync(Options(handler), Ct);
        var ex = await Assert.ThrowsAsync<XapiException>(() => session.WaitForTaskAsync("OpaqueRef:t", TimeSpan.FromMinutes(1), null, Ct));
        Assert.Equal("SR_FULL", ex.Code);
    }

    [Fact]
    public void Decodes_multiple_refs_from_import_results()
    {
        var refs = XapiSession.DecodeTaskResultRefs("<value><array><data><value>OpaqueRef:aa-1</value><value>OpaqueRef:bb-2</value></data></array></value>");
        Assert.Equal(["OpaqueRef:aa-1", "OpaqueRef:bb-2"], refs);
    }

    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    private sealed class FlakyHandler(HttpMessageHandler inner, Func<HttpRequestMessage, bool> shouldFail) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await request.Content!.LoadIntoBufferAsync(cancellationToken);
            if (shouldFail(request))
            {
                throw new HttpRequestException("connection reset", null, HttpStatusCode.BadGateway);
            }

            return await base.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
