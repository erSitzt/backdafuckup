using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Backdafuckup.Agent.Core;

namespace Backdafuckup.Agent;

/// <summary>
/// File restore on Windows: <c>\\?\</c> paths (long paths, also on .NET Framework 4.8 / Server 2012 R2), only local fixed
/// volumes, and owner, group and DACL from the backup with SeRestorePrivilege (the agent runs as Local System).
/// </summary>
internal sealed class WindowsRestoreFileSystem : PortableRestoreFileSystem
{
    private static readonly object PrivilegeLock = new object();
    private static bool _privilegesEnabled;

    /// <summary>
    /// A path guard against mistakes, not a security boundary (the server can update the agent anyway): only folders on
    /// local fixed volumes, never network drives, removable media or the agent's own folder.
    /// </summary>
    public override string? CheckRoot(string root)
    {
        DriveInfo drive;
        try
        {
            drive = new DriveInfo(root.Substring(0, 1));
        }
        catch (ArgumentException)
        {
            return "no drive letter";
        }

        if (drive.DriveType != DriveType.Fixed)
        {
            return $"{drive.Name} is a {drive.DriveType.ToString().ToLowerInvariant()} drive, not a local fixed volume";
        }

        if (!drive.IsReady)
        {
            return $"{drive.Name} is not ready";
        }

        return CheckPath(root);
    }

    /// <summary>The agent's own folder (program, agent.json, agent.key) is never restored over.</summary>
    public override string? CheckPath(string path)
    {
        var agentFolder = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        return WindowsPath.IsBelow(path, agentFolder) ? "the agent's own folder is not restored" : null;
    }

    public override void SetSecurity(string path, bool isDirectory, byte[] descriptor)
    {
        EnablePrivileges();

        // Owner, group and DACL; the SACL needs SeSecurityPrivilege and is not restored. Inherited entries of the stored
        // DACL are recomputed from the target's parent by Windows, as when copying with inheritance.
        const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;
        var mapped = Map(path);
        if (isDirectory)
        {
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorBinaryForm(descriptor, sections);
#if NET48
            Directory.SetAccessControl(mapped, security);
#else
            new DirectoryInfo(mapped).SetAccessControl(security);
#endif
        }
        else
        {
            var security = new FileSecurity();
            security.SetSecurityDescriptorBinaryForm(descriptor, sections);
#if NET48
            File.SetAccessControl(mapped, security);
#else
            new FileInfo(mapped).SetAccessControl(security);
#endif
        }
    }

    protected override string Map(string windowsPath)
    {
        if (!WindowsPath.IsValidAbsolute(windowsPath))
        {
            throw new ArgumentException($"'{windowsPath}' is not an absolute local path.");
        }

        return @"\\?\" + windowsPath;
    }

    /// <summary>SeRestorePrivilege sets any owner; SeTakeOwnershipPrivilege and SeBackupPrivilege help with locked-down folders.</summary>
    private static void EnablePrivileges()
    {
        lock (PrivilegeLock)
        {
            if (_privilegesEnabled)
            {
                return;
            }

            foreach (var name in new[] { "SeRestorePrivilege", "SeTakeOwnershipPrivilege", "SeBackupPrivilege" })
            {
                Enable(name);
            }

            _privilegesEnabled = true;
        }
    }

    private static void Enable(string privilege)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed");
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"LookupPrivilegeValue({privilege}) failed");
            }

            var state = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
            {
                throw new Win32Exception(ErrorNotAllAssigned, $"{privilege} is not held by the agent's account (it must run as Local System)");
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;
    private const int ErrorNotAllAssigned = 1300;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, uint bufferLength, IntPtr previous, IntPtr returnLength);
}
