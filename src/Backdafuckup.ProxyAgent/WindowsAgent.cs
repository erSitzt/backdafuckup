using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Backdafuckup.Proxy;
using Backdafuckup.Proxy.Windows;

namespace Backdafuckup.ProxyAgent;

/// <summary>
/// The Windows agent as a service: install, uninstall, and the service that runs the agent's loop. After a self-update the
/// service stops with <see cref="RestartExitCode"/>; with <c>sc failureflag 1</c> the service manager counts that as a
/// failure and starts the new program (as the guest agent does).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAgent
{
    public const int RestartExitCode = 3;

    private static ProxyWindowsService? _service;

    public static void RunService(Func<CancellationToken, Task<int>> run) => ServiceBase.Run(_service = new ProxyWindowsService(run));

    /// <summary>The update is in place: stop with the restart code (outside the service: exit with it).</summary>
    public static void StopForUpdate(int code)
    {
        if (_service is { } service)
        {
            service.StopForUpdate(code);
        }
        else
        {
            Environment.Exit(code);
        }
    }

    public static async Task<int> InstallAsync(Func<CancellationToken, Task<int>> enroll, CancellationToken cancellationToken)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("The proxy agent installs as a service: run it in a PowerShell or command prompt started as administrator.");
            return 1;
        }

        var source = Environment.ProcessPath ?? throw new InvalidOperationException("The running program's path is unknown.");
        return await Setup().InstallAsync(source, enroll, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> UninstallAsync(CancellationToken cancellationToken)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("The proxy agent is removed as administrator.");
            return 1;
        }

        return await Setup().UninstallAsync(Environment.ProcessPath, RemoveLater, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Only SYSTEM and Administrators may read the folder with the agent's key.</summary>
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

    private static WindowsSetup Setup() => new(Commands.RunAsync, Console.WriteLine, AgentPaths.ProgramDirectory, AgentPaths.DataDirectory, ProtectDirectory);

    /// <summary>The program folder of the running uninstall: removed by a command prompt once this process has ended.</summary>
    private static void RemoveLater(string directory)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add($"ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{directory}\"");
        using var process = Process.Start(start);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || identity.IsSystem;
    }

    private sealed class ProxyWindowsService : ServiceBase
    {
        private readonly Func<CancellationToken, Task<int>> _run;
        private readonly CancellationTokenSource _stop = new();
        private Task? _running;

        public ProxyWindowsService(Func<CancellationToken, Task<int>> run)
        {
            _run = run;
            ServiceName = WindowsSetup.ServiceName;
            CanStop = true;
            CanShutdown = true;
        }

        public void StopForUpdate(int code) => StopWith(code);

        /// <summary>
        /// Stops the service with an exit code from another thread: OnStop waits for the agent's loop, which may be the
        /// caller. A non-zero exit code makes the service recovery start the (new) program.
        /// </summary>
        private void StopWith(int code) => _ = Task.Run(() =>
        {
            ExitCode = code;
            Stop();
        });

        protected override void OnStart(string[] args)
        {
            // Also here, so an agent installed by hand gets the restart after its self-update.
            _ = WindowsSetup.EnsureRecoveryAsync(Commands.RunAsync, CancellationToken.None);
            _running = Task.Run(async () =>
            {
                try
                {
                    await _run(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AgentLog.Write($"The agent stopped: {ex}");
                    StopWith(1);
                }
            });
        }

        protected override void OnStop()
        {
            _stop.Cancel();
            _running?.Wait(TimeSpan.FromSeconds(20));
        }

        protected override void OnShutdown() => OnStop();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stop.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
