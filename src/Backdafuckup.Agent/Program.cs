using System;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent;

/// <summary>
/// <c>backdafuckup-agent run</c> (service; <c>--console</c> for a test run), <c>install --source DIR --config FILE [--force]</c>,
/// <c>uninstall</c>, <c>info</c> (prints what the agent reports).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        try
        {
            switch (command)
            {
                case "run":
                    if (args.Contains("--console"))
                    {
                        using (var stop = new CancellationTokenSource())
                        {
                            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
                            return AgentHost.Run(echo: true, stop.Token);
                        }
                    }

                    var service = new AgentWindowsService();
                    ServiceBase.Run(service);
                    return service.ExitCode;
                case "install":
                    var source = Option(args, "--source") ?? AppDomain.CurrentDomain.BaseDirectory;
                    var config = Option(args, "--config") ?? Path.Combine(source, AgentProtocol.ConfigFile);
                    return AgentInstaller.Install(source, config, args.Contains("--force"));
                case "uninstall":
                    return AgentInstaller.Uninstall();
                case "info":
                    var paths = new AgentPaths(AppDomain.CurrentDomain.BaseDirectory);
                    var info = new WindowsHostInfo(paths, () => new NativeVss().ProviderState).Collect();
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(info, AgentJson.Options));
                    return 0;
                default:
                    Console.WriteLine("backdafuckup agent " + WindowsHostInfo.Version + " (" + WindowsHostInfo.Build + ")");
                    Console.WriteLine("  run [--console]                          run the agent (as the Windows service, or in this console)");
                    Console.WriteLine("  install --source DIR --config agent.json [--force]   install the service from DIR");
                    Console.WriteLine("  uninstall                                remove service, VSS provider and folder");
                    Console.WriteLine("  info                                     print what the agent reports to the server");
                    return command == "help" ? 0 : 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

/// <summary>Starts the agent loop: protects its folder, registers the VSS provider if needed, polls the server.</summary>
internal static class AgentHost
{
    private const string RegistryKey = @"SOFTWARE\backdafuckup\Agent";

    /// <summary>SHA-256 of the bdfu-vss.dll that was registered last (HKLM\SOFTWARE\backdafuckup\Agent).</summary>
    private static string? RegisteredDllHash()
    {
        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryKey))
        {
            return key?.GetValue("VssProviderDllHash") as string;
        }
    }

    private static void SaveRegisteredDllHash(string hash)
    {
        using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(RegistryKey))
        {
            key.SetValue("VssProviderDllHash", hash);
        }
    }

    public static int Run(bool echo, CancellationToken stop)
    {
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var paths = new AgentPaths(baseDirectory);
        var log = new AgentLog(paths.LogFile) { Echo = echo };
        log.Write($"backdafuckup agent {WindowsHostInfo.Version} ({WindowsHostInfo.Build}) starting in {baseDirectory}.");
        try
        {
            AgentInstaller.ProtectDirectory(baseDirectory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is InvalidOperationException)
        {
            log.Write("The folder permissions could not be set: " + ex.Message);
        }

        var vss = new NativeVss();
        var dll = Path.Combine(baseDirectory, AgentProtocol.VssDllName);
        var dllHash = File.Exists(dll) ? AgentFiles.Sha256Of(dll) : string.Empty;
        if (vss.ProviderState != "registered" || RegisteredDllHash() != dllHash)
        {
            // Not registered yet, or a new bdfu-vss.dll arrived with an update: register (again).
            try
            {
                NativeVss.Register(dll);
                SaveRegisteredDllHash(dllHash);
                log.Write("VSS provider registered (" + dllHash.Substring(0, Math.Min(12, dllHash.Length)) + ").");
            }
            catch (Exception ex) when (ex is VssException || ex is DllNotFoundException)
            {
                log.Write("The VSS provider could not be registered; backups of this VM stay crash-consistent: " + ex.Message);
            }
        }

#if NET48
        // .NET Framework allows 2 connections per host: the long poll, a file restore download and its progress report
        // need more at the same time.
        System.Net.ServicePointManager.DefaultConnectionLimit = Math.Max(System.Net.ServicePointManager.DefaultConnectionLimit, 16);
#endif
        var runner = new AgentRunner(paths, new WindowsHostInfo(paths, () => vss.ProviderState), vss, log, restoreFiles: new WindowsRestoreFileSystem(), sql: new WindowsSqlEnvironment());
        var code = runner.RunAsync(stop).GetAwaiter().GetResult();
        log.Write(code == AgentProtocol.RestartExitCode ? "Restarting for the update." : "Stopped.");
        return code;
    }
}

internal sealed class AgentWindowsService : ServiceBase
{
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private Thread? _thread;

    public AgentWindowsService()
    {
        ServiceName = AgentProtocol.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _thread = new Thread(() =>
        {
            try
            {
                AgentInstaller.EnsureRecovery();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                EventLog.WriteEntry("The service recovery could not be set: " + ex.Message, System.Diagnostics.EventLogEntryType.Warning);
            }

            var code = AgentHost.Run(echo: false, _stop.Token);
            if (code == AgentProtocol.RestartExitCode)
            {
                // A non-zero exit code makes the service recovery restart the agent with the new files.
                ExitCode = code;
                Stop();
            }
        })
        { IsBackground = true, Name = "AgentLoop" };
        _thread.Start();
    }

    protected override void OnStop()
    {
        _stop.Cancel();
        if (_thread is not null && _thread != Thread.CurrentThread)
        {
            _thread.Join(TimeSpan.FromSeconds(20));
        }
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
