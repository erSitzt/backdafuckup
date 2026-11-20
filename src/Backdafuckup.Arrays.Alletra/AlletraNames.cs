using System.Text.RegularExpressions;

namespace Backdafuckup.Arrays.Alletra;

/// <summary>Names the array takes for its objects, and iSCSI initiator names.</summary>
public static partial class AlletraNames
{
    /// <summary>A host, volume or domain name: 1 to 31 letters, digits, '.', '-' or '_', starting with a letter or digit.</summary>
    public static bool IsObjectName(string? name) => name is not null && ObjectName().IsMatch(name);

    /// <summary>An iSCSI name: <c>iqn.yyyy-mm.naming-authority[:unique]</c> or <c>eui.</c> with 16 hex digits.</summary>
    public static bool IsIscsiName(string? name) => name is not null && IscsiName().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,30}$")]
    private static partial Regex ObjectName();

    [GeneratedRegex(@"^(iqn\.[0-9]{4}-[0-9]{2}\.[A-Za-z0-9.-]+(:[^\s""'\\]+)?|eui\.[0-9A-Fa-f]{16})$")]
    private static partial Regex IscsiName();
}
