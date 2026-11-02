using System.Diagnostics;
using System.Globalization;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;

namespace Backdafuckup.Server.Agents;

/// <summary>
/// Guest freeze through the backdafuckup agent for one VM backup: prepare (VSS writer metadata), freeze (our VSS
/// provider holds the writes), thaw (writer results). The hypervisor provider calls it right around the snapshot.
/// </summary>
public sealed class AgentQuiescer(AgentRegistry registry, AgentOptions options, Guid agentId, string agentName) : IGuestQuiescer
{
    private readonly Guid _operation = Guid.NewGuid();
    private IDisposable? _reservation;

    private TimeSpan ThawTimeout => options.ThawTimeout;

    /// <summary>What the guest reported while preparing: volumes, VSS writers, SQL databases (stored with the restore point).</summary>
    public GuestBackupInfo? Prepared { get; private set; }

    /// <summary>The writer states after the thaw.</summary>
    public ThawReport? Thawed { get; private set; }

    public async Task<IGuestFreeze> FreezeAsync(IOperationLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        _reservation = registry.Reserve(agentId);
        AgentCommandResult prepare;
        try
        {
            prepare = await SendAsync(AgentCommandKinds.PrepareBackup, options.PrepareTimeout, "preparing VSS", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release();
            throw;
        }

        Prepared = prepare.Prepare ?? new GuestBackupInfo();
        log.Info($"Guest processing: VSS prepared by the {agentName}. {Describe(Prepared)}");

        var watch = Stopwatch.StartNew();
        try
        {
            await SendAsync(AgentCommandKinds.Freeze, options.FreezeTimeout, "freezing the guest", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await AbortAsync().ConfigureAwait(false);
            Release();
            throw;
        }

        return new Freeze(this, watch.Elapsed);
    }

    private async Task<AgentCommandResult> SendAsync(string kind, TimeSpan timeout, string what, CancellationToken cancellationToken)
    {
        var command = new AgentCommand
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            OperationId = _operation,
            TimeoutMs = (int)options.ProviderHoldLimit.TotalMilliseconds,
            CopyOnly = true,
        };
        AgentCommandResult result;
        try
        {
            result = await registry.SendAsync(agentId, command, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new GuestQuiesceException($"{what} failed: the {agentName} did not answer within {Seconds(timeout)}.", ex);
        }

        if (!result.Success)
        {
            var code = result.ErrorCode != 0 ? $" (0x{result.ErrorCode:X8})" : string.Empty;
            throw new GuestQuiesceException($"{what} failed in the {agentName}: {result.Error ?? "no details"}{code}.");
        }

        return result;
    }

    private async Task AbortAsync()
    {
        try
        {
            await SendAsync(AgentCommandKinds.Abort, TimeSpan.FromSeconds(30), "aborting the guest backup", CancellationToken.None).ConfigureAwait(false);
        }
        catch (GuestQuiesceException)
        {
            // The agent gives up by itself after its hold limit.
        }
    }

    private void Release()
    {
        _reservation?.Dispose();
        _reservation = null;
    }

    private static string Describe(GuestBackupInfo info)
    {
        var writers = info.Writers.Count == 0 ? "no VSS writers" : $"{info.Writers.Count} VSS writer(s): {string.Join(", ", info.Writers.Select(w => w.Name).Distinct().Take(12))}";
        var sql = info.SqlDatabases.Count == 0 ? string.Empty
            : $"; {info.SqlDatabases.Count} SQL database(s) in {string.Join(", ", info.SqlDatabases.Select(d => d.Instance).Distinct())}";
        var volumes = info.Volumes.Count == 0 ? string.Empty : $"; volumes {string.Join(", ", info.Volumes.SelectMany(v => v.MountPoints).Take(12))}";
        return writers + sql + volumes + ".";
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0 s", CultureInfo.InvariantCulture);

    private sealed class Freeze(AgentQuiescer owner, TimeSpan freezeTime) : IGuestFreeze
    {
        private bool _done;

        public async Task<GuestFreezeResult> ThawAsync(CancellationToken cancellationToken)
        {
            _done = true;
            AgentCommandResult result;
            try
            {
                result = await owner.SendAsync(AgentCommandKinds.Thaw, owner.ThawTimeout, "thawing the guest", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                owner.Release();
            }

            var report = result.Thaw ?? new ThawReport();
            owner.Thawed = report;
            var writers = report.Writers.Select(w => new GuestWriterResult(w.Name, w.State, w.Failure)).ToList();
            var held = TimeSpan.FromMilliseconds(report.HeldMs);
            if (report.Consistent)
            {
                return new GuestFreezeResult(true, null, freezeTime, held, writers);
            }

            var failed = writers.Where(w => w.Failure is not null).Select(w => $"{w.Name}: {w.Failure}").ToList();
            var reason = report.VssResult == VssHoldWritesTimeout
                ? $"Windows released the writes after its limit of 10 s (VSS_E_HOLD_WRITES_TIMEOUT); writes were held for {held.TotalSeconds:0.0} s."
                : failed.Count > 0 ? $"VSS writer(s) failed: {string.Join("; ", failed)}."
                : $"VSS did not complete the snapshot set (0x{report.VssResult:X8}).";
            return new GuestFreezeResult(false, reason, freezeTime, held, writers);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_done)
            {
                _done = true;
                await owner.AbortAsync().ConfigureAwait(false);
            }

            owner.Release();
        }
    }

    /// <summary>VSS_E_HOLD_WRITES_TIMEOUT.</summary>
    internal const int VssHoldWritesTimeout = unchecked((int)0x80042314);
}
