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

    /// <summary>How long a publish session waits for the agent to come back to remove a database the publish left.</summary>
    public TimeSpan LeftoverWaitLimit { get; set; } = TimeSpan.FromMinutes(20);

    public int MinAutoUnpublishMinutes { get; set; } = 15;

    public int MaxAutoUnpublishMinutes { get; set; } = 7 * 24 * 60;
}
