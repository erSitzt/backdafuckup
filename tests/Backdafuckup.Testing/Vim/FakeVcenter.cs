using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace Backdafuckup.Testing.Vim;

/// <summary>
/// A vCenter as the vim25 SOAP API shows it, in memory: service content, login with a session cookie, container views,
/// the property collector with paging, tasks, and handlers per method that tests add. Every request is checked against the
/// element order of the public XSD (<see cref="VimSchemaOrder"/>), as vCenter does.
/// </summary>
public sealed class FakeVcenter
{
    public static readonly XNamespace Ns = "urn:vim25";
    public static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Type, string Value), Dictionary<string, XElement>> _objects = [];
    private readonly HashSet<string> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<XElement>> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<XElement, XElement?>> _handlers = new(StringComparer.Ordinal);
    private int _next = 100;

    public FakeVcenter()
    {
        Add("Folder", "group-d1", ("name", Val.Str("Datacenters")));
        On("CurrentTime", _ => Val.Typed("returnval", "xsd:dateTime", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
        On("FindByUuid", request =>
        {
            var path = request.Element(Ns + "instanceUuid")?.Value == "true" ? "config.instanceUuid" : "config.uuid";
            var uuid = request.Element(Ns + "uuid")!.Value;
            var match = Objects("VirtualMachine").FirstOrDefault(vm => string.Equals(Get(vm, path)?.Value, uuid, StringComparison.OrdinalIgnoreCase));
            return match == default ? null : Val.Mor("returnval", match);
        });
    }

    public string UserName { get; set; } = "backup@vsphere.local";

    public string Password { get; set; } = "secret";

    public string Version { get; set; } = "7.0.3.0";

    public string InstanceUuid { get; set; } = "1c0306a4-870a-4572-9472-3fbdd6561a0a";

    /// <summary>"VirtualCenter", or "HostAgent" for a standalone ESXi host.</summary>
    public string ApiType { get; set; } = "VirtualCenter";

    public (string Type, string Value) RootFolder { get; } = ("Folder", "group-d1");

    /// <summary>Answers RetrievePropertiesEx in pages of at most this many objects (null: the request's maxObjects).</summary>
    public int? PageSize { get; set; }

    /// <summary>Polls of a task's state that still answer "running" before it finishes (exercises the client's wait loop).</summary>
    public int TaskPolls { get; set; }

    /// <summary>The method names of all requests, in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The request bodies, in order (for golden tests).</summary>
    public List<XElement> Requests { get; } = [];

    public HttpMessageHandler CreateHandler() => new Handler(this);

    /// <summary>Drops every session: the next call gets NotAuthenticated, as after a vCenter restart or an idle timeout.</summary>
    public void ExpireSessions()
    {
        lock (_lock)
        {
            _sessions.Clear();
        }
    }

    /// <summary>Adds a managed object with properties (path → value element, see <see cref="Val"/>).</summary>
    public (string Type, string Value) Add(string type, string value, params (string Path, XElement Value)[] properties)
    {
        lock (_lock)
        {
            var props = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var (path, val) in properties)
            {
                props[path] = val;
            }

            _objects[(type, value)] = props;
            return (type, value);
        }
    }

    public void Set((string Type, string Value) obj, string path, XElement? value)
    {
        lock (_lock)
        {
            var props = _objects[obj];
            if (value is null)
            {
                props.Remove(path);
            }
            else
            {
                props[path] = value;
            }
        }
    }

    public XElement? Get((string Type, string Value) obj, string path)
    {
        lock (_lock)
        {
            return _objects.TryGetValue(obj, out var props) && props.TryGetValue(path, out var value) ? value : null;
        }
    }

    public bool Exists((string Type, string Value) obj)
    {
        lock (_lock)
        {
            return _objects.ContainsKey(obj);
        }
    }

    public void Remove((string Type, string Value) obj)
    {
        lock (_lock)
        {
            _objects.Remove(obj);
        }
    }

    public IReadOnlyList<(string Type, string Value)> Objects(string type)
    {
        lock (_lock)
        {
            return [.. _objects.Keys.Where(k => k.Type == type).OrderBy(k => k.Value, StringComparer.Ordinal)];
        }
    }

    /// <summary>A new id with a prefix ("vm" gives "vm-123").</summary>
    public string NewId(string prefix) => $"{prefix}-{Interlocked.Increment(ref _next)}";

    /// <summary>Handles <paramref name="method"/>: gets the request element, returns the <c>returnval</c> (or null for void).</summary>
    public void On(string method, Func<XElement, XElement?> handler)
    {
        lock (_lock)
        {
            _handlers[method] = handler;
        }
    }

    /// <summary>
    /// A task on <paramref name="entity"/> that runs <paramref name="work"/> (result element or null). An exception of type
    /// <see cref="VimFault"/> makes the task end in error with that fault.
    /// </summary>
    public XElement StartTask((string Type, string Value) entity, string descriptionId, Func<XElement?> work)
    {
        var task = Add("Task", NewId("task"), ("info.descriptionId", Val.Str(descriptionId)), ("info.entityName", Val.Str(Name(entity) ?? entity.Value)),
            ("info.state", Val.Str("running")), ("info.progress", Val.Int(0)));
        Set(task, "polls", Val.Int(TaskPolls));
        try
        {
            var result = work();
            if (result is not null)
            {
                Set(task, "info.result", Rename(result, "val"));
            }

            Set(task, "done", Val.Str("success"));
        }
        catch (VimFault fault)
        {
            Set(task, "info.error", Val.Obj("val", "LocalizedMethodFault",
                new XElement(Ns + "fault", new XAttribute(Xsi + "type", fault.Type)),
                new XElement(Ns + "localizedMessage", fault.Message)));
            Set(task, "done", Val.Str("error"));
        }

        if (TaskPolls == 0)
        {
            Finish(task);
        }

        return Val.Mor("returnval", task);
    }

    public string? Name((string Type, string Value) obj) => Get(obj, "name")?.Value;

    private void Finish((string Type, string Value) task)
    {
        Set(task, "info.state", Get(task, "done"));
        Set(task, "info.progress", Val.Int(100));
    }

    private (HttpStatusCode Status, XElement Body, string? Cookie) Dispatch(XElement request, string? cookie)
    {
        var method = request.Name.LocalName;
        lock (_lock)
        {
            Calls.Add(method);
            Requests.Add(new XElement(request));
        }

        VimSchemaCheck.Check(request);
        if (method is not ("RetrieveServiceContent" or "Login") && (cookie is null || !_sessions.Contains(cookie)))
        {
            throw new VimFault("NotAuthenticated", "The session is not authenticated.");
        }

        string? newCookie = null;
        XElement? result;
        switch (method)
        {
            case "RetrieveServiceContent":
                result = ServiceContent();
                break;
            case "Login":
                if (request.Element(Ns + "userName")?.Value != UserName || request.Element(Ns + "password")?.Value != Password)
                {
                    throw new VimFault("InvalidLogin", "Cannot complete login due to an incorrect user name or password.");
                }

                newCookie = $"\"{Guid.NewGuid():N}\"";
                lock (_lock)
                {
                    _sessions.Add(newCookie);
                }

                result = Val.Obj("returnval", "UserSession", new XElement(Ns + "key", Guid.NewGuid().ToString()), new XElement(Ns + "userName", UserName));
                break;
            case "Logout":
                lock (_lock)
                {
                    _sessions.Remove(cookie!);
                }

                result = null;
                break;
            case "CreateContainerView":
                result = CreateContainerView(request);
                break;
            case "DestroyView":
                Remove(Ref(request.Element(Ns + "_this")!));
                result = null;
                break;
            case "RetrievePropertiesEx":
                result = RetrieveProperties(request);
                break;
            case "ContinueRetrievePropertiesEx":
                result = NextPage(request.Element(Ns + "token")!.Value);
                break;
            default:
                Func<XElement, XElement?>? handler;
                lock (_lock)
                {
                    _handlers.TryGetValue(method, out handler);
                }

                result = handler is null
                    ? throw new VimFault("NotImplemented", $"The fake vCenter does not implement {method}.")
                    : handler(request);
                break;
        }

        var response = new XElement(Ns + $"{method}Response", result is null ? null : Rename(result, "returnval"));
        return (HttpStatusCode.OK, response, newCookie);
    }

    private XElement ServiceContent()
    {
        static XElement M(string name, string type, string value) => new(Ns + name, new XAttribute("type", type), value);
        return new XElement(
            Ns + "returnval",
            M("rootFolder", "Folder", RootFolder.Value),
            M("propertyCollector", "PropertyCollector", "propertyCollector"),
            M("viewManager", "ViewManager", "ViewManager"),
            new XElement(Ns + "about",
                new XElement(Ns + "name", "VMware vCenter Server"),
                new XElement(Ns + "fullName", $"VMware vCenter Server {Version[..5]} build-21477706"),
                new XElement(Ns + "vendor", "VMware, Inc."),
                new XElement(Ns + "version", Version[..5]),
                new XElement(Ns + "build", "21477706"),
                new XElement(Ns + "apiType", ApiType),
                new XElement(Ns + "apiVersion", Version),
                new XElement(Ns + "instanceUuid", InstanceUuid),
                new XElement(Ns + "licenseProductName", "VMware VirtualCenter Server")),
            M("sessionManager", "SessionManager", "SessionManager"),
            M("authorizationManager", "AuthorizationManager", "AuthorizationManager"),
            M("searchIndex", "SearchIndex", "SearchIndex"),
            M("fileManager", "FileManager", "FileManager"),
            M("virtualDiskManager", "VirtualDiskManager", "virtualDiskManager"),
            M("guestOperationsManager", "GuestOperationsManager", "guestOperationsManager"));
    }

    private XElement CreateContainerView(XElement request)
    {
        var container = Ref(request.Element(Ns + "container")!);
        var types = request.Elements(Ns + "type").Select(t => t.Value).ToHashSet(StringComparer.Ordinal);
        var recursive = request.Element(Ns + "recursive")?.Value == "true";
        List<(string Type, string Value)> members;
        lock (_lock)
        {
            members = [.. _objects.Keys.Where(k => types.Contains(k.Type) && Below(k, container, recursive)).OrderBy(k => k.Value, StringComparer.Ordinal)];
        }

        var view = Add("ContainerView", NewId("session[fake]view"));
        Set(view, "view", Val.Array("ArrayOfManagedObjectReference", members.Select(m => Val.Mor("ManagedObjectReference", m))));
        return Val.Mor("returnval", view);
    }

    private bool Below((string Type, string Value) obj, (string Type, string Value) container, bool recursive)
    {
        if (container == RootFolder)
        {
            return true;
        }

        var current = obj;
        for (var depth = 0; depth < 32; depth++)
        {
            if (!_objects.TryGetValue(current, out var props) || !props.TryGetValue("parent", out var parent))
            {
                return false;
            }

            current = Ref(parent);
            if (current == container)
            {
                return true;
            }

            if (!recursive)
            {
                return false;
            }
        }

        return false;
    }

    private XElement? RetrieveProperties(XElement request)
    {
        var spec = request.Element(Ns + "specSet")!;
        var wanted = spec.Elements(Ns + "propSet").ToDictionary(
            p => p.Element(Ns + "type")!.Value,
            p => p.Elements(Ns + "pathSet").Select(x => x.Value).ToList(),
            StringComparer.Ordinal);
        var targets = new List<(string Type, string Value)>();
        foreach (var objectSpec in spec.Elements(Ns + "objectSet"))
        {
            var obj = Ref(objectSpec.Element(Ns + "obj")!);
            if (objectSpec.Element(Ns + "skip")?.Value != "true")
            {
                targets.Add(obj);
            }

            foreach (var select in objectSpec.Elements(Ns + "selectSet"))
            {
                if (select.Element(Ns + "path")?.Value is { } path && Get(obj, path) is { } value)
                {
                    targets.AddRange(value.Elements().Select(Ref));
                }
            }
        }

        var objects = new List<XElement>();
        foreach (var target in targets.Distinct())
        {
            if (!wanted.TryGetValue(target.Type, out var paths))
            {
                continue;
            }

            lock (_lock)
            {
                if (!_objects.TryGetValue(target, out var props))
                {
                    continue;
                }

                if (target.Type == "Task" && props.TryGetValue("polls", out var polls))
                {
                    var left = int.Parse(polls.Value, CultureInfo.InvariantCulture);
                    if (left <= 0)
                    {
                        props["info.state"] = props["done"];
                        props["info.progress"] = Val.Int(100);
                    }
                    else
                    {
                        props["polls"] = Val.Int(left - 1);
                    }
                }

                objects.Add(new XElement(
                    Ns + "objects",
                    Val.Mor("obj", target),
                    paths.Where(props.ContainsKey).Select(p => new XElement(Ns + "propSet", new XElement(Ns + "name", p), Rename(props[p], "val")))));
            }
        }

        var max = PageSize ?? (int.TryParse(request.Element(Ns + "options")?.Element(Ns + "maxObjects")?.Value, out var m) ? m : int.MaxValue);
        var token = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            _pages[token] = new Queue<XElement>(objects);
        }

        return Page(token, max);
    }

    private XElement? NextPage(string token) => Page(token, PageSize ?? 500);

    private XElement? Page(string token, int max)
    {
        lock (_lock)
        {
            if (!_pages.TryGetValue(token, out var queue))
            {
                throw new VimFault("InvalidArgument", "A specified parameter was not correct: token");
            }

            var page = new List<XElement>();
            while (page.Count < max && queue.Count > 0)
            {
                page.Add(queue.Dequeue());
            }

            if (queue.Count == 0)
            {
                _pages.Remove(token);
            }

            return page.Count == 0 && queue.Count == 0
                ? null
                : new XElement(Ns + "returnval", queue.Count > 0 ? new XElement(Ns + "token", token) : null, page);
        }
    }

    internal static (string Type, string Value) Ref(XElement element) => (element.Attribute("type")!.Value, element.Value);

    private static XElement Rename(XElement element, string name) => new(Ns + name, element.Attributes(), element.Nodes());

    private sealed class Handler(FakeVcenter vcenter) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/sdk/vimServiceVersions.xml")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"<?xml version=\"1.0\" encoding=\"UTF-8\" ?><namespaces version=\"1.0\"><namespace><name>urn:vim25</name><version>{vcenter.Version}</version>" +
                        "<priorVersions><version>6.7</version></priorVersions></namespace></namespaces>", Encoding.UTF8, "text/xml"),
                };
            }

            if (request.Method != HttpMethod.Post || path != "/sdk")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (!request.Headers.TryGetValues("SOAPAction", out var actions) || actions.Single() != $"urn:vim25/{vcenter.Version}")
            {
                return Fault(new VimFault("InvalidRequest", "SOAPAction must name urn:vim25 and the version."));
            }

            var cookie = request.Headers.TryGetValues("Cookie", out var cookies)
                ? cookies.SelectMany(c => c.Split(';')).Select(c => c.Trim()).FirstOrDefault(c => c.StartsWith("vmware_soap_session=", StringComparison.Ordinal))?["vmware_soap_session=".Length..]
                : null;
            var text = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var body = XDocument.Parse(text).Root!.Element(Soap + "Body")!.Elements().Single();
            try
            {
                var (status, response, newCookie) = vcenter.Dispatch(body, cookie);
                var message = new HttpResponseMessage(status) { Content = Envelope(response) };
                if (newCookie is not null)
                {
                    message.Headers.TryAddWithoutValidation("Set-Cookie", $"vmware_soap_session={newCookie}; Path=/; HttpOnly; Secure;");
                }

                return message;
            }
            catch (VimFault fault)
            {
                return Fault(fault);
            }
        }

        private static StringContent Envelope(XElement body) => new(
            new XElement(Soap + "Envelope", new XAttribute(XNamespace.Xmlns + "soapenv", Soap.NamespaceName), new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
                new XAttribute("xmlns", Ns.NamespaceName), new XElement(Soap + "Body", body)).ToString(SaveOptions.DisableFormatting),
            Encoding.UTF8, "text/xml");

        private static HttpResponseMessage Fault(VimFault fault) => new(HttpStatusCode.InternalServerError)
        {
            Content = Envelope(new XElement(
                Soap + "Fault",
                new XElement("faultcode", "ServerFaultCode"),
                new XElement("faultstring", fault.Message),
                new XElement("detail", new XElement(Ns + $"{fault.Type}Fault", new XAttribute(Xsi + "type", fault.Type))))),
        };
    }
}

/// <summary>A vim25 fault the fake vCenter answers with (type such as "NotAuthenticated", "InvalidArgument").</summary>
public sealed class VimFault(string type, string message) : Exception(message)
{
    public string Type { get; } = type;
}

/// <summary>Value elements for the fake vCenter's properties and answers.</summary>
public static class Val
{
    public static XElement Str(string value) => Typed("val", "xsd:string", value);

    public static XElement Int(int value) => Typed("val", "xsd:int", value.ToString(CultureInfo.InvariantCulture));

    public static XElement Long(long value) => Typed("val", "xsd:long", value.ToString(CultureInfo.InvariantCulture));

    public static XElement Bool(bool value) => Typed("val", "xsd:boolean", value ? "true" : "false");

    public static XElement Mor(string type, string value) => Mor("val", (type, value));

    public static XElement Mor(string name, (string Type, string Value) obj) =>
        new(FakeVcenter.Ns + name, new XAttribute("type", obj.Type), obj.Value);

    public static XElement Typed(string name, string type, object content) =>
        new(FakeVcenter.Ns + name, new XAttribute(FakeVcenter.Xsi + "type", type), content);

    /// <summary>A data object: <c>&lt;name xsi:type="type"&gt;fields&lt;/name&gt;</c>.</summary>
    public static XElement Obj(string name, string type, params object?[] fields) =>
        new(FakeVcenter.Ns + name, new XAttribute(FakeVcenter.Xsi + "type", type), fields.Where(f => f is not null));

    /// <summary>A field of a data object.</summary>
    public static XElement F(string name, object? content) => new(FakeVcenter.Ns + name, content);

    public static XElement Array(string arrayType, IEnumerable<XElement> items) =>
        new(FakeVcenter.Ns + "val", new XAttribute(FakeVcenter.Xsi + "type", arrayType), items);
}
