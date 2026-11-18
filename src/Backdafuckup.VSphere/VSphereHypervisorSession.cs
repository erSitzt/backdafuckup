using System.Net;
using System.Runtime.CompilerServices;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere;

/// <summary>An open connection to one vCenter.</summary>
internal sealed class VSphereHypervisorSession(VSphereProvider provider, ConnectionSettings settings, VimSession vim, PlatformInfo platform) : IHypervisorSession
{
    public PlatformInfo Platform { get; } = platform;

    internal VSphereProvider Provider { get; } = provider;

    internal ConnectionSettings Settings { get; } = settings;

    internal VimSession Vim { get; } = vim;

    public Task<Inventory> GetInventoryAsync(CancellationToken cancellationToken) => VSphereInventory.GetAsync(Vim, Platform, cancellationToken);

    public Task<StorageState> GetStorageStateAsync(CancellationToken cancellationToken) => VSphereInventory.GetStorageStateAsync(Vim, cancellationToken);

    public async Task<IReadOnlyList<string>> GetGuestAddressesAsync(string vmId, CancellationToken cancellationToken)
    {
        var vm = await VSphereInventory.FindVmAsync(Vim, vmId, cancellationToken).ConfigureAwait(false);
        if (vm is null)
        {
            return [];
        }

        var row = (await Vim.RetrievePropertiesAsync([vm.Ref], ["guest.net", "guest.ipAddress"], cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        var addresses = new List<string>();
        foreach (var nic in row?["guest.net"]?.Items ?? [])
        {
            addresses.AddRange(nic.Children("ipAddress").Select(a => a.Value));
        }

        if (row?.Str("guest.ipAddress") is { Length: > 0 } primary)
        {
            addresses.Insert(0, primary);
        }

        return [.. addresses
            .Where(a => IPAddress.TryParse(a, out var ip) && !ip.IsIPv6LinkLocal && !IPAddress.IsLoopback(ip))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a.Contains(':', StringComparison.Ordinal))];
    }

    public Task<IVmBackupSession> BeginBackupAsync(VmBackupRequest request, IOperationLog log, IArtifactLedger ledger, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"Backups of vSphere VMs are not available yet in this version ({Platform.MasterAddress}).");

    public Task<IVmRestoreSession> BeginRestoreAsync(VmRestorePlan plan, IOperationLog log, IArtifactLedger ledger, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"Restores to vSphere are not available yet in this version ({Platform.MasterAddress}).");

    public Task CleanupArtifactAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CommitArtifactAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>vSphere keeps no per-backup change-tracking objects: a changeId is just a position in the disk's log.</summary>
    public Task ReleaseChangeTokensAsync(IEnumerable<ChangeToken> tokens, IOperationLog log, CancellationToken cancellationToken) => Task.CompletedTask;

    public async IAsyncEnumerable<ProviderArtifact> FindTaggedArtifactsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public ValueTask DisposeAsync() => Vim.DisposeAsync();
}
