using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

public enum AdScope
{
    Base,
    OneLevel,
    Subtree,
}

/// <summary>An LDAP entry: its DN and attributes (values are <see cref="string"/> or <see cref="byte"/>[]).</summary>
public sealed class AdRecord
{
    public AdRecord(string dn, Dictionary<string, List<object>>? attributes = null)
    {
        Dn = dn;
        Attributes = attributes ?? new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
    }

    public string Dn { get; }

    /// <summary>By attribute name (case-insensitive). Range-limited values keep their name (<c>member;range=0-1499</c>).</summary>
    public Dictionary<string, List<object>> Attributes { get; }

    public List<object> Values(string name) => Attributes.TryGetValue(name, out var values) ? values : new List<object>();

    public string? Text(string name) => Values(name).FirstOrDefault() switch
    {
        string text => text,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        _ => null,
    };

    public byte[]? Bytes(string name) => Values(name).FirstOrDefault() switch
    {
        byte[] bytes => bytes,
        string text => Encoding.UTF8.GetBytes(text),
        _ => null,
    };

    public bool Flag(string name) => string.Equals(Text(name), "TRUE", StringComparison.OrdinalIgnoreCase);

    public Guid? Guid => Bytes("objectGUID") is { Length: 16 } bytes ? new Guid(bytes) : (Guid?)null;

    /// <summary>The most specific object class (the last value of objectClass).</summary>
    public string ObjectClass => Values("objectClass").Select(v => v as string ?? Encoding.UTF8.GetString((byte[])v)).LastOrDefault() ?? string.Empty;
}

public sealed class AdPage
{
    public List<AdRecord> Records { get; } = new List<AdRecord>();

    /// <summary>The paging cookie for the next page; null: the last page.</summary>
    public byte[]? Cookie { get; set; }
}

/// <summary>An LDAP connection to a directory: the live AD (port 389) or a backup served by dsamain.</summary>
public interface IAdDirectory : IDisposable
{
    /// <summary>Host and port, for messages (e.g. <c>dc1.bdfu-lab.local:389</c>).</summary>
    string Endpoint { get; }

    AdRecord RootDse();

    /// <summary>
    /// One page of a search. <paramref name="showDeleted"/>: deleted and recycled objects too (Show-Deleted control);
    /// <paramref name="extendedDn"/>: DN values as <c>&lt;GUID=…&gt;;&lt;SID=…&gt;;dn</c>. <paramref name="fullValues"/>:
    /// multi-valued attributes beyond the server's limit are fetched completely (range retrieval). The records' own DNs
    /// are always plain (without the extended form). Throws
    /// <see cref="AdDirectoryException"/> naming the endpoint, the base DN and the LDAP result.
    /// </summary>
    AdPage Search(string baseDn, string filter, AdScope scope, IReadOnlyList<string> attributes, int pageSize, byte[]? cookie,
        bool showDeleted = false, bool extendedDn = false, bool fullValues = false);

    /// <summary>Creates an object (LDAP add).</summary>
    void Add(string dn, IReadOnlyDictionary<string, List<object>> attributes);

    /// <summary>
    /// Changes an object (LDAP modify). <paramref name="showDeleted"/>: the object is in Deleted Objects (reanimation:
    /// delete isDeleted, replace distinguishedName, [MS-ADTS] 3.1.1.5.3.7).
    /// </summary>
    void Modify(string dn, IReadOnlyList<AdModification> changes, bool showDeleted = false);

    /// <summary>Moves and/or renames an object (LDAP modify DN).</summary>
    void Move(string dn, string newRdn, string newParentDn);

    /// <summary>The object's DACL as a self-relative security descriptor (SD flags control: DACL only), also of a deleted object; null if none.</summary>
    byte[]? ReadSecurityDescriptor(string dn);

    /// <summary>Writes the object's DACL from a self-relative security descriptor (SD flags control: DACL only).</summary>
    void WriteSecurityDescriptor(string dn, byte[] descriptor);
}

public enum AdModificationKind
{
    Add,
    Delete,
    Replace,
}

/// <summary>One change of an LDAP modify: values are <see cref="string"/> or <see cref="byte"/>[]; Delete without values removes the attribute.</summary>
public sealed class AdModification
{
    public AdModification(AdModificationKind kind, string name, params object[] values)
    {
        Kind = kind;
        Name = name;
        Values = values.ToList();
    }

    public AdModificationKind Kind { get; }

    public string Name { get; }

    public List<object> Values { get; }
}

/// <summary>An LDAP error, with the endpoint, the DN and the result code in its message.</summary>
public sealed class AdDirectoryException : Exception
{
    public AdDirectoryException(string message, int resultCode = 0, Exception? inner = null)
        : base(message, inner) => ResultCode = resultCode;

    /// <summary>The LDAP result code (e.g. 32 noSuchObject, 49 invalidCredentials, 50 insufficientAccessRights).</summary>
    public int ResultCode { get; }
}

/// <summary>A dsamain process serving a backup's ntds.dit.</summary>
public interface IAdServer
{
    int ProcessId { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    /// <summary>The last lines dsamain wrote (for an error message).</summary>
    string Output { get; }
}

public sealed class AdToolResult
{
    public int ExitCode { get; set; }

    public string Output { get; set; } = string.Empty;
}

/// <summary>
/// The domain controller as an Active Directory job sees it. The Windows implementation runs esentutl and dsamain and
/// talks LDAP (System.DirectoryServices.Protocols); the tests use an in-memory directory.
/// </summary>
public interface IAdEnvironment
{
    /// <summary>The parent of the work folders, e.g. <c>C:\ProgramData\backdafuckup\ad</c>.</summary>
    string WorkRoot { get; }

    /// <summary>This DC's DNS host name.</summary>
    string HostName { get; }

    /// <summary>A path in a folder (Windows: <see cref="System.IO.Path.Combine(string, string)"/>).</summary>
    string Join(string folder, string name);

    /// <summary>Creates an empty folder that only SYSTEM and Administrators may access (no inherited entries).</summary>
    void CreateWorkFolder(string folder);

    long FreeBytes(string folder);

    /// <summary>Deletes the folder with everything in it; throws naming the path and the file that stayed.</summary>
    void DeleteFolder(string folder);

    bool FolderExists(string folder);

    bool FileExists(string path);

    /// <summary>The work folders under <see cref="WorkRoot"/>.</summary>
    IReadOnlyList<string> WorkFolders();

    /// <summary>ESE soft recovery of the copied database in <paramref name="folder"/> (<c>esentutl /r edb</c>).</summary>
    AdToolResult Recover(string folder);

    /// <summary>The first of four free consecutive TCP ports (dsamain uses LDAP, LDAPS, GC and GC-SSL ports).</summary>
    int FreePort();

    /// <summary>Starts dsamain for <paramref name="ditPath"/> with its logs in <paramref name="logFolder"/>, LDAP on <paramref name="port"/>.</summary>
    IAdServer StartServer(string ditPath, string logFolder, int port);

    /// <summary>Whether a dsamain process with this id runs (a journal's process id may belong to another program by now).</summary>
    bool IsServer(int processId);

    void StopServer(int processId);

    /// <summary>LDAP to this DC (<paramref name="port"/> 389) or to dsamain, as <paramref name="account"/>, signed and sealed.</summary>
    IAdDirectory Connect(int port, AdAccount account);

    /// <summary>This DC's SYSVOL share folder (<c>Netlogon\Parameters\SysVol</c>, e.g. <c>C:\Windows\SYSVOL\sysvol</c>); null if unknown.</summary>
    string? SysvolRoot { get; }

    /// <summary>
    /// Creates a GPO staging folder in SYSVOL (it inherits SYSVOL's permissions). Only <c>Policies\{GUID}.bdfu-new</c>.
    /// </summary>
    void CreatePolicyFolder(string folder);

    /// <summary>Renames a folder in SYSVOL's Policies folder (<c>{GUID}</c> and <c>{GUID}.bdfu-*</c> names only).</summary>
    void MovePolicyFolder(string from, string to);

    /// <summary>Deletes a GPO staging or replaced folder (<c>{GUID}.bdfu-new</c> or <c>{GUID}.bdfu-replaced</c> only).</summary>
    void DeletePolicyFolder(string folder);

    byte[] ReadFile(string path);

    /// <summary>Writes a file in a GPO staging folder (GPT.INI).</summary>
    void WriteFile(string path, byte[] content);
}

/// <summary>LDAP strings: filter escaping, extended DNs, SIDs and times.</summary>
public static class AdText
{
    /// <summary>RFC 4515 escaping of a value inside a filter.</summary>
    public static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\5c"); break;
                case '*': builder.Append("\\2a"); break;
                case '(': builder.Append("\\28"); break;
                case ')': builder.Append("\\29"); break;
                case '\0': builder.Append("\\00"); break;
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }

    /// <summary>A filter value for an octet string: every byte escaped (<c>\0a\1b…</c>).</summary>
    public static string EscapeBytes(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            builder.Append('\\').Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    public static string GuidFilter(Guid guid) => "(objectGUID=" + EscapeBytes(guid.ToByteArray()) + ")";

    /// <summary>
    /// Splits an extended DN (<c>&lt;GUID=…&gt;;&lt;SID=…&gt;;CN=…</c>, [MS-ADTS] 3.1.1.3.4.1.5, string form) into the GUID
    /// and the plain DN. Other values come back unchanged without a GUID.
    /// </summary>
    public static (Guid? Guid, string Dn) ParseExtendedDn(string value)
    {
        Guid? guid = null;
        var rest = value;
        while (rest.StartsWith("<", StringComparison.Ordinal))
        {
            var end = rest.IndexOf('>');
            if (end < 0)
            {
                break;
            }

            var part = rest.Substring(1, end - 1);
            if (part.StartsWith("GUID=", StringComparison.OrdinalIgnoreCase) && System.Guid.TryParse(part.Substring(5), out var parsed))
            {
                guid = parsed;
            }

            rest = rest.Substring(end + 1);
            if (rest.StartsWith(";", StringComparison.Ordinal))
            {
                rest = rest.Substring(1);
            }
        }

        return (guid, rest);
    }

    /// <summary>The string form of a binary SID ([MS-DTYP] 2.4.2.2), e.g. <c>S-1-5-21-…-1104</c>; null if malformed.</summary>
    public static string? Sid(byte[] sid)
    {
        if (sid.Length < 8 || sid.Length != 8 + (sid[1] * 4))
        {
            return null;
        }

        long authority = 0;
        for (var i = 2; i < 8; i++)
        {
            authority = (authority << 8) | sid[i];
        }

        var builder = new StringBuilder("S-").Append(sid[0].ToString(CultureInfo.InvariantCulture)).Append('-').Append(authority.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < sid[1]; i++)
        {
            builder.Append('-').Append(BitConverter.ToUInt32(sid, 8 + (i * 4)).ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary><c>DC=bdfu-lab,DC=local</c> → <c>bdfu-lab.local</c>.</summary>
    public static string DnsName(string dn) =>
        string.Join(".", dn.Split(',').Select(p => p.Trim()).Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(p => p.Substring(3)));

    /// <summary>The parent DN (everything after the first unescaped comma); null for a top-level DN.</summary>
    public static string? Parent(string dn)
    {
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\')
            {
                i++;
                continue;
            }

            if (dn[i] == ',')
            {
                return dn.Substring(i + 1);
            }
        }

        return null;
    }

    /// <summary>The value of the first RDN (<c>CN=lab-user01,OU=…</c> → <c>lab-user01</c>), unescaped for display.</summary>
    public static string RdnValue(string dn)
    {
        var parent = Parent(dn);
        var rdn = parent is null ? dn : dn.Substring(0, dn.Length - parent.Length - 1);
        var equals = rdn.IndexOf('=');
        var value = equals < 0 ? rdn : rdn.Substring(equals + 1);
        var deleted = value.IndexOf("\\0ADEL:", StringComparison.OrdinalIgnoreCase);
        if (deleted >= 0)
        {
            value = value.Substring(0, deleted);
        }

        return value.Replace("\\,", ",").Replace("\\+", "+").Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\#", "#").Replace("\\=", "=");
    }

    /// <summary>A FILETIME integer (pwdLastSet, accountExpires, lastLogonTimestamp) with its UTC time when it is one.</summary>
    public static string FileTime(string value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
        {
            return value;
        }

        if (ticks == 0)
        {
            return "0 (never)";
        }

        if (ticks == long.MaxValue)
        {
            return value + " (never expires)";
        }

        try
        {
            return value + " (" + DateTime.FromFileTimeUtc(ticks).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC)";
        }
        catch (ArgumentOutOfRangeException)
        {
            return value;
        }
    }
}
