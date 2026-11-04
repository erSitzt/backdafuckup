using System.Diagnostics;
using System.Globalization;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Sql;

namespace Backdafuckup.Server.Agents;

/// <summary>The SQL Server snapshot instead of the VSS freeze: the job channel, the account and the backup's name in msdb.</summary>
public sealed record SqlSnapshotSettings(Sql.SqlJobs Jobs, SqlAccount Account, string BackupName);

/// <summary>
/// Guest freeze through the backdafuckup agent for one VM backup: prepare (VSS writer metadata), freeze (our VSS
/// provider holds the writes), thaw (writer results). The hypervisor provider calls it right around the snapshot.
/// With <see cref="SqlSnapshotSettings"/> the freeze is SQL Server's T-SQL snapshot backup instead: VSS only gathers
/// the metadata, the agent suspends the databases' writes (no time limit) and records the snapshot in msdb afterwards.
/// </summary>
public sealed class AgentQuiescer(AgentRegistry registry, AgentOptions options, Guid agentId, string agentName, SqlSnapshotSettings? sql = null) : IGuestQuiescer
{
    private readonly Guid _operation = Guid.NewGuid();
    private IDisposable? _reservation;

    private TimeSpan ThawTimeout => options.ThawTimeout;

    private TimeSpan SqlHoldLimit => options.SqlSnapshotHoldLimit;

    private string AgentName => agentName;

    /// <summary>What the guest reported while preparing: volumes, VSS writers, SQL databases (stored with the restore point).</summary>
    public GuestBackupInfo? Prepared { get; private set; }

    /// <summary>The writer states after the thaw.</summary>
    public ThawReport? Thawed { get; private set; }

    /// <summary>SQL Server snapshot: per instance the recorded, skipped and failed databases and the <c>.bkm</c>.</summary>
    public IReadOnlyList<SqlSnapshotInstance>? SqlSnapshot { get; private set; }

    /// <summary>SQL Server snapshot: how long the databases' writes were suspended.</summary>
    public long SqlSuspendedMs { get; private set; }

    /// <summary>True if the freeze is the SQL Server snapshot (<see cref="SqlSnapshotSettings"/>).</summary>
    public bool UsesSqlSnapshot => sql is not null;

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
        if (sql is not null)
        {
            return await SuspendSqlAsync(sql, log, cancellationToken).ConfigureAwait(false);
        }

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

    /// <summary>
    /// SQL Server snapshot: ends the VSS backup (it only gathered the metadata), then has the agent suspend the user
    /// databases. Fails (crash-consistent) if no database could be suspended at all.
    /// </summary>
    private async Task<IGuestFreeze> SuspendSqlAsync(SqlSnapshotSettings settings, IOperationLog log, CancellationToken cancellationToken)
    {
        RunningSqlJob? job = null;
        var handedOver = false;
        try
        {
            await SendAsync(AgentCommandKinds.Abort, TimeSpan.FromSeconds(30), "ending the VSS metadata backup", cancellationToken).ConfigureAwait(false);
            if (!registry.HasCapability(agentId, AgentProtocol.CapabilitySqlSnapshot))
            {
                throw new GuestQuiesceException($"the {agentName} cannot suspend SQL Server for a snapshot yet; it updates itself after a server update (or reinstall it).");
            }

            var watch = Stopwatch.StartNew();
            job = await settings.Jobs.StartAsync(
                agentId,
                new SqlJobRequest
                {
                    Kind = SqlJobKinds.SnapshotSuspend,
                    Account = settings.Account,
                    HoldLimitMs = (int)options.SqlSnapshotHoldLimit.TotalMilliseconds,
                    BackupName = settings.BackupName,
                },
                cancellationToken).ConfigureAwait(false);
            var (progress, failed) = await job.WaitForProgressAsync(options.PrepareTimeout, cancellationToken).ConfigureAwait(false);
            if (progress is null)
            {
                throw new GuestQuiesceException($"suspending SQL Server failed in the {agentName}: {failed?.Error ?? "no details"}");
            }

            var suspended = progress.Instances.Sum(i => i.Databases.Count(d => d.State == SqlSnapshotStates.Suspended));
            if (suspended == 0)
            {
                job.SetSignal(SqlJobSignals.Abort);
                throw new GuestQuiesceException($"the {agentName} could not suspend any SQL Server database: {DescribeProblems(progress.Instances)}");
            }

            log.Info($"Guest processing: the {agentName} suspended the writes of {suspended} SQL Server database(s) in {Seconds1(watch.Elapsed)} " +
                $"(reads go on; no VSS freeze): {DescribeSuspended(progress.Instances)}.");
            if (DescribeProblems(progress.Instances) is { Length: > 0 } problems)
            {
                log.Warning($"Guest processing: not suspended: {problems}");
            }

            var freeze = new SqlFreeze(this, job, watch.Elapsed);
            job = null;
            handedOver = true;
            return freeze;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            throw new GuestQuiesceException($"suspending SQL Server through the {agentName} failed: {ex.Message}", ex);
        }
        finally
        {
            if (!handedOver)
            {
                // Tell a running job to resume (it also does after its hold limit or when the job is gone), and free the agent.
                job?.SetSignal(SqlJobSignals.Abort);
                job?.Dispose();
                Release();
            }
        }
    }

    internal static string DescribeSuspended(IEnumerable<SqlSnapshotInstance> instances) => string.Join("; ", instances
        .Where(i => i.Databases.Any(d => d.State is SqlSnapshotStates.Suspended or SqlSnapshotStates.Recorded))
        .Select(i => $"{i.Name} ({i.ProductVersion}): {string.Join(", ", i.Databases.Where(d => d.State is SqlSnapshotStates.Suspended or SqlSnapshotStates.Recorded).Select(d => d.Name))}"));

    internal static string DescribeProblems(IEnumerable<SqlSnapshotInstance> instances) => string.Join("; ", instances
        .SelectMany(i => (i.Error is null ? [] : new[] { $"{i.Name}: {i.Error}" })
            .Concat(i.Databases.Where(d => d.State is SqlSnapshotStates.Failed or SqlSnapshotStates.Skipped).Select(d => $"{i.Name}\\{d.Name}: {d.Reason ?? d.State}"))));

    private static string Seconds1(TimeSpan time) => time.TotalSeconds.ToString("0.0 s", CultureInfo.InvariantCulture);

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

        public async Task<GuestFreezeResult> ThawAsync(bool snapshotTaken, CancellationToken cancellationToken)
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

    /// <summary>The databases are suspended by the agent's SQL job; the thaw tells it whether to record the snapshot.</summary>
    private sealed class SqlFreeze(AgentQuiescer owner, RunningSqlJob job, TimeSpan freezeTime) : IGuestFreeze
    {
        private bool _done;

        public async Task<GuestFreezeResult> ThawAsync(bool snapshotTaken, CancellationToken cancellationToken)
        {
            _done = true;
            SqlJobResult result;
            try
            {
                job.SetSignal(snapshotTaken ? SqlJobSignals.SnapshotTaken : SqlJobSignals.Abort);
                result = await job.WaitForResultAsync(owner.ThawTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return GuestFreezeResult.NotConsistent($"the {owner.AgentName} did not report the SQL Server snapshot within {Seconds(owner.ThawTimeout)}; it resumes SQL Server after {Seconds(owner.SqlHoldLimit)} at the latest.")
                    with { Sql = new SqlSnapshotOutcome(false, "no result from the agent") };
            }
            finally
            {
                job.Dispose();
                owner.Release();
            }

            var instances = result.Snapshot ?? [];
            owner.SqlSnapshot = instances;
            owner.SqlSuspendedMs = result.SuspendedMs;
            var held = TimeSpan.FromMilliseconds(result.SuspendedMs);
            if (!result.Success)
            {
                return GuestFreezeResult.NotConsistent($"the SQL Server snapshot failed in the {owner.AgentName}: {result.Error ?? "no details"}")
                    with { HeldTime = held, Sql = new SqlSnapshotOutcome(false, $"failed: {result.Error ?? "no details"}") };
            }

            var recorded = instances.Sum(i => i.Databases.Count(d => d.State == SqlSnapshotStates.Recorded));
            var problems = DescribeProblems(instances);
            var all = recorded > 0 && problems.Length == 0;
            var summary = recorded == 0
                ? $"no database was recorded{(problems.Length > 0 ? $" ({problems})" : string.Empty)}; writes were suspended for {Seconds1(held)}."
                : $"{recorded} database(s) recorded in msdb as copy-only snapshot backups (application-consistent): {DescribeSuspended(instances)}. " +
                  $"Writes were suspended for {Seconds1(held)}; files outside SQL Server are crash-consistent." +
                  (problems.Length > 0 ? $" Not consistent: {problems}." : string.Empty);
            return new GuestFreezeResult(false, all ? null : $"SQL Server snapshot: {problems}", freezeTime, held, [])
            {
                Sql = new SqlSnapshotOutcome(all, summary),
            };
        }

        public ValueTask DisposeAsync()
        {
            if (!_done)
            {
                _done = true;
                job.SetSignal(SqlJobSignals.Abort);
                job.Dispose();
            }

            owner.Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>VSS_E_HOLD_WRITES_TIMEOUT.</summary>
    internal const int VssHoldWritesTimeout = unchecked((int)0x80042314);
}
