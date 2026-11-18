using System.Xml.Linq;

namespace Backdafuckup.VSphere.Vim;

/// <summary>What vCenter says about itself (<c>ServiceContent.about</c>).</summary>
public sealed record VimAbout(string FullName, string ApiType, string ApiVersion, string? InstanceUuid, string? Version, string? Build, string? LicenseProductName);

/// <summary>The managed objects every session starts from (<c>ServiceContent</c>).</summary>
public sealed record VimServiceContent(
    VimAbout About,
    ManagedObjectReference RootFolder,
    ManagedObjectReference PropertyCollector,
    ManagedObjectReference ViewManager,
    ManagedObjectReference SessionManager,
    ManagedObjectReference? SearchIndex,
    ManagedObjectReference? AuthorizationManager,
    ManagedObjectReference? FileManager,
    ManagedObjectReference? VirtualDiskManager,
    ManagedObjectReference? GuestOperationsManager)
{
    internal static VimServiceContent Parse(VimObject content)
    {
        var about = content.Child("about") ?? throw new InvalidDataException("ServiceContent without about.");
        return new VimServiceContent(
            new VimAbout(
                about.Str("fullName") ?? string.Empty, about.Str("apiType") ?? string.Empty, about.Str("apiVersion") ?? string.Empty,
                about.Str("instanceUuid"), about.Str("version"), about.Str("build"), about.Str("licenseProductName")),
            Required(content, "rootFolder"),
            Required(content, "propertyCollector"),
            Required(content, "viewManager"),
            Required(content, "sessionManager"),
            content.Ref("searchIndex"),
            content.Ref("authorizationManager"),
            content.Ref("fileManager"),
            content.Ref("virtualDiskManager"),
            content.Ref("guestOperationsManager"));
    }

    private static ManagedObjectReference Required(VimObject content, string name) =>
        content.Ref(name) ?? throw new InvalidDataException($"ServiceContent without {name}.");
}

/// <summary>A managed object with the properties asked for (a path missing from the dictionary was not set).</summary>
public sealed record VimObjectContent(ManagedObjectReference Obj, IReadOnlyDictionary<string, VimObject> Properties)
{
    public VimObject? this[string path] => Properties.GetValueOrDefault(path);

    public string? Str(string path) => Properties.GetValueOrDefault(path)?.Value;
}

/// <summary>
/// A logged-in vim25 session to a vCenter: calls methods, reads properties (with paging) and waits for tasks. A session
/// that vCenter dropped (<c>NotAuthenticated</c>) is logged in again once per call.
/// </summary>
public sealed class VimSession : IAsyncDisposable
{
    private const int PageSize = 500;
    private readonly string _userName;
    private readonly string _password;
    private readonly SemaphoreSlim _login = new(1, 1);
    private bool _disposed;

    private VimSession(VimTransport transport, VimServiceContent content, string userName, string password)
    {
        Transport = transport;
        Content = content;
        _userName = userName;
        _password = password;
    }

    public VimTransport Transport { get; }

    public VimServiceContent Content { get; }

    public string Endpoint => Transport.Endpoint;

    /// <summary>Negotiates the version, reads the service content and logs in. The transport belongs to the session afterwards.</summary>
    public static async Task<VimSession> ConnectAsync(VimTransport transport, string userName, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        try
        {
            await transport.NegotiateVersionAsync(cancellationToken).ConfigureAwait(false);
            var response = await transport.InvokeAsync(
                VimXml.E("RetrieveServiceContent", VimXml.This(new ManagedObjectReference("ServiceInstance", "ServiceInstance"))), cancellationToken).ConfigureAwait(false);
            var content = VimServiceContent.Parse(ReturnValue(response) ?? throw new InvalidDataException($"vCenter {transport.Endpoint}: RetrieveServiceContent returned nothing."));
            var session = new VimSession(transport, content, userName, password);
            await session.LoginAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    /// <summary>Calls <paramref name="method"/> on <paramref name="target"/>; the arguments are elements in the method's parameter order.</summary>
    public async Task<VimObject?> InvokeAsync(string method, ManagedObjectReference target, IEnumerable<object?> arguments, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = VimXml.E(method, VimXml.This(target), arguments);
        try
        {
            return ReturnValue(await Transport.InvokeAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (VimFaultException ex) when (ex.FaultType == "NotAuthenticated")
        {
            await LoginAsync(cancellationToken).ConfigureAwait(false);
            return ReturnValue(await Transport.InvokeAsync(request, cancellationToken).ConfigureAwait(false));
        }
    }

    public Task<VimObject?> InvokeAsync(string method, ManagedObjectReference target, CancellationToken cancellationToken, params object?[] arguments) =>
        InvokeAsync(method, target, arguments, cancellationToken);

    /// <summary>Starts a task-returning method (<c>…_Task</c>) and returns the task's reference.</summary>
    public async Task<ManagedObjectReference> StartTaskAsync(string method, ManagedObjectReference target, CancellationToken cancellationToken, params object?[] arguments) =>
        (await InvokeAsync(method, target, arguments, cancellationToken).ConfigureAwait(false))?.AsRef()
        ?? throw new InvalidDataException($"vCenter {Endpoint}: {method} on {target} returned no task.");

    /// <summary>
    /// Waits for a task and returns its result (or null). A failed task throws <see cref="VimTaskException"/> with vCenter's
    /// message and the object it ran on.
    /// </summary>
    public async Task<VimObject?> WaitForTaskAsync(ManagedObjectReference task, CancellationToken cancellationToken, IProgress<int>? progress = null)
    {
        var delay = TimeSpan.FromMilliseconds(200);
        while (true)
        {
            var info = (await RetrievePropertiesAsync([task], ["info.state", "info.error", "info.result", "info.progress", "info.descriptionId", "info.entityName"], cancellationToken)
                .ConfigureAwait(false)).FirstOrDefault()
                ?? throw new InvalidDataException($"vCenter {Endpoint}: task {task.Value} no longer exists.");
            switch (info.Str("info.state"))
            {
                case "success":
                    progress?.Report(100);
                    return info["info.result"];
                case "error":
                    var error = info["info.error"];
                    throw new VimTaskException(
                        info.Str("info.descriptionId") ?? task.Value,
                        info.Str("info.entityName"),
                        error?.Child("fault")?.Type ?? string.Empty,
                        error?.Str("localizedMessage") is { Length: > 0 } text ? text : "no message");
            }

            if (info["info.progress"] is { } percent && int.TryParse(percent.Value, out var value))
            {
                progress?.Report(value);
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(1000, delay.TotalMilliseconds * 1.5));
        }
    }

    /// <summary>Reads properties of the given objects (all of the same or of different types).</summary>
    public Task<IReadOnlyList<VimObjectContent>> RetrievePropertiesAsync(IReadOnlyCollection<ManagedObjectReference> objects, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var types = objects.Select(o => o.Type).Distinct(StringComparer.Ordinal).ToList();
        var spec = VimXml.E(
            "specSet",
            types.Select(type => VimXml.E("propSet", VimXml.E("type", type), paths.Select(p => VimXml.E("pathSet", p)))),
            objects.Select(o => VimXml.E("objectSet", VimXml.Ref("obj", o))));
        return RetrieveAsync(spec, cancellationToken);
    }

    /// <summary>
    /// Reads properties of every object of <paramref name="type"/> below <paramref name="container"/> (default: the root
    /// folder), through a temporary container view.
    /// </summary>
    public async Task<IReadOnlyList<VimObjectContent>> RetrieveAllAsync(string type, IReadOnlyList<string> paths, CancellationToken cancellationToken, ManagedObjectReference? container = null)
    {
        var view = (await InvokeAsync(
                "CreateContainerView", Content.ViewManager, cancellationToken,
                VimXml.Ref("container", container ?? Content.RootFolder), VimXml.E("type", type), VimXml.E("recursive", "true")).ConfigureAwait(false))?.AsRef()
            ?? throw new InvalidDataException($"vCenter {Endpoint}: CreateContainerView returned no view.");
        try
        {
            var spec = VimXml.E(
                "specSet",
                VimXml.E("propSet", VimXml.E("type", type), paths.Select(p => VimXml.E("pathSet", p))),
                VimXml.E(
                    "objectSet",
                    VimXml.Ref("obj", view),
                    VimXml.E("skip", "true"),
                    VimXml.Typed("selectSet", "TraversalSpec", VimXml.E("name", "view"), VimXml.E("type", "ContainerView"), VimXml.E("path", "view"), VimXml.E("skip", "false"))));
            return await RetrieveAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await InvokeAsync("DestroyView", view, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is VimFaultException or HttpRequestException or TimeoutException)
            {
                // The view ends with the session anyway.
            }
        }
    }

    /// <summary>Logs out (best effort) and releases the transport.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (Transport.HasSession)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await Transport.InvokeAsync(VimXml.E("Logout", VimXml.This(Content.SessionManager)), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is VimFaultException or HttpRequestException or TimeoutException or OperationCanceledException or InvalidDataException)
        {
            // vCenter ends idle sessions itself.
        }
        finally
        {
            Transport.Dispose();
            _login.Dispose();
        }
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        await _login.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Transport.ForgetSession();
            await Transport.InvokeAsync(
                VimXml.E("Login", VimXml.This(Content.SessionManager), VimXml.E("userName", _userName), VimXml.E("password", _password)), cancellationToken).ConfigureAwait(false);
        }
        catch (VimFaultException ex) when (ex.FaultType == "InvalidLogin")
        {
            throw new VimFaultException(ex.FaultType,
                $"the user name or password of '{_userName}' is wrong (a local SSO user needs its domain, e.g. user@vsphere.local)", "Login", Endpoint, ex.Detail);
        }
        finally
        {
            _login.Release();
        }
    }

    private async Task<IReadOnlyList<VimObjectContent>> RetrieveAsync(XElement specSet, CancellationToken cancellationToken)
    {
        var result = new List<VimObjectContent>();
        var page = await InvokeAsync(
            "RetrievePropertiesEx", Content.PropertyCollector, cancellationToken,
            specSet, VimXml.E("options", VimXml.E("maxObjects", PageSize))).ConfigureAwait(false);
        while (page is not null)
        {
            result.AddRange(page.Children("objects").Select(Parse));
            if (page.Str("token") is not { Length: > 0 } token)
            {
                break;
            }

            page = await InvokeAsync("ContinueRetrievePropertiesEx", Content.PropertyCollector, cancellationToken, VimXml.E("token", token)).ConfigureAwait(false);
        }

        return result;
    }

    private static VimObjectContent Parse(VimObject content)
    {
        var obj = content.Ref("obj") ?? throw new InvalidDataException("ObjectContent without obj.");
        var properties = new Dictionary<string, VimObject>(StringComparer.Ordinal);
        foreach (var property in content.Children("propSet"))
        {
            if (property.Str("name") is { } name && property.Child("val") is { } value)
            {
                properties[name] = value;
            }
        }

        return new VimObjectContent(obj, properties);
    }

    private static VimObject? ReturnValue(XElement response) => response.Element(VimXml.Ns + "returnval") is { } value ? new VimObject(value) : null;
}
