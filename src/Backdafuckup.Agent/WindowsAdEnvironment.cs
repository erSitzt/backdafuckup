using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.DirectoryServices.Protocols;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent;

/// <summary>
/// The domain controller's AD tools: Windows' own <c>esentutl.exe</c> and <c>dsamain.exe</c> (AD Database Mounting Tool),
/// called with fixed arguments and quoted paths of our work folder only, and LDAP through System.DirectoryServices.Protocols.
/// </summary>
internal sealed class WindowsAdEnvironment : IAdEnvironment
{
    private static readonly string DsaMain = Path.Combine(Environment.SystemDirectory, "dsamain.exe");
    private static readonly string EsentUtl = Path.Combine(Environment.SystemDirectory, "esentutl.exe");

    private WindowsAdEnvironment(string hostName)
    {
        HostName = hostName;
        WorkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "backdafuckup", "ad");
    }

    public string WorkRoot { get; }

    public string HostName { get; }

    public string Join(string folder, string name) => Path.Combine(folder, name);

    /// <summary>The environment on a domain controller with the AD DS tools; null elsewhere (the log says why on a DC).</summary>
    public static WindowsAdEnvironment? TryCreate(AgentLog log)
    {
        if (WindowsHostInfo.WmiInt("SELECT DomainRole FROM Win32_ComputerSystem", "DomainRole") is not (4 or 5))
        {
            return null;
        }

        if (!File.Exists(DsaMain))
        {
            log.Write($"This is a domain controller, but {DsaMain} is missing: Active Directory explore sessions are not offered.");
            return null;
        }

        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var host = string.IsNullOrEmpty(properties.DomainName) ? properties.HostName : properties.HostName + "." + properties.DomainName;
        return new WindowsAdEnvironment(host);
    }

    public void CreateWorkFolder(string folder)
    {
        // The root too: only SYSTEM and Administrators may list the explore sessions' folders.
        Create(WorkRoot);
        Create(folder);
    }

    public long FreeBytes(string folder) => new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace;

    public void DeleteFolder(string folder)
    {
        if (!IsWorkFolder(folder))
        {
            throw new InvalidOperationException($"{folder} is not a work folder under {WorkRoot}; not deleted.");
        }

        try
        {
            foreach (var file in Directory.GetFiles(folder, "*", System.IO.SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(folder, true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            throw new IOException($"Deleting {folder} on {HostName} failed: {ex.Message}", ex);
        }
    }

    public bool FolderExists(string folder) => Directory.Exists(folder);

    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<string> WorkFolders() => Directory.Exists(WorkRoot) ? Directory.GetDirectories(WorkRoot) : Array.Empty<string>();

    public AdToolResult Recover(string folder)
    {
        // Soft recovery of the copy with its own logs (base name edb, as NTDS uses), in place.
        var arguments = $"/r edb /l \"{folder}\" /s \"{folder}\" /d \"{folder}\" /o";
        var output = new StringBuilder();
        using (var process = Start(EsentUtl, arguments, folder, line => { lock (output) { output.AppendLine(line); } }))
        {
            if (!process.WaitForExit((int)TimeSpan.FromMinutes(30).TotalMilliseconds))
            {
                process.Kill();
                return new AdToolResult { ExitCode = -1, Output = "esentutl did not finish within 30 minutes" };
            }

            process.WaitForExit();
            lock (output)
            {
                return new AdToolResult { ExitCode = process.ExitCode, Output = output.ToString() };
            }
        }
    }

    public int FreePort()
    {
        var start = 50000 + (new Random().Next(0, 250) * 4);
        for (var i = 0; i < 250; i++)
        {
            var port = 50000 + ((start - 50000 + (i * 4)) % 1000);
            if (Enumerable.Range(port, 4).All(Free))
            {
                return port;
            }
        }

        throw new InvalidOperationException($"No four free consecutive TCP ports between 50000 and 50999 on {HostName} for dsamain.");
    }

    public IAdServer StartServer(string ditPath, string logFolder, int port)
    {
        if (!IsWorkFolder(Path.GetDirectoryName(ditPath)!) || !IsWorkFolder(logFolder))
        {
            throw new InvalidOperationException($"{ditPath} is not in a work folder under {WorkRoot}.");
        }

        var server = new Server();
        var arguments = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "/dbpath \"{0}\" /logpath \"{1}\" /ldapport {2} /sslport {3} /gcport {4} /gcsslport {5} /allowUpgrade",
            ditPath, logFolder, port, port + 1, port + 2, port + 3);
        server.Process = Start(DsaMain, arguments, logFolder, server.Add);
        return server;
    }

    public bool IsServer(int processId)
    {
        try
        {
            using (var process = Process.GetProcessById(processId))
            {
                return string.Equals(process.ProcessName, "dsamain", StringComparison.OrdinalIgnoreCase) && !process.HasExited;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void StopServer(int processId)
    {
        try
        {
            using (var process = Process.GetProcessById(processId))
            {
                process.Kill();
                process.WaitForExit(30000);
            }
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
        }
    }

    public IAdDirectory Connect(int port, AdAccount account) => new WindowsAdDirectory(HostName, port, account);

    private bool IsWorkFolder(string folder)
    {
        var root = Path.GetFullPath(WorkRoot).TrimEnd('\\') + "\\";
        return Path.GetFullPath(folder).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void Create(string folder)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(administrators);
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), administrators })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }

        if (!Directory.Exists(folder))
        {
#if NET48
            Directory.CreateDirectory(folder, security);
#else
            new DirectoryInfo(folder).Create(security);
#endif
        }

#if NET48
        Directory.SetAccessControl(folder, security);
#else
        new DirectoryInfo(folder).SetAccessControl(security);
#endif
    }

    private static bool Free(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static Process Start(string file, string arguments, string workingDirectory, Action<string> output)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory,
            },
        };
        process.OutputDataReceived += (_, e) => { if (e.Data is { } line) output(line); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) output(line); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private sealed class Server : IAdServer
    {
        private readonly Queue<string> _lines = new Queue<string>();

        public Process Process { get; set; } = null!;

        public int ProcessId => Process.Id;

        public bool HasExited => Process.HasExited;

        public int? ExitCode => Process.HasExited ? Process.ExitCode : (int?)null;

        public string Output
        {
            get
            {
                lock (_lines)
                {
                    return string.Join(Environment.NewLine, _lines);
                }
            }
        }

        public void Add(string line)
        {
            lock (_lines)
            {
                _lines.Enqueue(line);
                while (_lines.Count > 50)
                {
                    _lines.Dequeue();
                }
            }
        }
    }
}

/// <summary>LDAP to this DC or to dsamain: Negotiate as the job's account, signed and sealed.</summary>
internal sealed class WindowsAdDirectory : IAdDirectory
{
    /// <summary>Attributes whose values are bytes (the rest is text when it is valid UTF-8).</summary>
    private static readonly HashSet<string> Binary = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "objectGUID", "parentGUID", "objectSid", "sIDHistory", "nTSecurityDescriptor", "userCertificate", "thumbnailPhoto", "jpegPhoto", "logonHours",
        "msDS-GenerationId", "mS-DS-ConsistencyGuid", "replPropertyMetaData", "replUpToDateVector", "dSASignature", "schemaIDGUID",
        "attributeSecurityGUID", "msExchMailboxGuid", "invocationId", "msDS-Cached-Membership", "userParameters", "msDFSR-ContentSetGuid",
        "msDFSR-ReplicationGroupGuid", "auditingPolicy", "msDS-AllowedToActOnBehalfOfOtherIdentity", "mSMQSignCertificates", "mSMQDigests",
        "msTPM-OwnerInformation", "msPKI-RoamingTimeStamp", "cACertificate", "terminalServer",
    };

    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly LdapConnection _connection;

    public WindowsAdDirectory(string host, int port, AdAccount account)
    {
        Endpoint = $"{host}:{port}";
        _connection = new LdapConnection(new LdapDirectoryIdentifier(host, port), Credential(account), AuthType.Negotiate);
        _connection.SessionOptions.ProtocolVersion = 3;
        _connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        _connection.SessionOptions.Signing = true;
        _connection.SessionOptions.Sealing = true;
        _connection.Timeout = TimeSpan.FromMinutes(5);
        try
        {
            _connection.Bind();
        }
        catch (LdapException ex)
        {
            _connection.Dispose();
            throw new AdDirectoryException($"Logging on to {Endpoint} as {account.UserName} failed: LDAP {ex.ErrorCode} {ex.Message} {ex.ServerErrorMessage}".TrimEnd(), ex.ErrorCode, ex);
        }
    }

    public string Endpoint { get; }

    public AdRecord RootDse() =>
        Search(string.Empty, "(objectClass=*)", AdScope.Base, new[] { "*" }, 1, null).Records.FirstOrDefault()
        ?? throw new AdDirectoryException($"{Endpoint} returned no rootDSE.");

    public AdPage Search(string baseDn, string filter, AdScope scope, IReadOnlyList<string> attributes, int pageSize, byte[]? cookie,
        bool showDeleted = false, bool extendedDn = false, bool fullValues = false)
    {
        var request = Request(baseDn, filter, scope, attributes, showDeleted, extendedDn);
        PageResultRequestControl? paging = null;
        if (scope != AdScope.Base)
        {
            paging = new PageResultRequestControl(pageSize) { Cookie = cookie ?? Array.Empty<byte>() };
            request.Controls.Add(paging);
        }

        var response = Send(request, baseDn);
        var page = new AdPage();
        foreach (SearchResultEntry entry in response.Entries)
        {
            var record = Record(entry);
            if (fullValues)
            {
                Complete(record, showDeleted, extendedDn);
            }

            page.Records.Add(record);
        }

        page.Cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie is { Length: > 0 } next ? next : null;
        return page;
    }

    public void Dispose() => _connection.Dispose();

    private static NetworkCredential Credential(AdAccount account)
    {
        var name = account.UserName;
        var slash = name.IndexOf('\\');
        return slash > 0
            ? new NetworkCredential(name.Substring(slash + 1), account.Password, name.Substring(0, slash))
            : new NetworkCredential(name, account.Password);
    }

    private static SearchRequest Request(string baseDn, string filter, AdScope scope, IReadOnlyList<string> attributes, bool showDeleted, bool extendedDn)
    {
        var request = new SearchRequest(baseDn, filter, scope switch
        {
            AdScope.Base => SearchScope.Base,
            AdScope.OneLevel => SearchScope.OneLevel,
            _ => SearchScope.Subtree,
        }, attributes.ToArray());
        if (showDeleted)
        {
            request.Controls.Add(new ShowDeletedControl());
        }

        if (extendedDn)
        {
            request.Controls.Add(new ExtendedDNControl(ExtendedDNFlag.StandardString));
        }

        return request;
    }

    private SearchResponse Send(SearchRequest request, string baseDn)
    {
        try
        {
            return (SearchResponse)_connection.SendRequest(request);
        }
        catch (DirectoryOperationException ex)
        {
            var code = (int)(ex.Response?.ResultCode ?? ResultCode.Other);
            throw new AdDirectoryException($"LDAP search on {Endpoint} below '{baseDn}' failed: {ex.Response?.ResultCode} ({code}) {ex.Message}", code, ex);
        }
        catch (LdapException ex)
        {
            throw new AdDirectoryException($"LDAP search on {Endpoint} below '{baseDn}' failed: {ex.ErrorCode} {ex.Message}", ex.ErrorCode, ex);
        }
    }

    /// <summary>Range retrieval ([MS-ADTS] 3.1.1.3.1.3.3): fetches the rest of every range-limited attribute.</summary>
    private void Complete(AdRecord record, bool showDeleted, bool extendedDn)
    {
        foreach (var name in record.Attributes.Keys.Where(k => k.IndexOf(";range=", StringComparison.OrdinalIgnoreCase) > 0).ToList())
        {
            var plain = name.Substring(0, name.IndexOf(';'));
            var values = new List<object>(record.Attributes[name]);
            record.Attributes.Remove(name);
            var range = name.Substring(name.IndexOf(";range=", StringComparison.OrdinalIgnoreCase) + 7);
            while (!range.EndsWith("*", StringComparison.Ordinal))
            {
                var high = int.Parse(range.Substring(range.IndexOf('-') + 1), System.Globalization.CultureInfo.InvariantCulture);
                var next = Send(Request(record.Dn, "(objectClass=*)", AdScope.Base, new[] { $"{plain};range={high + 1}-*" }, showDeleted, extendedDn), record.Dn);
                var entry = next.Entries.Cast<SearchResultEntry>().FirstOrDefault();
                var attribute = entry?.Attributes.AttributeNames.Cast<string>().FirstOrDefault(n => n.StartsWith(plain + ";range=", StringComparison.OrdinalIgnoreCase));
                if (entry is null || attribute is null)
                {
                    break;
                }

                values.AddRange(Values(attribute, entry.Attributes[attribute]));
                range = attribute.Substring(attribute.IndexOf(";range=", StringComparison.OrdinalIgnoreCase) + 7);
            }

            record.Attributes[plain] = values;
        }
    }

    private static AdRecord Record(SearchResultEntry entry)
    {
        var record = new AdRecord(AdText.ParseExtendedDn(entry.DistinguishedName).Dn);
        foreach (string name in entry.Attributes.AttributeNames)
        {
            record.Attributes[name] = Values(name, entry.Attributes[name]);
        }

        return record;
    }

    private static List<object> Values(string name, DirectoryAttribute attribute)
    {
        var raw = (byte[][])attribute.GetValues(typeof(byte[]));
        var plain = name.Split(';')[0];
        if (Binary.Contains(plain))
        {
            return raw.Cast<object>().ToList();
        }

        var values = new List<object>(raw.Length);
        foreach (var bytes in raw)
        {
            try
            {
                values.Add(StrictUtf8.GetString(bytes));
            }
            catch (DecoderFallbackException)
            {
                values.Add(bytes);
            }
        }

        return values;
    }
}
