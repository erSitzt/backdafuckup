namespace Backdafuckup.Server.Sql;

/// <summary>Configuration section "SqlRestore".</summary>
public sealed class SqlRestoreOptions
{
    /// <summary>
    /// What a publish may write into its overlay in the guest (SQL Server's recovery and the admin's changes); the agent
    /// lowers it so that 1 GiB stays free on the volume of the overlay.
    /// </summary>
    public long MaxOverlayBytes { get; set; } = 20L << 30;

    /// <summary>How long a publish may take until the database is attached (recovery reads from the backup).</summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Publishes at the same time (each holds a restore point and an agent).</summary>
    public int MaxConcurrentPublishes { get; set; } = 8;

    /// <summary>Database restores at the same time (one per agent; more wait for their agent).</summary>
    public int MaxConcurrentRestores { get; set; } = 4;

    /// <summary>How long the attach of a restored database may take (SQL Server's recovery runs in it).</summary>
    public TimeSpan AttachTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>How long a publish session waits for the agent to come back to remove a database the publish left.</summary>
    public TimeSpan LeftoverWaitLimit { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// After a start, how long the clean-up of restores a restart interrupted waits before it looks (the agents' own jobs
    /// from before end first), and for how long after the restart it keeps trying.
    /// </summary>
    public TimeSpan CleanupStartDelay { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan CleanupGiveUpAfter { get; set; } = TimeSpan.FromDays(1);

    public int MinAutoUnpublishMinutes { get; set; } = 15;

    public int MaxAutoUnpublishMinutes { get; set; } = 7 * 24 * 60;
}
