namespace Backdafuckup.Core.Hypervisor;

/// <summary>A guest account for <see cref="IGuestOperations"/>: <c>DOMAIN\user</c>, <c>user@domain</c> or a local user name.</summary>
public sealed record GuestLogin(string UserName, string Password)
{
    public override string ToString() => UserName;
}

/// <summary>
/// Copies files into a guest and runs programs there through the platform's guest tools, without a network path to the
/// guest (vSphere: VMware Tools guest operations). Optional on an <see cref="IHypervisorSession"/>.
/// </summary>
public interface IGuestOperations
{
    /// <summary>Null when guest operations work for the VM now; otherwise why not (VM off, tools not running, …).</summary>
    Task<string?> CheckGuestOperationsAsync(string vmId, CancellationToken cancellationToken);

    /// <summary>Creates the directory and its parents; an existing directory is fine.</summary>
    Task CreateDirectoryAsync(string vmId, GuestLogin login, string path, CancellationToken cancellationToken);

    /// <summary>Writes the file in the guest, replacing an existing one.</summary>
    Task UploadFileAsync(string vmId, GuestLogin login, string path, Stream content, long length, CancellationToken cancellationToken);

    /// <summary>Starts a program in the guest and returns its process id.</summary>
    Task<long> StartProgramAsync(string vmId, GuestLogin login, string programPath, string arguments, CancellationToken cancellationToken);

    /// <summary>The exit code of a program started with <see cref="StartProgramAsync"/>, or null while it runs.</summary>
    Task<int?> GetExitCodeAsync(string vmId, GuestLogin login, long processId, CancellationToken cancellationToken);
}
