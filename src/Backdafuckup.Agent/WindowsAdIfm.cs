using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;
using Microsoft.Win32;

namespace Backdafuckup.Agent;

/// <summary>
/// A consistent copy of a domain controller's directory for the VM snapshot, made with Windows' own
/// <c>ntdsutil "activate instance ntds" "ifm" "create full …"</c> (Install From Media). ntdsutil takes its own shadow copy
/// of the volume, so the copy does not depend on the 10 s VSS freeze of the VM snapshot, and AD records it as a backup
/// (dSASignature, event 1917). The folder holds every password hash and the boot key: only SYSTEM and Administrators may
/// open it, and it is deleted after the snapshot (and at agent start, if the agent stopped in between).
/// </summary>
internal static class WindowsAdIfm
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(25);

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "backdafuckup", "ad-ifm");

    /// <summary>Makes the copy into <see cref="Folder"/>, replacing an old one; the result says why if it could not.</summary>
    public static void Create(GuestAdDatabase facts, AgentLog log)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            Delete(log);
            var dit = facts.DitPath ?? LiveDit();
            var needed = (dit is not null && File.Exists(dit) ? new FileInfo(dit).Length : 0) + (1L << 30);
            var root = Path.GetPathRoot(Folder)!;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < needed)
            {
                facts.IfmError = $"not enough free space on {root} for the copy of the directory: {free / (1 << 20):N0} MiB free, {needed / (1 << 20):N0} MiB needed";
                return;
            }

            CreateProtected(Folder);
            var output = new StringBuilder();
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ntdsutil.exe"),
                $"\"activate instance ntds\" ifm \"create full {Folder}\" quit quit")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Folder,
            };
            using (var process = new Process { StartInfo = start })
            {
                process.OutputDataReceived += (_, e) => { if (e.Data is { } line) { lock (output) { output.AppendLine(line); } } };
                process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) { lock (output) { output.AppendLine(line); } } };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit((int)Limit.TotalMilliseconds))
                {
                    process.Kill();
                    facts.IfmError = $"ntdsutil did not finish the copy within {Limit.TotalMinutes:0} minutes";
                    Delete(log);
                    return;
                }

                process.WaitForExit();
            }

            var copy = Path.Combine(Folder, "Active Directory", "ntds.dit");
            if (!File.Exists(copy))
            {
                string text;
                lock (output)
                {
                    text = string.Join(" ", output.ToString().Replace("\r", string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Reverse().Take(4).Reverse());
                }

                facts.IfmError = $"ntdsutil made no copy of the directory: {text}";
                Delete(log);
                return;
            }

            facts.IfmPath = Folder;
            facts.IfmCreatedUtc = DateTime.UtcNow;
            facts.IfmSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
            facts.IfmBytes = new FileInfo(copy).Length;
            log.Write($"Directory copied with ntdsutil IFM into {Folder} in {facts.IfmSeconds:0.0} s ({facts.IfmBytes / (1 << 20):N0} MiB).");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
        {
            facts.IfmError = $"the copy of the directory with ntdsutil failed: {ex.Message}";
            Delete(log);
        }
    }

    /// <summary>Deletes the copy; a failure is logged with the path (the folder holds the domain's password hashes).</summary>
    public static void Delete(AgentLog log)
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.GetFiles(Folder, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Folder, true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            log.Write($"Error: the copy of the directory in {Folder} could not be deleted: {ex.Message} It holds the domain's password hashes; delete it by hand.");
        }
    }

    private static string? LiveDit()
    {
        using (var parameters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\NTDS\Parameters"))
        {
            return parameters?.GetValue("DSA Database file") as string;
        }
    }

    private static void CreateProtected(string folder)
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

#if NET48
        Directory.CreateDirectory(folder, security);
        Directory.SetAccessControl(folder, security);
#else
        new DirectoryInfo(folder).Create(security);
        new DirectoryInfo(folder).SetAccessControl(security);
#endif
    }
}
