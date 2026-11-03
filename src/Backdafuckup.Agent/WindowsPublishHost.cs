using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Backdafuckup.Agent.Core;

namespace Backdafuckup.Agent;

/// <summary>
/// Publishing on Windows: the built-in iSCSI initiator logs in to the agent's loopback target (not persistent), the
/// disks are brought online and writable (the writes go to the overlay), and the volumes are mounted into folders under
/// the agent's install folder (SYSTEM and administrators only), without drive letters. The steps run through the
/// iSCSI and Storage cmdlets (Windows Server 2012 and later).
/// </summary>
internal sealed class WindowsPublishHost : IPublishHost
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(2);
    private readonly object _lock = new object();
    private int _port;
    private int _initiatorPid = -1;
    private string? _initiatorName;
    private bool _startedService;

    public WindowsPublishHost(string agentDirectory) => WorkFolder = Path.Combine(agentDirectory, "publish");

    public string WorkFolder { get; }

    /// <summary>Exists while MSiSCSI runs only because a publish started it.</summary>
    private string ServiceMarker => Path.Combine(WorkFolder, "msiscsi-started-by-agent");

    /// <summary>
    /// Only the connection of the guest's own initiator: from 127.0.0.1, owned by the process of the MSiSCSI service,
    /// with the initiator's node name. Any other local process is refused, even with the right name.
    /// </summary>
    public bool Authorize(IPEndPoint initiator, string initiatorName)
    {
        int port;
        int pid;
        string? name;
        lock (_lock)
        {
            port = _port;
            pid = _initiatorPid;
            name = _initiatorName;
        }

        if (port == 0 || pid <= 0 || name is null || !IPAddress.IsLoopback(initiator.Address))
        {
            return false;
        }

        return string.Equals(initiatorName, name, StringComparison.OrdinalIgnoreCase) && TcpOwner.Pid(initiator, port) == pid;
    }

    public void Attach(int port, string targetName, Action<string> log)
    {
        var service = Ps(
            "$s = Get-Service MSiSCSI; if ($s.StartType -eq 'Disabled') { throw 'The Microsoft iSCSI Initiator Service (MSiSCSI) is disabled in this VM; publishing needs it (start type Manual is enough).' }; " +
            "if ($s.Status -ne 'Running') { Start-Service MSiSCSI; 'started' } else { 'running' }").Trim();
        if (service == "started")
        {
            // The marker survives an agent crash, so that the cleanup at the next start stops the service again too.
            _startedService = true;
            Directory.CreateDirectory(WorkFolder);
            File.WriteAllText(ServiceMarker, DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            log("started the Microsoft iSCSI Initiator Service (MSiSCSI); it is stopped again afterwards.");
        }

        var pid = int.Parse(Ps("(Get-CimInstance Win32_Service -Filter \"Name='MSiSCSI'\").ProcessId").Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var name = Ps(
            "$n = (Get-InitiatorPort -ErrorAction SilentlyContinue | Where-Object ConnectionType -eq 'iSCSI' | Select-Object -First 1).NodeAddress; " +
            "if (-not $n) { $n = (Get-CimInstance -Namespace root\\wmi -ClassName MSiSCSIInitiator_MethodClass).iSCSINodeName }; $n").Trim();
        if (pid <= 0 || name.Length == 0)
        {
            throw new InvalidOperationException("The iSCSI initiator of this VM could not be identified (service process or node name missing).");
        }

        lock (_lock)
        {
            _port = port;
            _initiatorPid = pid;
            _initiatorName = name;
        }

        Ps($"New-IscsiTargetPortal -TargetPortalAddress 127.0.0.1 -TargetPortalPortNumber {port} | Out-Null; " +
           $"Connect-IscsiTarget -NodeAddress {Quote(targetName)} -TargetPortalAddress 127.0.0.1 -TargetPortalPortNumber {port} -IsPersistent $false | Out-Null");
        log($"the iSCSI initiator {name} logged in to 127.0.0.1:{port} (not persistent).");
    }

    public int FindDisk(string serial, Action<string> log)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var number = Ps(
                "Update-HostStorageCache; " +
                $"(Get-Disk | Where-Object {{ $_.SerialNumber -and $_.SerialNumber.Trim() -eq {Quote(serial)} }} | Select-Object -First 1).Number").Trim();
            if (number.Length > 0)
            {
                log($"the published disk {serial} is disk {number}.");
                return int.Parse(number, System.Globalization.CultureInfo.InvariantCulture);
            }

            if (DateTime.UtcNow > deadline)
            {
                var disks = Ps("(Get-Disk | ForEach-Object { '' + $_.Number + ': ' + $_.FriendlyName + ' (' + $_.SerialNumber + ')' }) -join '; '").Trim();
                throw new InvalidOperationException($"The published disk {serial} did not arrive within 60 seconds. Disks in this VM: {disks}");
            }

            Thread.Sleep(TimeSpan.FromSeconds(2));
        }
    }

    public void Online(int disk, Action<string> log)
    {
        Ps($"Set-Disk -Number {disk} -IsOffline $false; Set-Disk -Number {disk} -IsReadOnly $false");

        // GPT partitions carry "no drive letter" (set by the identity patch); MBR partitions may get letters on online.
        var removed = Ps(
            $"Get-Partition -DiskNumber {disk} | Where-Object {{ ('' + $_.DriveLetter) -match '^[A-Za-z]$' }} | ForEach-Object {{ " +
            $"Remove-PartitionAccessPath -DiskNumber {disk} -PartitionNumber $_.PartitionNumber -AccessPath ('' + $_.DriveLetter + ':\\'); " +
            $"Set-Partition -DiskNumber {disk} -PartitionNumber $_.PartitionNumber -NoDefaultDriveLetter $true -ErrorAction SilentlyContinue; '' + $_.DriveLetter + ':' }}").Trim();
        log($"disk {disk} is online and writable (writes go to the overlay)" + (removed.Length > 0 ? $"; removed the drive letters Windows gave it ({removed.Replace(Environment.NewLine, ", ")})." : "."));
    }

    public void Mount(int disk, long offset, string folder, Action<string> log)
    {
        var path = folder.TrimEnd('\\') + "\\";
        Ps($"$p = Get-Partition -DiskNumber {disk} | Where-Object Offset -eq {offset} | Select-Object -First 1; " +
           $"if (-not $p) {{ throw 'Disk {disk} has no partition at offset {offset}.' }}; " +
           $"Add-PartitionAccessPath -DiskNumber {disk} -PartitionNumber $p.PartitionNumber -AccessPath {Quote(path)}");
        log($"mounted the partition at offset {offset} of disk {disk} to {folder}.");
    }

    /// <summary>
    /// Removes the mount point (Win32 API, no command line to quote). The volume itself is dismounted when its disk goes
    /// offline. A mount point whose volume is gone (VM restarted, session logged out) is only a folder with a reparse
    /// point; it is removed as such.
    /// </summary>
    public void Unmount(string folder, Action<string> log)
    {
        if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0)
        {
            return;
        }

        var path = folder.TrimEnd('\\') + "\\";
        var volume = new StringBuilder(64);
        var present = GetVolumeNameForVolumeMountPointW(path, volume, volume.Capacity);
        if (DeleteVolumeMountPointW(path))
        {
            log($"unmounted {folder}.");
            return;
        }

        var error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        if (present)
        {
            throw new InvalidOperationException($"The mount point {folder} of {volume} could not be removed: {error}");
        }

        Directory.Delete(folder, recursive: false);
        log($"removed the mount point {folder}; its volume is no longer there.");
    }

    public void Offline(int disk, Action<string> log) => Ps($"Set-Disk -Number {disk} -IsOffline $true");

    /// <summary>
    /// Allow ACEs (full control) on a file of a published volume, so that SQL Server (its service SID, the account it runs
    /// as, and a Windows account that attaches) can open it. The change lands in the overlay.
    /// </summary>
    public void GrantAccess(string path, System.Collections.Generic.IReadOnlyList<string> accounts, Action<string> log)
    {
        var rules = new System.Collections.Generic.List<System.Security.AccessControl.FileSystemAccessRule>();
        foreach (var account in accounts)
        {
            System.Security.Principal.SecurityIdentifier sid;
            try
            {
                sid = account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                    ? new System.Security.Principal.SecurityIdentifier(account)
                    : (System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount(account).Translate(typeof(System.Security.Principal.SecurityIdentifier));
            }
            catch (System.Security.Principal.IdentityNotMappedException)
            {
                throw new InvalidOperationException($"The account {account} is not known on this VM, so it cannot be given access to {path}.");
            }

            rules.Add(new System.Security.AccessControl.FileSystemAccessRule(
                sid, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        }

#if NET48
        var security = File.GetAccessControl(path, System.Security.AccessControl.AccessControlSections.Access);
        foreach (var rule in rules)
        {
            security.AddAccessRule(rule);
        }

        File.SetAccessControl(path, security);
#else
        var file = new FileInfo(path);
        var security = file.GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);
        foreach (var rule in rules)
        {
            security.AddAccessRule(rule);
        }

        file.SetAccessControl(security);
#endif
    }

    public void Detach(int port, string targetName, Action<string> log)
    {
        lock (_lock)
        {
            if (_port == port)
            {
                _port = 0;
            }
        }

        // Sessions and portals are only visible while MSiSCSI runs; after a restart of the VM it may be stopped while the
        // portal of a publish is still configured. The session's disks go offline first: a disk in use cannot be logged out.
        var stopAfterwards = _startedService || File.Exists(ServiceMarker) ? "$true" : "$false";
        _startedService = false;
        var portal = $"Get-IscsiTargetPortal -ErrorAction SilentlyContinue | Where-Object {{ $_.TargetPortalAddress -eq '127.0.0.1' -and $_.TargetPortalPortNumber -eq {port} }}";
        var sessions = $"Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object TargetNodeAddress -eq {Quote(targetName)}";
        var result = Ps(
            $"$stop = {stopAfterwards}; if ((Get-Service MSiSCSI).Status -ne 'Running') {{ Start-Service MSiSCSI; $stop = $true }}; $errors = @(); " +
            $"foreach ($s in @({sessions})) {{ " +
            "$s | Get-Disk -ErrorAction SilentlyContinue | Where-Object { -not $_.IsOffline } | ForEach-Object { try { Set-Disk -Number $_.Number -IsOffline $true } catch { $errors += 'offline disk ' + $_.Number + ': ' + $_.Exception.Message } }; " +
            "try { Disconnect-IscsiTarget -SessionIdentifier $s.SessionIdentifier -Confirm:$false } catch { $errors += $_.Exception.Message } }; " +
            $"if ({portal}) {{ iscsicli RemoveTargetPortal 127.0.0.1 {port} | Out-Null }}; " +
            $"$portals = @({portal}).Count; $left = @({sessions}).Count; $stopped = ''; " +
            "if ($stop -and @(Get-IscsiSession -ErrorAction SilentlyContinue).Count -eq 0) { Stop-Service MSiSCSI; $stopped = 'stopped' }; " +
            "'' + $portals + '|' + $left + '|' + $stopped + '|' + ($errors -join ' ')").Trim();
        var parts = result.Split(new[] { '|' }, 4);
        if (parts.Length < 4)
        {
            throw new InvalidOperationException($"Unexpected answer while logging out of {targetName}: {result}");
        }

        if (parts[2] == "stopped")
        {
            File.Delete(ServiceMarker);
            log("stopped the Microsoft iSCSI Initiator Service (MSiSCSI) again.");
        }

        if (parts[1] != "0")
        {
            throw new InvalidOperationException(
                $"The iSCSI session to {targetName} (127.0.0.1:{port}) could not be logged out: {parts[3].Trim()} It ends at the latest with the next restart of the VM; the agent tries again before its next publish and when it starts.");
        }

        if (parts[0] != "0")
        {
            throw new InvalidOperationException($"The iSCSI portal 127.0.0.1:{port} could not be removed; remove it with: iscsicli RemoveTargetPortal 127.0.0.1 {port}");
        }

        log($"logged out of {targetName} and removed the portal 127.0.0.1:{port}.");
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteVolumeMountPointW(string mountPoint);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int length);

    /// <summary>Runs a PowerShell script; its error message (not a stack of CLIXML) becomes the exception text.</summary>
    private static string Ps(string script)
    {
        var wrapped = "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; try { " + script + " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var (code, output, error) = Run(powershell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded);
        if (code != 0)
        {
            throw new InvalidOperationException(Clean(error).Length > 0 ? Clean(error) : $"PowerShell ended with code {code}.");
        }

        return output;
    }

    private static (int Code, string Output, string Error) Run(string file, string arguments)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using (var process = Process.Start(info)!)
        {
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit((int)StepTimeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                }

                throw new TimeoutException($"{Path.GetFileName(file)} did not finish within {StepTimeout.TotalMinutes:0} minutes.");
            }

            return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
    }

    /// <summary>Error text without PowerShell's CLIXML wrapping, if it used it.</summary>
    private static string Clean(string error)
    {
        if (!error.StartsWith("#< CLIXML", StringComparison.Ordinal))
        {
            return error.Trim();
        }

        var text = new StringBuilder();
        foreach (Match match in Regex.Matches(error, "<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline))
        {
            text.Append(WebUtility.HtmlDecode(match.Groups[1].Value).Replace("_x000D_", string.Empty).Replace("_x000A_", " "));
        }

        return text.ToString().Trim();
    }

    /// <summary>The process that owns the client end of a loopback TCP connection (IPv4).</summary>
    private static class TcpOwner
    {
        private const int AfInet = 2;
        private const int TcpTableOwnerPidAll = 5;

        public static int Pid(IPEndPoint client, int serverPort)
        {
            var size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    var result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
                    if (result == 122)
                    {
                        // ERROR_INSUFFICIENT_BUFFER: the table grew meanwhile; size holds the new size.
                        continue;
                    }

                    if (result != 0)
                    {
                        return -1;
                    }

                    var count = Marshal.ReadInt32(buffer);
                    for (var i = 0; i < count; i++)
                    {
                        // MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, pid.
                        var row = IntPtr.Add(buffer, 4 + i * 24);
                        if (Port(Marshal.ReadInt32(row, 8)) == client.Port && Port(Marshal.ReadInt32(row, 16)) == serverPort)
                        {
                            return Marshal.ReadInt32(row, 20);
                        }
                    }

                    return -1;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return -1;
        }

        private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);
    }
}
