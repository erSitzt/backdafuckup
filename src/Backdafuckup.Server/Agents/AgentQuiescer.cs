using System.Diagnostics;
using System.Globalization;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Sql;

namespace Backdafuckup.Server.Agents;

/// <summary>The SQL Server snapshot instead of the VSS freeze: the job channel, the account and the backup's name in msdb.</summary>
/// <param name="CopyOnly">Copy-only backups (outside the backup chain) instead of regular full backups.</param>
public sealed record SqlSnapshotSettings(Sql.SqlJobs Jobs, SqlAccount Account, string BackupName, bool CopyOnly = false);

/// <summary>
/// Guest freeze through the backdafuckup agent for one VM backup: prepare (VSS writer metadata), freeze (our VSS
/// provider holds the writes), thaw (writer results). The hypervisor provider calls it right around the snapshot.
/// With <see cref="SqlSnapshotSettings"/> the freeze is SQL Server's T-SQL snapshot backup instead: VSS only gathers
/// the metadata, the agent suspends the databases' writes (no time limit) and records the snapshot in msdb afterwards.
/// If no database can be suspended (no SQL Server 2022 or later in the VM, an agent without the capability, no rights),
/// the VM falls back to the VSS freeze.
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
            if (!registry.HasCapability(agentId, AgentProtocol.CapabilitySqlSnapshot))
            {
                // VSS is still prepared: freeze with it right away.
                log.Info($"Guest processing: the {agentName} cannot suspend SQL Server for a snapshot yet (it updates itself after a server update); VSS is used for this VM.");
            }
            else if (await SuspendSqlAsync(sql, log, cancellationToken).ConfigureAwait(false) is { } sqlFreeze)
            {
                return sqlFreeze;
            }
            else
            {
                // The SQL path ended the VSS backup: prepare it again for the VSS freeze.
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
            }
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
    /// databases. Returns null if no database could be suspended (the reason is logged): the caller falls back to VSS.
    /// A failing command throws (crash-consistent) as for VSS.
    /// </summary>
    private async Task<IGuestFreeze?> SuspendSqlAsync(SqlSnapshotSettings settings, IOperationLog log, CancellationToken cancellationToken)
    {
        RunningSqlJob? job = null;
        var handedOver = false;
        var failedHard = false;
        try
        {
            await SendAsync(AgentCommandKinds.Abort, TimeSpan.FromSeconds(30), "ending the VSS metadata backup", cancellationToken).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            job = await settings.Jobs.StartAsync(
                agentId,
                new SqlJobRequest
                {
                    Kind = SqlJobKinds.SnapshotSuspend,
                    Account = settings.Account,
                    HoldLimitMs = (int)options.SqlSnapshotHoldLimit.TotalMilliseconds,
                    LockWaitMs = (int)options.SqlSnapshotLockWait.TotalMilliseconds,
                    BackupName = settings.BackupName,
                    CopyOnly = settings.CopyOnly,
                },
                cancellationToken).ConfigureAwait(false);
            var (progress, failed) = await job.WaitForProgressAsync(options.PrepareTimeout + options.SqlSnapshotLockWait, cancellationToken).ConfigureAwait(false);
            if (progress is null)
            {
                log.Warning($"Guest processing: suspending SQL Server failed in the {agentName}: {failed?.Error ?? "no details"}. VSS is used for this VM.");
                return null;
            }

            var suspended = progress.Instances.Sum(i => i.Databases.Count(d => d.State == SqlSnapshotStates.Suspended));
            if (suspended == 0)
            {
                // Expected for VMs without SQL Server 2022 or later; a misconfiguration (rights, connection) if they have one.
                var text = progress.Instances.Count == 0
                    ? $"Guest processing: the {agentName} found no SQL Server instance to suspend; VSS is used for this VM."
                    : $"Guest processing: the {agentName} could not suspend any SQL Server database ({DescribeProblems(progress.Instances)}); VSS is used for this VM.";
                if (progress.Instances.Any(i => MajorVersion(i.ProductVersion) >= 16))
                {
                    log.Warning(text);
                }
                else
                {
                    log.Info(text);
                }

                return null;
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
        catch (Exception ex)
        {
            failedHard = true;
            if (ex is TimeoutException or InvalidOperationException)
            {
                throw new GuestQuiesceException($"suspending SQL Server through the {agentName} failed: {ex.Message}", ex);
            }

            throw;
        }
        finally
        {
            if (!handedOver)
            {
                // Tell a running job to resume (it also does after its hold limit or when the job is gone). The agent stays
                // reserved for the VSS fallback, unless this failed.
                job?.SetSignal(SqlJobSignals.Abort);
                job?.Dispose();
                if (failedHard)
                {
                    Release();
                }
            }
        }
    }

    private static int MajorVersion(string? version) =>
        int.TryParse(version?.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ? major : 0;

    internal static string DescribeSuspended(IEnumerable<SqlSnapshotInstance> instances) => string.Join("; ", instances
        .Where(i => i.Databases.Any(d => d.State is SqlSnapshotStates.Suspended or SqlSnapshotStates.Recorded))
        .Select(i => $"{i.Name} ({i.ProductVersion}): {string.Join(", ", i.Databases.Where(d => d.State is SqlSnapshotStates.Suspended or SqlSnapshotStates.Recorded).Select(d => d.Name))}"));

    internal static string DescribeProblems(IEnumerable<SqlSnapshotInstance> instances) => string.Join("; ", instances
        .SelectMany(i => (i.Error is null ? [] : new[] { $"{i.Name}: {i.Error}" })
            .Concat(i.Databases.Where(d => d.State is SqlSnapshotStates.Failed or SqlSnapshotStates.Skipped).Select(d => $"{i.Name}\\{d.Name}: {d.Reason ?? d.State}"))));

    private static string Seconds1(TimeSpan time) => time.TotalSeconds.ToString("0.0 s", CultureInfo.InvariantCulture);

    /// <summary>
    /// Warnings about the transaction logs of databases in FULL or BULK_LOGGED recovery, where only log backups free the
    /// log: a regular full backup that started a log chain, and log chains without a log backup for longer than
    /// <paramref name="maxLogBackupAge"/> (taken by any tool). Copy-only backups start no chain; without one SQL Server
    /// frees the log at checkpoints, so those databases need no warning.
    /// </summary>
    internal static List<string> LogChainWarnings(IEnumerable<SqlSnapshotInstance> instances, TimeSpan maxLogBackupAge)
    {
        var started = new List<string>();
        var stale = new List<string>();
        foreach (var instance in instances)
        {
            foreach (var database in instance.Databases.Where(d => d.RecoveryModel is "FULL" or "BULK_LOGGED"))
            {
                var name = $"{instance.Name}\\{database.Name}";
                if (database.StartedLogChain)
                {
                    started.Add(name);
                }
                else if (database.HadLogChain && (database.LogBackupAgeMinutes is not { } age || age > maxLogBackupAge.TotalMinutes))
                {
                    stale.Add(database.LogBackupAgeMinutes is { } minutes ? $"{name} (last one {Age(TimeSpan.FromMinutes(minutes))} ago)" : $"{name} (none yet)");
                }
            }
        }

        var warnings = new List<string>();
        if (started.Count > 0)
        {
            var one = started.Count == 1;
            warnings.Add($"This full backup started the log chain of {string.Join(", ", started)} (FULL recovery, no full backup before). From now on SQL Server frees " +
                $"{(one ? "its transaction log" : "their transaction logs")} only after log backups; without them {(one ? "it grows" : "they grow")}. " +
                "Take log backups, set the recovery model to SIMPLE, or choose copy-only backups in the job.");
        }

        if (stale.Count > 0)
        {
            warnings.Add($"No log backup for more than {Age(maxLogBackupAge)}: {string.Join(", ", stale)}. In FULL recovery SQL Server frees the transaction log " +
                "only after log backups, so it grows until they run.");
        }

        return warnings;
    }

    private static string Age(TimeSpan age) => age.TotalHours < 48
        ? $"{Math.Max(1, (int)Math.Round(age.TotalHours)).ToString(CultureInfo.InvariantCulture)} hours"
        : $"{((int)age.TotalDays).ToString(CultureInfo.InvariantCulture)} days";

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
            catch (Sql.SqlJobLostException lost)
            {
                // Its connections closed with it, so SQL Server resumed the writes by itself.
                return GuestFreezeResult.NotConsistent($"{lost.Message} (the {owner.AgentName}); SQL Server resumed the writes when the agent's connections closed, " +
                    "and the snapshot backups were not reported.") with { Sql = new SqlSnapshotOutcome(false, "the agent restarted") };
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

            var recorded = instances.SelectMany(i => i.Databases).Where(d => d.State == SqlSnapshotStates.Recorded).ToList();
            var problems = DescribeProblems(instances);
            var all = recorded.Count > 0 && problems.Length == 0;
            // An agent before the setting reports no type and records copy-only.
            var kind = recorded.All(d => d.CopyOnly == false) ? "full snapshot backups (in the backup chain)"
                : recorded.All(d => d.CopyOnly != false) ? "copy-only snapshot backups"
                : "snapshot backups (" + string.Join("; ", recorded.Select(d => $"{d.Name}: {(d.CopyOnly == false ? "full" : "copy-only")}")) + ")";
            var summary = recorded.Count == 0
                ? $"no database was recorded{(problems.Length > 0 ? $" ({problems})" : string.Empty)}; writes were suspended for {Seconds1(held)}."
                : $"{recorded.Count} database(s) recorded in msdb as {kind} (application-consistent): {DescribeSuspended(instances)}. " +
                  $"Writes were suspended for {Seconds1(held)}; files outside SQL Server are crash-consistent." +
                  (problems.Length > 0 ? $" Not consistent: {problems}." : string.Empty);
            return new GuestFreezeResult(false, all ? null : $"SQL Server snapshot: {problems}", freezeTime, held, [])
            {
                Sql = new SqlSnapshotOutcome(all, summary, LogChainWarnings(instances, TimeSpan.FromDays(1))),
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
