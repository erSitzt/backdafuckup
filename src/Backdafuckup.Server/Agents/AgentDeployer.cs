using System.Net.Sockets;
using System.Text.Json;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Smb;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Agents;

/// <summary>
/// Push installation of the agent over SMB 445: the files go to a fixed folder (C$\Program Files\backdafuckup Agent,
/// otherwise ADMIN$\backdafuckup Agent), then the service BackdafuckupAgent is created (Local System, automatic) and
/// started. The .NET 10 build is tried first; if it does not keep running, the .NET Framework 4.8 build replaces it.
/// The agent itself protects its folder, registers the VSS provider and enrolls with the server.
/// </summary>
public sealed class AgentDeployer(
    IDbContextFactory<AppDbContext> dbFactory,
    CredentialProtector protector,
    ConnectionManager connections,
    AgentPackages packages,
    AgentSettingsStore settings,
    IRemoteWindowsConnector connector,
    Microsoft.Extensions.Options.IOptions<AgentOptions> options) : IAgentInstaller
{
    private TimeSpan StableTime => options.Value.ServiceStableTime;

    private TimeSpan PollInterval => options.Value.ServicePollInterval;

    public async Task InstallAsync(Guid connectionId, string vmNativeId, string vmName, Guid credentialId, IOperationLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        string user;
        string password;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var credential = await db.Credentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == credentialId && c.Kind == CredentialKind.Windows, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Windows credentials of the job no longer exist.");
            user = credential.Username;
            password = protector.Unprotect(credential.ProtectedSecret);
        }

        IReadOnlyList<string> addresses;
        await using (var hypervisor = await connections.OpenAsync(connectionId, cancellationToken).ConfigureAwait(false))
        {
            addresses = await hypervisor.GetGuestAddressesAsync(vmNativeId, cancellationToken).ConfigureAwait(false);
        }

        if (addresses.Count == 0)
        {
            throw new InvalidOperationException($"VM {vmName} reports no IP address to XCP-ng: are the guest tools installed and the VM running?");
        }

        var builds = new[] { AgentProtocol.BuildNet10, AgentProtocol.BuildNet48 }.Select(packages.Get).OfType<AgentBuild>().ToList();
        if (builds.Count == 0)
        {
            throw new InvalidOperationException($"The backup server has no agent build in {packages.Root} (build it with scripts/build-agent.sh, or set Agent:PackageDirectory).");
        }

        var config = JsonSerializer.SerializeToUtf8Bytes(await settings.CreateAgentConfigAsync(cancellationToken).ConfigureAwait(false), AgentJson.Options);
        await Task.Run(() => Deploy(addresses, user, password, vmName, builds, config, log, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private void Deploy(IReadOnlyList<string> addresses, string user, string password, string vmName, List<AgentBuild> builds, byte[] config, IOperationLog log, CancellationToken cancellationToken)
    {
        using var remote = Connect(addresses, user, password, log);
        log.Info($"Agent installation in VM {vmName}: connected to {remote.Target}:445 as {user}.");
        var (share, folder, local) = remote.HasShare("C$")
            ? ("C$", @"Program Files\backdafuckup Agent", @"C:\Program Files\backdafuckup Agent")
            : ("ADMIN$", "backdafuckup Agent", @"%SystemRoot%\backdafuckup Agent");
        using var services = remote.Services();
        if (services.Query(AgentProtocol.ServiceName) is { } existing)
        {
            log.Info($"Replacing the existing agent service on {remote.Target} ({existing.StateName}).");
            Remove(services, cancellationToken);
        }

        remote.CreateDirectory(share, folder);
        for (var i = 0; i < builds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var build = builds[i];
            foreach (var file in build.Files)
            {
                using var stream = File.OpenRead(Path.Combine(build.Directory, file.Name));
                remote.WriteFile(share, $@"{folder}\{file.Name}", stream);
            }

            // Last: the enrollment secret only lands next to complete program files.
            remote.WriteFile(share, $@"{folder}\{AgentProtocol.ConfigFile}", new MemoryStream(config));
            log.Info($"Copied the agent ({build.Name}, {build.Version}, {build.Files.Count} file(s)) to \\\\{remote.Target}\\{share}\\{folder}.");

            services.Create(AgentProtocol.ServiceName, AgentProtocol.ServiceDisplayName, $"\"{local}\\{AgentProtocol.ExecutableName}\" run");
            var (running, status) = StartAndWatch(services, cancellationToken);
            if (running)
            {
                log.Info($"The agent service runs on {remote.Target} ({build.Name} build) in {local}; it now registers with the backup server.");
                return;
            }

            var next = i + 1 < builds.Count ? $" Trying the {builds[i + 1].Name} build." : string.Empty;
            log.Warning($"The {build.Name} build does not keep running on {remote.Target} ({status}).{next}");
            Remove(services, cancellationToken);
        }

        throw new InvalidOperationException(
            $"The agent does not start in VM {vmName} ({remote.Target}); see {local}\\{AgentProtocol.LogFile} and the Windows event log there.");
    }

    private IRemoteWindows Connect(IReadOnlyList<string> addresses, string user, string password, IOperationLog log)
    {
        var errors = new List<string>();
        foreach (var address in addresses)
        {
            try
            {
                return connector.Connect(address, user, password, TimeSpan.FromSeconds(20));
            }
            catch (UnauthorizedAccessException ex)
            {
                // A logon failure is the same on every address of the VM.
                throw new InvalidOperationException(ex.Message, ex);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                errors.Add(ex.Message);
                log.Info($"Agent installation: {ex.Message}");
            }
        }

        throw new InvalidOperationException($"None of the guest's addresses ({string.Join(", ", addresses)}) accepted an SMB connection on port 445: {string.Join(" ", errors)}");
    }

    /// <summary>Starts the service and watches it: true if it keeps running for <see cref="StableTime"/>.</summary>
    private (bool Running, string Status) StartAndWatch(IRemoteServices services, CancellationToken cancellationToken)
    {
        try
        {
            services.Start(AgentProtocol.ServiceName);
        }
        catch (ScmException ex)
        {
            return (false, ex.Message);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var runningSince = TimeSpan.MinValue;
        while (watch.Elapsed < StableTime + TimeSpan.FromSeconds(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = services.Query(AgentProtocol.ServiceName);
            if (status is null || status.CurrentState == ServiceStatus.Stopped)
            {
                return (false, status is null ? "the service disappeared" : $"stopped with exit code {status.Win32ExitCode}/{status.ServiceSpecificExitCode}");
            }

            if (status.CurrentState == ServiceStatus.Running)
            {
                if (runningSince == TimeSpan.MinValue)
                {
                    runningSince = watch.Elapsed;
                }
                else if (watch.Elapsed - runningSince >= StableTime)
                {
                    return (true, status.StateName);
                }
            }

            Thread.Sleep(PollInterval);
        }

        return (false, "did not reach a stable running state");
    }

    private void Remove(IRemoteServices services, CancellationToken cancellationToken)
    {
        if (services.Query(AgentProtocol.ServiceName) is { CurrentState: not ServiceStatus.Stopped })
        {
            services.Stop(AgentProtocol.ServiceName);
            WaitUntil(() => services.Query(AgentProtocol.ServiceName) is null or { CurrentState: ServiceStatus.Stopped }, cancellationToken);
        }

        if (services.Query(AgentProtocol.ServiceName) is not null)
        {
            services.Delete(AgentProtocol.ServiceName);
            WaitUntil(() => services.Query(AgentProtocol.ServiceName) is null, cancellationToken);
        }
    }

    private void WaitUntil(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 30 && !condition(); i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(PollInterval);
        }
    }
}

/// <summary>Installs the agent into the VMs of a deployment session (one task per VM) and waits for each to report.</summary>
public sealed class AgentDeploymentRunner(
    IAgentInstaller installer,
    AgentService agents,
    SessionTracker tracker,
    SessionEventWriter events,
    TimeProvider time)
{
    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<InstallAgentsDto>(session.Parameters ?? "null")
            ?? throw new InvalidOperationException("The deployment session has no parameters.");
        var failed = 0;
        var warnings = 0;
        foreach (var vm in request.Vms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var task = await tracker.AddTaskAsync(session.Id, vm.NativeId, vm.Name, cancellationToken).ConfigureAwait(false);
            var log = events.CreateLog(session.Id, task.Id, vm.Name);
            await tracker.UpdateTaskAsync(task.Id, t => { t.State = SessionState.Running; t.StartedUtc = time.GetUtcNow(); t.Attempt = 1; }, CancellationToken.None).ConfigureAwait(false);
            SessionState state;
            string? error = null;
            try
            {
                await installer.InstallAsync(request.ConnectionId, vm.NativeId, vm.Name, request.CredentialId, log, cancellationToken).ConfigureAwait(false);
                var agent = await agents.WaitForVmAsync(vm.NativeId, agents.Options.InstallWaitTimeout, cancellationToken).ConfigureAwait(false);
                if (agent is null)
                {
                    state = SessionState.Warning;
                    error = $"Installed, but the agent did not report within {agents.Options.InstallWaitTimeout.TotalMinutes:0} minutes (address under Guest agents → Settings, port 9555 from the guest).";
                    log.Warning(error);
                }
                else
                {
                    state = SessionState.Succeeded;
                    log.Info($"The agent on {agent.HostName} ({agent.RemoteAddress}) is online: {agent.OsName}, {agent.Build} build, VSS provider {agent.VssProvider}.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state = SessionState.Failed;
                error = ex.Message;
                log.Error($"Agent installation failed: {ex.Message}");
            }

            failed += state == SessionState.Failed ? 1 : 0;
            warnings += state == SessionState.Warning ? 1 : 0;

            await tracker.UpdateTaskAsync(task.Id, t => { t.State = state; t.Error = error; t.EndedUtc = time.GetUtcNow(); t.Progress = 100; }, CancellationToken.None).ConfigureAwait(false);
        }

        var summary = $"{request.Vms.Count - failed} of {request.Vms.Count} agent(s) installed" + (warnings > 0 ? $", {warnings} not reporting yet." : ".");
        var result = failed == request.Vms.Count ? SessionState.Failed : failed + warnings > 0 ? SessionState.Warning : SessionState.Succeeded;
        return (result, summary);
    }
}
