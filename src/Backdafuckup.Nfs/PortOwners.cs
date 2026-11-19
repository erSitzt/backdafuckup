using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Backdafuckup.Nfs;

/// <summary>
/// Which process holds a local port, for the message of a failed bind ("port 111/tcp is in use by rpcbind (pid 812)").
/// Linux reads /proc/net and the processes' descriptors; Windows asks the IP helper API. Best effort: null when it cannot
/// be told (another user's process without permission to look, or an unknown platform).
/// </summary>
public static partial class PortOwners
{
    public static string? Describe(int port, ProtocolType protocol)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return DescribeLinux(port, protocol);
            }

            if (OperatingSystem.IsWindows())
            {
                return DescribeWindows(port, protocol);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or ArgumentException
            or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            // Diagnostics only.
        }

        return null;
    }

    private static string? DescribeLinux(int port, ProtocolType protocol)
    {
        // State 0A is LISTEN for TCP; an unconnected UDP socket shows 07 (TCP_CLOSE).
        var (files, state) = protocol == ProtocolType.Udp ? (new[] { "/proc/net/udp", "/proc/net/udp6" }, "07") : (new[] { "/proc/net/tcp", "/proc/net/tcp6" }, "0A");
        foreach (var file in files.Where(File.Exists))
        {
            foreach (var line in File.ReadLines(file).Skip(1))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 10 || fields[3] != state)
                {
                    continue;
                }

                var local = fields[1];
                var colon = local.LastIndexOf(':');
                if (colon < 0 || int.Parse(local.AsSpan(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture) != port)
                {
                    continue;
                }

                var inode = fields[9];
                var uid = fields[7];
                return FindProcessBySocket(inode) ?? $"a process of user {UserName(uid)} (uid {uid}) that this service may not look into";
            }
        }

        return null;
    }

    private static string? FindProcessBySocket(string inode)
    {
        var target = $"socket:[{inode}]";
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(directory);
            if (!int.TryParse(name, out var pid))
            {
                continue;
            }

            try
            {
                foreach (var fd in Directory.EnumerateFiles(Path.Combine(directory, "fd")))
                {
                    if (new FileInfo(fd).LinkTarget == target)
                    {
                        var command = File.ReadAllText(Path.Combine(directory, "comm")).Trim();
                        return $"{command} (pid {pid})";
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another user's process, or it ended meanwhile.
            }
        }

        return null;
    }

    private static string UserName(string uid)
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var fields = line.Split(':');
                if (fields.Length > 2 && fields[2] == uid)
                {
                    return fields[0];
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return uid;
    }

    private const uint AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int UdpTableOwnerPid = 1;

    private static string? DescribeWindows(int port, ProtocolType protocol)
    {
        var tcp = protocol != ProtocolType.Udp;
        var rowSize = tcp ? 24 : 12;
        var portOffset = tcp ? 8 : 4;
        var pidOffset = tcp ? 20 : 8;
        uint size = 0;
        _ = tcp ? GetExtendedTcpTable(null, ref size, false, AfInet, TcpTableOwnerPidListener, 0) : GetExtendedUdpTable(null, ref size, false, AfInet, UdpTableOwnerPid, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var table = new byte[size + 1024];
            size = (uint)table.Length;
            var result = tcp ? GetExtendedTcpTable(table, ref size, false, AfInet, TcpTableOwnerPidListener, 0) : GetExtendedUdpTable(table, ref size, false, AfInet, UdpTableOwnerPid, 0);
            if (result == 122)
            {
                continue; // ERROR_INSUFFICIENT_BUFFER: the table grew meanwhile.
            }

            if (result != 0)
            {
                return null;
            }

            var count = BitConverter.ToUInt32(table, 0);
            for (var i = 0; i < count; i++)
            {
                var row = 4 + (i * rowSize);
                // The port is in network byte order in the low 16 bits.
                var rowPort = (table[row + portOffset] << 8) | table[row + portOffset + 1];
                if (rowPort == port)
                {
                    return DescribeWindowsProcess((int)BitConverter.ToUInt32(table, row + pidOffset));
                }
            }

            return null;
        }

        return null;
    }

    private static string DescribeWindowsProcess(int pid)
    {
        if (pid == 4)
        {
            return "the Windows kernel (pid 4: a driver such as the Windows NFS server, \"Server for NFS\")";
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            var name = process.ProcessName;
            return string.Equals(name, "svchost", StringComparison.OrdinalIgnoreCase)
                ? $"svchost (pid {pid}; tasklist /svc /fi \"pid eq {pid}\" names the service)"
                : $"{name} (pid {pid})";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"process {pid}";
        }
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(byte[]? table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint addressFamily, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedUdpTable(byte[]? table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint addressFamily, int tableClass, uint reserved);
}
