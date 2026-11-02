using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent;

/// <summary>
/// Sets the agent up from a folder (setup package, e.g. group policy startup script or software distribution):
/// %ProgramFiles%\backdafuckup Agent (SYSTEM and administrators only), service BackdafuckupAgent as Local System with
/// restart on failure. The push installation of the server does the same over ADMIN$ and the service control manager.
/// </summary>
internal static class AgentInstaller
{
    public const string FolderName = "backdafuckup Agent";
    private const int NotElevated = 5;
    private const int Failed = 1;

    public static string TargetDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), FolderName);

    public static int Install(string sourceDirectory, string configFile, bool force)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("The agent can only be installed with administrator rights (or as Local System).");
            return NotElevated;
        }

        sourceDirectory = Path.GetFullPath(sourceDirectory);
        var target = TargetDirectory;
        if (!File.Exists(configFile))
        {
            Console.Error.WriteLine($"{configFile} is missing: download the agent setup from the backup server (Guest agents).");
            return 2;
        }

        if (string.Equals(sourceDirectory.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("The setup must not run from the installation folder.");
            return 2;
        }

        if (ServiceExists() && File.Exists(Path.Combine(target, AgentProtocol.ExecutableName)) && !force)
        {
            // An installed agent updates itself from the server; the setup must not reset it to an older state.
            Start();
            Console.WriteLine("The agent is already installed: " + target);
            return 0;
        }

        Stop();
        ProtectDirectory(target);
        foreach (var file in Directory.GetFiles(sourceDirectory).Where(f => AgentFiles.IsProgramFile(Path.GetFileName(f))))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }

        File.Copy(configFile, Path.Combine(target, AgentProtocol.ConfigFile), true);
        if (!ServiceExists())
        {
            var binPath = "\\\"" + Path.Combine(target, AgentProtocol.ExecutableName) + "\\\" run";
            if (Sc($"create {AgentProtocol.ServiceName} binPath= \"{binPath}\" start= auto obj= LocalSystem DisplayName= \"{AgentProtocol.ServiceDisplayName}\"") != 0)
            {
                Console.Error.WriteLine($"The service {AgentProtocol.ServiceName} could not be created.");
                return Failed;
            }

            Sc($"description {AgentProtocol.ServiceName} \"Freezes the guest through VSS while the backdafuckup backup server takes the VM snapshot.\"");
        }

        EnsureRecovery();
        Start();

        // The setup tries the .NET 10 build first: if that cannot run here, the service stops at once and the setup
        // script falls back to the .NET Framework 4.8 build.
        if (!StaysRunning(TimeSpan.FromSeconds(15)))
        {
            Console.Error.WriteLine($"The service {AgentProtocol.ServiceName} does not keep running; see {Path.Combine(target, AgentProtocol.LogFile)} and the event log.");
            return Failed;
        }

        Console.WriteLine("Agent installed: " + target);
        return 0;
    }

    /// <summary>
    /// Restart on failure, quickly the first time: this is how the agent comes back after a self-update (exit code 3).
    /// Also called by the service at every start, so a push installation (which only creates the service) gets it too.
    /// </summary>
    public static void EnsureRecovery()
    {
        Sc($"failure {AgentProtocol.ServiceName} reset= 3600 actions= restart/5000/restart/5000/restart/30000");
        Sc($"failureflag {AgentProtocol.ServiceName} 1");
    }

    public static int Uninstall()
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("The agent can only be removed with administrator rights (or as Local System).");
            return NotElevated;
        }

        try
        {
            NativeVss.Unregister();
        }
        catch (Exception ex) when (ex is VssException || ex is DllNotFoundException)
        {
            Console.Error.WriteLine(ex.Message);
        }

        if (ServiceExists())
        {
            Stop();
            if (Sc("delete " + AgentProtocol.ServiceName) != 0)
            {
                Console.Error.WriteLine($"The service {AgentProtocol.ServiceName} could not be deleted.");
                return Failed;
            }
        }

        try
        {
            if (Directory.Exists(TargetDirectory))
            {
                Directory.Delete(TargetDirectory, true);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"The folder {TargetDirectory} could not be removed completely: {ex.Message}");
            return Failed;
        }

        Console.WriteLine("Agent removed.");
        return 0;
    }

    /// <summary>The folder holds the enrollment secret and the agent key, and the service runs as SYSTEM: SYSTEM and administrators only.</summary>
    public static void ProtectDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        var directory = new DirectoryInfo(path);
        if (directory.Exists)
        {
            directory.SetAccessControl(security);
        }
        else
        {
            directory.Create(security);
        }
    }

    private static bool IsElevated()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || identity.IsSystem;
        }
    }

    private static bool ServiceExists() =>
        ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, AgentProtocol.ServiceName, StringComparison.OrdinalIgnoreCase));

    private static void Stop()
    {
        if (!ServiceExists())
        {
            return;
        }

        using (var service = new ServiceController(AgentProtocol.ServiceName))
        {
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                return;
            }

            if (service.Status != ServiceControllerStatus.StopPending)
            {
                service.Stop();
            }

            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
        }
    }

    private static void Start()
    {
        using (var service = new ServiceController(AgentProtocol.ServiceName))
        {
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                service.Start();
            }
        }
    }

    private static bool StaysRunning(TimeSpan time)
    {
        var deadline = DateTime.UtcNow + time;
        using (var service = new ServiceController(AgentProtocol.ServiceName))
        {
            while (DateTime.UtcNow < deadline)
            {
                service.Refresh();
                if (service.Status == ServiceControllerStatus.Stopped)
                {
                    return false;
                }

                Thread.Sleep(500);
            }

            service.Refresh();
            return service.Status == ServiceControllerStatus.Running;
        }
    }

    private static int Sc(string arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using (var process = Process.Start(info)!)
        {
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
    }
}
