using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Linq;
using Backdafuckup.Agent.Protocol;
using Microsoft.Win32;

namespace Backdafuckup.Agent;

/// <summary>
/// A domain controller's Active Directory facts for the restore point: the database files (from the NTDS writer's
/// component, else the registry), the SYSVOL folder, and over LDAP to this DC (as Local System, i.e. the DC's computer
/// account) the naming contexts, invocation id, USN, functional levels, Recycle Bin and lifetimes. Whatever cannot be
/// read is named in <see cref="GuestAdDatabase.Error"/>; the backup goes on.
/// </summary>
internal static class WindowsAdFacts
{
    private const string RecycleBinFeature = "CN=Recycle Bin Feature,CN=Optional Features,CN=Directory Service,CN=Windows NT,CN=Services,";

    /// <param name="dataFiles">The NTDS writer's data files (folder and spec, e.g. <c>C:\Windows\NTDS\</c> + <c>ntds.dit</c>).</param>
    /// <param name="logFiles">The NTDS writer's log files (folder and spec, e.g. <c>edb*.log</c>).</param>
    public static GuestAdDatabase Read(IReadOnlyList<GuestSqlFile> dataFiles, IReadOnlyList<GuestSqlFile> logFiles)
    {
        var facts = new GuestAdDatabase();
        var problems = new List<string>();
        try
        {
            using (var parameters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\NTDS\Parameters"))
            {
                facts.DitPath = parameters?.GetValue("DSA Database file") as string;
                facts.LogPath = parameters?.GetValue("Database log files path") as string;
            }

            using (var netlogon = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Netlogon\Parameters"))
            {
                facts.SysvolPath = netlogon?.GetValue("SysVol") as string;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is System.IO.IOException)
        {
            problems.Add("registry: " + ex.Message);
        }

        // The writer's own description wins: it is what VSS put into the snapshot.
        if (dataFiles.FirstOrDefault(f => f.Spec.EndsWith(".dit", StringComparison.OrdinalIgnoreCase)) is { } dit)
        {
            facts.DitPath = System.IO.Path.Combine(dit.Path, dit.Spec);
        }

        if (logFiles.FirstOrDefault() is { } log)
        {
            facts.LogPath = log.Path.TrimEnd('\\');
        }

        try
        {
            ReadDirectory(facts);
        }
        catch (Exception ex) when (ex is LdapException || ex is DirectoryOperationException || ex is InvalidOperationException || ex is FormatException)
        {
            problems.Add("LDAP to this domain controller (localhost:389): " + ex.Message);
        }

        facts.Error = problems.Count == 0 ? null : string.Join("; ", problems);
        return facts;
    }

    private static void ReadDirectory(GuestAdDatabase facts)
    {
        using (var connection = new LdapConnection(new LdapDirectoryIdentifier("localhost", 389)))
        {
            connection.AuthType = AuthType.Negotiate;
            connection.SessionOptions.ProtocolVersion = 3;
            connection.Timeout = TimeSpan.FromSeconds(30);
            connection.Bind();

            var root = Single(connection, string.Empty, SearchScope.Base, "(objectClass=*)",
                "defaultNamingContext", "configurationNamingContext", "rootDomainNamingContext", "dsServiceName", "highestCommittedUSN",
                "domainFunctionality", "forestFunctionality", "dnsHostName", "ldapServiceName");
            facts.DomainDn = Text(root, "defaultNamingContext");
            facts.DsaDn = Text(root, "dsServiceName");
            facts.HighestCommittedUsn = long.TryParse(Text(root, "highestCommittedUSN"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var usn) ? usn : (long?)null;
            facts.DomainFunctionalLevel = int.TryParse(Text(root, "domainFunctionality"), out var domainLevel) ? domainLevel : (int?)null;
            facts.ForestFunctionalLevel = int.TryParse(Text(root, "forestFunctionality"), out var forestLevel) ? forestLevel : (int?)null;
            var configuration = Text(root, "configurationNamingContext") ?? string.Empty;
            facts.DomainDns = DnsName(facts.DomainDn);
            facts.Forest = DnsName(Text(root, "rootDomainNamingContext"));

            if (facts.DsaDn is { } dsa)
            {
                var settings = Single(connection, dsa, SearchScope.Base, "(objectClass=*)", "invocationId");
                if (settings.Attributes["invocationId"]?[0] is byte[] invocation && invocation.Length == 16)
                {
                    facts.InvocationId = new Guid(invocation).ToString();
                }
            }

            // The NetBIOS name of the domain: its crossRef in the partitions container.
            if (facts.DomainDn is { } domainDn && configuration.Length > 0)
            {
                var crossRef = connection.SendRequest(new SearchRequest("CN=Partitions," + configuration,
                    "(&(objectClass=crossRef)(nCName=" + Escape(domainDn) + "))", SearchScope.OneLevel, "nETBIOSName")) as SearchResponse;
                facts.DomainNetbios = crossRef?.Entries.Count > 0 ? Text(crossRef.Entries[0], "nETBIOSName") : null;
            }

            if (configuration.Length > 0)
            {
                var service = Single(connection, "CN=Directory Service,CN=Windows NT,CN=Services," + configuration, SearchScope.Base, "(objectClass=*)",
                    "tombstoneLifetime", "msDS-DeletedObjectLifetime");
                facts.TombstoneLifetimeDays = int.TryParse(Text(service, "tombstoneLifetime"), out var tombstone) ? tombstone : 60;
                facts.DeletedObjectLifetimeDays = int.TryParse(Text(service, "msDS-DeletedObjectLifetime"), out var deleted) ? deleted : facts.TombstoneLifetimeDays;

                // The Recycle Bin is enabled when its feature object names the partitions container as a scope.
                var feature = connection.SendRequest(new SearchRequest(RecycleBinFeature + configuration, "(objectClass=*)", SearchScope.Base, "msDS-EnabledFeatureBL")) as SearchResponse;
                facts.RecycleBin = feature?.Entries.Count > 0 && feature.Entries[0].Attributes["msDS-EnabledFeatureBL"]?.Count > 0;
            }
        }
    }

    private static SearchResultEntry Single(LdapConnection connection, string dn, SearchScope scope, string filter, params string[] attributes)
    {
        var response = (SearchResponse)connection.SendRequest(new SearchRequest(dn, filter, scope, attributes));
        if (response.Entries.Count == 0)
        {
            throw new InvalidOperationException($"no entry {(dn.Length == 0 ? "rootDSE" : dn)}");
        }

        return response.Entries[0];
    }

    private static string? Text(SearchResultEntry entry, string attribute) =>
        entry.Attributes[attribute] is { Count: > 0 } values ? values[0] as string ?? (values[0] is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null) : null;

    /// <summary><c>DC=bdfu-lab,DC=local</c> → <c>bdfu-lab.local</c>.</summary>
    internal static string? DnsName(string? dn) =>
        dn is null ? null : string.Join(".", dn.Split(',').Select(p => p.Trim()).Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(p => p.Substring(3)));

    /// <summary>RFC 4515 escaping of a value inside a filter.</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");
}
