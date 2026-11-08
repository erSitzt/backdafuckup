using System.Text;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Tests;

/// <summary>
/// A directory in memory with an RFC 4515 filter subset (and, or, not, equality, presence, substrings): enough for the
/// agent's searches. DN-valued attributes are stored as extended DNs and returned plain without the control.
/// </summary>
internal sealed class InMemoryDirectory(string endpoint, string domainDn) : IAdDirectory
{
    private static readonly HashSet<string> DnAttributes = new(StringComparer.OrdinalIgnoreCase) { "member", "memberOf", "manager", "directReports" };

    public List<AdRecord> Records { get; } = [];

    public string Endpoint { get; } = endpoint;

    public List<string> Filters { get; } = [];

    public bool Disposed { get; private set; }

    public string SchemaDn => "CN=Schema,CN=Configuration," + domainDn;

    public AdRecord RootDse()
    {
        var root = new AdRecord(string.Empty);
        root.Attributes["defaultNamingContext"] = [domainDn];
        root.Attributes["configurationNamingContext"] = ["CN=Configuration," + domainDn];
        root.Attributes["schemaNamingContext"] = [SchemaDn];
        return root;
    }

    public AdRecord Add(string dn, Guid guid, params (string Name, object[] Values)[] attributes)
    {
        var record = new AdRecord(dn);
        record.Attributes["objectGUID"] = [guid.ToByteArray()];
        record.Attributes["distinguishedName"] = [dn];
        record.Attributes["name"] = [AdText.RdnValue(dn)];
        foreach (var (name, values) in attributes)
        {
            record.Attributes[name] = [.. values];
        }

        Records.Add(record);
        return record;
    }

    public AdPage Search(string baseDn, string filter, AdScope scope, IReadOnlyList<string> attributes, int pageSize, byte[]? cookie,
        bool showDeleted = false, bool extendedDn = false, bool fullValues = false)
    {
        Filters.Add(filter);
        var parsed = Parse(filter);
        var matches = Records
            .Where(r => InScope(r.Dn, baseDn, scope))
            .Where(r => showDeleted || !r.Flag("isDeleted"))
            .Where(r => parsed(r))
            .ToList();
        if (scope == AdScope.Base && matches.Count == 0 && !Records.Any(r => string.Equals(r.Dn, baseDn, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AdDirectoryException($"LDAP search on {Endpoint} below '{baseDn}' failed: noSuchObject (32)", 32);
        }

        var start = cookie is { Length: 4 } ? BitConverter.ToInt32(cookie) : 0;
        var page = new AdPage();
        foreach (var record in matches.Skip(start).Take(pageSize))
        {
            var copy = new AdRecord(record.Dn);
            foreach (var (name, values) in record.Attributes)
            {
                if (attributes.Contains("*") || attributes.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    copy.Attributes[name] = DnAttributes.Contains(name) && !extendedDn
                        ? values.Select(v => (object)AdText.ParseExtendedDn((string)v).Dn).ToList()
                        : [.. values];
                }
            }

            if (attributes.Contains("parentGUID", StringComparer.OrdinalIgnoreCase)
                && Records.FirstOrDefault(p => string.Equals(p.Dn, AdText.Parent(record.Dn), StringComparison.OrdinalIgnoreCase)) is { Guid: { } parent })
            {
                copy.Attributes["parentGUID"] = [parent.ToByteArray()];
            }

            page.Records.Add(copy);
        }

        page.Cookie = start + pageSize < matches.Count ? BitConverter.GetBytes(start + pageSize) : null;
        return page;
    }

    public void Dispose() => Disposed = true;

    /// <summary>Every write, in order: "add DN", "modify DN: names", "move DN → new DN".</summary>
    public List<string> Writes { get; } = [];

    /// <summary>The DC refuses to reanimate recycled objects (unwillingToPerform), as with the Recycle Bin.</summary>
    public bool RefuseRecycledReanimation { get; set; }

    /// <summary>Writes to these DNs fail with insufficientAccessRights (50).</summary>
    public Func<string, bool>? FailWrite { get; set; }

    private void Check(string dn, string operation)
    {
        if (FailWrite?.Invoke(dn) == true)
        {
            throw new AdDirectoryException($"LDAP {operation} of '{dn}' on {Endpoint} failed: InsufficientAccessRights (50)", 50);
        }
    }

    public void Add(string dn, IReadOnlyDictionary<string, List<object>> attributes)
    {
        Writes.Add($"add {dn}");
        Check(dn, "add");
        if (Find(dn, true) is not null)
        {
            throw new AdDirectoryException($"LDAP add of '{dn}' on {Endpoint} failed: EntryAlreadyExists (68)", 68);
        }

        if (AdText.Parent(dn) is not { } parent || Find(parent, false) is null)
        {
            throw new AdDirectoryException($"LDAP add of '{dn}' on {Endpoint} failed: NoSuchObject (32)", 32);
        }

        var record = Add(dn, Guid.NewGuid());
        foreach (var (name, values) in attributes)
        {
            record.Attributes[name] = [.. values.Select(v => Stored(name, v))];
        }
    }

    public void Modify(string dn, IReadOnlyList<AdModification> changes, bool showDeleted = false)
    {
        Writes.Add($"modify {dn}: {string.Join(", ", changes.Select(c => $"{c.Kind.ToString().ToLowerInvariant()} {c.Name}"))}");
        Check(dn, "modify");
        var record = Find(dn, showDeleted) ?? throw new AdDirectoryException($"LDAP modify of '{dn}' on {Endpoint} failed: NoSuchObject (32)", 32);
        var rest = changes.ToList();
        if (changes.Any(c => c.Kind == AdModificationKind.Delete && c.Name == "isDeleted")
            && changes.FirstOrDefault(c => c.Kind == AdModificationKind.Replace && c.Name == "distinguishedName") is { } target)
        {
            if (record.Flag("isRecycled") && RefuseRecycledReanimation)
            {
                throw new AdDirectoryException($"LDAP modify of '{dn}' on {Endpoint} failed: UnwillingToPerform (53)", 53);
            }

            var newDn = (string)target.Values[0];
            if (AdText.Parent(newDn) is not { } parent || Find(parent, false) is null)
            {
                throw new AdDirectoryException($"LDAP modify of '{dn}' on {Endpoint} failed: NoSuchObject (32), no parent {AdText.Parent(newDn)}", 32);
            }

            record = Rename(record, newDn);
            record.Attributes.Remove("isDeleted");
            record.Attributes.Remove("isRecycled");
            record.Attributes.Remove("lastKnownParent");
            rest = [.. changes.Where(c => c != target && c.Name != "isDeleted")];
        }

        foreach (var change in rest)
        {
            var values = change.Values.Select(v => Stored(change.Name, v)).ToList();
            var current = record.Values(change.Name);
            switch (change.Kind)
            {
                case AdModificationKind.Add:
                    if (values.Any(v => current.Any(c => Same(c, v))))
                    {
                        throw new AdDirectoryException($"LDAP modify of '{dn}' on {Endpoint} failed: AttributeOrValueExists (20)", 20);
                    }

                    record.Attributes[change.Name] = [.. current, .. values];
                    break;
                case AdModificationKind.Replace:
                    record.Attributes[change.Name] = values;
                    break;
                default:
                    if (values.Count == 0)
                    {
                        record.Attributes.Remove(change.Name);
                    }
                    else
                    {
                        record.Attributes[change.Name] = [.. current.Where(c => !values.Any(v => Same(c, v)))];
                    }

                    break;
            }
        }
    }

    public void Move(string dn, string newRdn, string newParentDn)
    {
        Writes.Add($"move {dn} → {newRdn},{newParentDn}");
        var record = Find(dn, false) ?? throw new AdDirectoryException($"LDAP move of '{dn}' on {Endpoint} failed: NoSuchObject (32)", 32);
        Rename(record, newRdn + "," + newParentDn);
    }

    public byte[]? ReadSecurityDescriptor(string dn) => Find(dn, true)?.Bytes("nTSecurityDescriptor");

    public void WriteSecurityDescriptor(string dn, byte[] descriptor)
    {
        Writes.Add($"acl {dn}");
        Check(dn, "modify (permissions)");
        (Find(dn, false) ?? throw new AdDirectoryException($"LDAP modify of '{dn}' on {Endpoint} failed: NoSuchObject (32)", 32)).Attributes["nTSecurityDescriptor"] = [descriptor];
    }

    public AdRecord? Find(string dn, bool deletedToo) =>
        Records.FirstOrDefault(r => string.Equals(r.Dn, dn, StringComparison.OrdinalIgnoreCase) && (deletedToo || !r.Flag("isDeleted")));

    public AdRecord? Find(Guid guid) => Records.FirstOrDefault(r => r.Guid == guid);

    private AdRecord Rename(AdRecord record, string newDn)
    {
        var renamed = new AdRecord(newDn);
        foreach (var (name, values) in record.Attributes)
        {
            renamed.Attributes[name] = values;
        }

        renamed.Attributes["distinguishedName"] = [newDn];
        renamed.Attributes["name"] = [AdText.RdnValue(newDn)];
        Records[Records.IndexOf(record)] = renamed;
        return renamed;
    }

    /// <summary>A written DN value is stored as a link to its object (extended DN), as the directory keeps links by GUID.</summary>
    private object Stored(string name, object value) =>
        DnAttributes.Contains(name) && value is string dn && Find(dn, false) is { } target ? Link(target) : value;

    private static bool Same(object a, object b) => (a, b) switch
    {
        (string x, string y) => string.Equals(AdText.ParseExtendedDn(x).Guid?.ToString() ?? x, AdText.ParseExtendedDn(y).Guid?.ToString() ?? y, StringComparison.OrdinalIgnoreCase),
        (byte[] x, byte[] y) => x.SequenceEqual(y),
        _ => false,
    };

    /// <summary>An extended DN value pointing to <paramref name="record"/>.</summary>
    public static string Link(AdRecord record) => $"<GUID={record.Guid:D}>;{record.Dn}";

    private static bool InScope(string dn, string baseDn, AdScope scope)
    {
        if (baseDn.Length == 0)
        {
            return false;
        }

        return scope switch
        {
            AdScope.Base => string.Equals(dn, baseDn, StringComparison.OrdinalIgnoreCase),
            AdScope.OneLevel => string.Equals(AdText.Parent(dn), baseDn, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(dn, baseDn, StringComparison.OrdinalIgnoreCase) || dn.EndsWith("," + baseDn, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static Func<AdRecord, bool> Parse(string filter)
    {
        var index = 0;
        var result = ParseItem(filter, ref index);
        return result;
    }

    private static Func<AdRecord, bool> ParseItem(string filter, ref int index)
    {
        if (filter[index] != '(')
        {
            throw new FormatException($"Filter '{filter}': '(' expected at {index}.");
        }

        index++;
        var op = filter[index];
        if (op is '&' or '|' or '!')
        {
            index++;
            var parts = new List<Func<AdRecord, bool>>();
            while (filter[index] == '(')
            {
                parts.Add(ParseItem(filter, ref index));
            }

            index++; // ')'
            return op switch
            {
                '&' => r => parts.All(p => p(r)),
                '|' => r => parts.Any(p => p(r)),
                _ => r => !parts[0](r),
            };
        }

        var end = filter.IndexOf(')', index);
        var item = filter[index..end];
        index = end + 1;
        var equals = item.IndexOf('=');
        var name = item[..equals];
        var value = item[(equals + 1)..];
        if (value == "*")
        {
            return r => r.Values(name).Count > 0;
        }

        var parts2 = value.Split('*').Select(Unescape).ToArray();
        return r => r.Values(name).Any(v => Matches(v, parts2, name));
    }

    private static bool Matches(object value, byte[][] parts, string name)
    {
        var bytes = value switch
        {
            byte[] b => b,
            string s when DnAttributes.Contains(name) => Encoding.UTF8.GetBytes(AdText.ParseExtendedDn(s).Dn),
            string s => Encoding.UTF8.GetBytes(s),
            _ => [],
        };
        if (parts.Length == 1)
        {
            return value is byte[] ? bytes.SequenceEqual(parts[0]) : string.Equals(Encoding.UTF8.GetString(bytes), Encoding.UTF8.GetString(parts[0]), StringComparison.OrdinalIgnoreCase);
        }

        var text = Encoding.UTF8.GetString(bytes);
        var position = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = Encoding.UTF8.GetString(parts[i]);
            if (part.Length == 0)
            {
                continue;
            }

            var found = text.IndexOf(part, position, StringComparison.OrdinalIgnoreCase);
            if (found < 0 || (i == 0 && found != 0))
            {
                return false;
            }

            position = found + part.Length;
        }

        return parts[^1].Length == 0 || position == text.Length;
    }

    private static byte[] Unescape(string value)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }

        return [.. bytes];
    }
}

/// <summary>
/// dsamain and the DC's folders in a temporary directory; the backup and live directories in memory. With
/// <c>windowsSandbox</c> the paths are Windows paths (<c>C:\ProgramData\…</c>) mapped into that folder (C:\x → {sandbox}/C/x),
/// as the agent's file transfer in the server tests writes them.
/// </summary>
internal sealed class FakeAdEnvironment : IAdEnvironment
{
    private readonly HashSet<int> _running = [];
    private readonly string? _sandbox;
    private int _nextPid = 4242;

    public FakeAdEnvironment(string root, InMemoryDirectory backup, InMemoryDirectory live, bool windowsSandbox = false)
    {
        _sandbox = windowsSandbox ? root : null;
        WorkRoot = windowsSandbox ? @"C:\ProgramData\backdafuckup\ad" : Path.Combine(root, "ad");
        SysvolRoot = windowsSandbox ? @"C:\Windows\SYSVOL\sysvol" : Path.Combine(root, "sysvol");
        Backup = backup;
        Live = live;
    }

    /// <summary>The local path of a path as the agent sees it.</summary>
    public string Map(string path) =>
        _sandbox is null ? path : Path.Combine([_sandbox, path[..1], .. path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries)]);

    public string WorkRoot { get; }

    public string HostName => "dc1.bdfu-lab.local";

    public string Join(string folder, string name) => _sandbox is null ? Path.Combine(folder, name) : folder.TrimEnd('\\') + "\\" + name;

    public string? SysvolRoot { get; set; }

    public void CreatePolicyFolder(string folder) => Directory.CreateDirectory(Map(folder));

    public void MovePolicyFolder(string from, string to) => Directory.Move(Map(from), Map(to));

    public void DeletePolicyFolder(string folder)
    {
        Assert.True(folder.EndsWith(".bdfu-new", StringComparison.Ordinal) || folder.EndsWith(".bdfu-replaced", StringComparison.Ordinal), folder);
        Directory.Delete(Map(folder), true);
    }

    public byte[] ReadFile(string path) => File.ReadAllBytes(Map(path));

    public void WriteFile(string path, byte[] content) => File.WriteAllBytes(Map(path), content);

    public InMemoryDirectory Backup { get; }

    public InMemoryDirectory Live { get; }

    public long Free { get; set; } = 100L << 30;

    public AdToolResult Recovery { get; set; } = new() { ExitCode = 0, Output = "Operation completed successfully." };

    /// <summary>dsamain ends at once with this output instead of serving.</summary>
    public string? ServerFails { get; set; }

    public List<string> Started { get; } = [];

    public List<int> Stopped { get; } = [];

    public List<AdAccount> Accounts { get; } = [];

    public void CreateWorkFolder(string folder) => Directory.CreateDirectory(Map(folder));

    public long FreeBytes(string folder) => Free;

    public void DeleteFolder(string folder) => Directory.Delete(Map(folder), true);

    public bool FolderExists(string folder) => Directory.Exists(Map(folder));

    public bool FileExists(string path) => File.Exists(Map(path));

    public IReadOnlyList<string> WorkFolders() =>
        Directory.Exists(Map(WorkRoot)) ? [.. Directory.GetDirectories(Map(WorkRoot)).Select(d => Join(WorkRoot, Path.GetFileName(d)))] : [];

    public AdToolResult Recover(string folder) => Recovery;

    public int FreePort() => 50000;

    public IAdServer StartServer(string ditPath, string logFolder, int port)
    {
        Started.Add($"{ditPath}|{logFolder}|{port}");
        var pid = _nextPid++;
        if (ServerFails is null)
        {
            _running.Add(pid);
        }

        return new Server(pid, ServerFails);
    }

    public bool IsServer(int processId) => _running.Contains(processId);

    public void StopServer(int processId)
    {
        _running.Remove(processId);
        Stopped.Add(processId);
    }

    /// <summary>Simulates a dsamain left running by an agent that stopped.</summary>
    public void Running(int processId) => _running.Add(processId);

    public IAdDirectory Connect(int port, AdAccount account)
    {
        Accounts.Add(account);
        if (account.Password != "secret")
        {
            throw new AdDirectoryException($"Logging on to {HostName}:{port} as {account.UserName} failed: LDAP 49 The supplied credential is invalid.", 49);
        }

        if (port == 389)
        {
            return Live;
        }

        return _running.Count > 0 ? Backup : throw new AdDirectoryException($"{HostName}:{port}: the LDAP server is unavailable.", 81);
    }

    private sealed class Server(int pid, string? fails) : IAdServer
    {
        public int ProcessId => pid;

        public bool HasExited => fails is not null;

        public int? ExitCode => fails is null ? null : 1;

        public string Output => fails ?? string.Empty;
    }
}
