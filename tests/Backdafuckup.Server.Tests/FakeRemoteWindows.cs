using Backdafuckup.Smb;

namespace Backdafuckup.Server.Tests;

/// <summary>
/// A simulated Windows guest for the push installation: shares with files, and a service control manager whose
/// services run or stop depending on which agent build was copied.
/// </summary>
internal sealed class FakeRemoteWindowsConnector : IRemoteWindowsConnector
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, (string BinaryPath, uint State)> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Calls { get; } = [];

    /// <summary>The addresses that accept SMB connections.</summary>
    public HashSet<string> Reachable { get; } = [];

    public string Password { get; set; } = "secret";

    public bool HasCDollar { get; set; } = true;

    /// <summary>Decides whether the service keeps running with the copied files (e.g. only the net48 build runs).</summary>
    public Func<byte[], bool> Runs { get; set; } = _ => true;

    /// <summary>Called when a service reaches the running state.</summary>
    public Action? OnStarted { get; set; }

    public IRemoteWindows Connect(string address, string userName, string password, TimeSpan timeout)
    {
        lock (Calls)
        {
            Calls.Add($"connect {address} as {userName}");
        }

        if (!Reachable.Contains(address))
        {
            throw new IOException($"No SMB connection to {address}:445 (firewall, or the guest is not a Windows computer with file sharing).");
        }

        if (password != Password)
        {
            throw new UnauthorizedAccessException($"Logon as {userName} at {address}:445 failed: wrong user name or password.");
        }

        return new Remote(this, address);
    }

    private sealed class Remote(FakeRemoteWindowsConnector owner, string address) : IRemoteWindows, IRemoteServices
    {
        public string Target => address;

        public bool HasShare(string share) => share != "C$" || owner.HasCDollar;

        public void CreateDirectory(string share, string path) => owner.Calls.Add($"mkdir {share}\\{path}");

        public void WriteFile(string share, string path, Stream content)
        {
            using var memory = new MemoryStream();
            content.CopyTo(memory);
            owner.Files[$"{share}\\{path}"] = memory.ToArray();
            owner.Calls.Add($"write {share}\\{path}");
        }

        public IRemoteServices Services() => this;

        public ServiceStatus? Query(string name) =>
            owner.Services.TryGetValue(name, out var service) ? new ServiceStatus(0x10, service.State, 0, 0, 0, 0, 0) : null;

        public void Create(string name, string displayName, string binaryPath)
        {
            if (owner.Services.ContainsKey(name))
            {
                throw new ScmException($"Creating the service {name}", ScmException.ServiceExists);
            }

            owner.Services[name] = (binaryPath, ServiceStatus.Stopped);
            owner.Calls.Add($"create {name}: {binaryPath}");
        }

        public void Start(string name)
        {
            var exe = owner.Files.First(f => f.Key.EndsWith("backdafuckup-agent.exe", StringComparison.OrdinalIgnoreCase)).Value;
            var runs = owner.Runs(exe);
            owner.Services[name] = (owner.Services[name].BinaryPath, runs ? ServiceStatus.Running : ServiceStatus.Stopped);
            owner.Calls.Add($"start {name}: {(runs ? "running" : "stopped")}");
            if (runs)
            {
                owner.OnStarted?.Invoke();
            }
        }

        public void Stop(string name)
        {
            owner.Services[name] = (owner.Services[name].BinaryPath, ServiceStatus.Stopped);
            owner.Calls.Add($"stop {name}");
        }

        public void Delete(string name)
        {
            owner.Services.Remove(name);
            owner.Calls.Add($"delete {name}");
        }

        public void Dispose()
        {
        }
    }
}
