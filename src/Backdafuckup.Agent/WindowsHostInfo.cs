using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent;

/// <summary>Host facts from WMI and the network stack.</summary>
internal sealed class WindowsHostInfo : IHostInfoSource
{
    private readonly AgentPaths _paths;
    private readonly Func<string> _vssProviderState;

    public WindowsHostInfo(AgentPaths paths, Func<string> vssProviderState)
    {
        _paths = paths;
        _vssProviderState = vssProviderState;
    }

    public static string Build =>
#if NET48
        AgentProtocol.BuildNet48;
#else
        AgentProtocol.BuildNet10;
#endif

    public static string Version =>
        typeof(WindowsHostInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    public AgentHostInfo Collect()
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        return new AgentHostInfo
        {
            HostName = Environment.MachineName,
            DnsName = string.IsNullOrEmpty(properties.DomainName) ? null : properties.HostName + "." + properties.DomainName,
            BiosSerial = Wmi("SELECT SerialNumber FROM Win32_BIOS", "SerialNumber"),
            SmbiosUuid = Wmi("SELECT UUID FROM Win32_ComputerSystemProduct", "UUID"),
            OsName = Wmi("SELECT Caption FROM Win32_OperatingSystem", "Caption"),
            OsVersion = Wmi("SELECT Version FROM Win32_OperatingSystem", "Version"),
            IpAddresses = Addresses(),
            Build = Build,
            AgentVersion = Version,
            FilesHash = AgentFiles.Hash(AgentFiles.List(_paths.BaseDirectory)),
            VssProvider = _vssProviderState(),
        };
    }

    /// <summary>Fixed volumes with their mount points (Win32_Volume).</summary>
    public static List<GuestVolume> Volumes()
    {
        var volumes = new List<GuestVolume>();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT DeviceID, Name, FileSystem, Label, Capacity FROM Win32_Volume WHERE DriveType = 3"))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject volume in results)
                {
                    using (volume)
                    {
                        volumes.Add(new GuestVolume
                        {
                            Name = volume["DeviceID"] as string ?? string.Empty,
                            MountPoints = volume["Name"] is string name && !name.StartsWith(@"\\?\", StringComparison.Ordinal) ? new List<string> { name } : new List<string>(),
                            FileSystem = volume["FileSystem"] as string,
                            Label = volume["Label"] as string,
                            SizeBytes = volume["Capacity"] is ulong capacity ? (long)capacity : 0,
                        });
                    }
                }
            }
        }
        catch (ManagementException)
        {
        }

        return volumes;
    }

    private static string? Wmi(string query, string property)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher(query))
            using (var results = searcher.Get())
            {
                foreach (ManagementBaseObject item in results)
                {
                    using (item)
                    {
                        if (item[property] is string value && value.Trim().Length > 0)
                        {
                            return value.Trim();
                        }
                    }
                }
            }
        }
        catch (ManagementException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static List<string> Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork || (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal))
            .Select(a => a.ToString())
            .Distinct()
            .ToList();
}
