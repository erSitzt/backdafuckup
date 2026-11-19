namespace Backdafuckup.Server.InstantRecovery;

/// <summary>Who answers the portmapper on port 111, through which hypervisors find the MOUNT and NFS ports.</summary>
public enum PortmapperMode
{
    /// <summary>Our own portmapper; on Linux, when port 111 is taken (or may not be bound) and rpcbind runs, register with rpcbind.</summary>
    Automatic,

    /// <summary>Our own portmapper on <see cref="InstantRecoveryOptions.PortmapperPort"/>.</summary>
    Own,

    /// <summary>Register with this machine's rpcbind through its Unix socket (Linux).</summary>
    System,
}

/// <summary>Configuration section "InstantRecovery": the NFS server that instant VM recovery mounts as a datastore.</summary>
public sealed class InstantRecoveryOptions
{
    /// <summary>Off by default: the server opens the NFS ports only when this is on.</summary>
    public bool Enabled { get; set; }

    /// <summary>Instant recoveries running at the same time (each is one datastore).</summary>
    public int MaxConcurrentSessions { get; set; } = 4;

    /// <summary>VMs in one instant recovery.</summary>
    public int MaxVmsPerSession { get; set; } = 8;

    /// <summary>A running instant recovery warns once it is older than this (it is meant to be migrated or stopped soon).</summary>
    public TimeSpan AgeWarning { get; set; } = TimeSpan.FromHours(24);

    /// <summary>The address the NFS server listens on; 0.0.0.0 for all IPv4 addresses of this server.</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// The address of this server that hosts mount the datastore from. Empty: <see cref="ListenAddress"/> if it is one
    /// address, else the address that routes to the host's VMkernel NICs.
    /// </summary>
    public string? AdvertiseAddress { get; set; }

    /// <summary>How often a running instant recovery looks at its VMs (migrated or removed by hand, age).</summary>
    public TimeSpan WatchInterval { get; set; } = TimeSpan.FromSeconds(10);

    public PortmapperMode PortmapperMode { get; set; } = PortmapperMode.Automatic;

    public int PortmapperPort { get; set; } = 111;

    public int NfsPort { get; set; } = 2049;

    /// <summary>The MOUNT protocol's port (the same as <see cref="NfsPort"/> is allowed).</summary>
    public int MountPort { get; set; } = 20048;

    /// <summary>Portmapper and MOUNT also over UDP (some clients ask the portmapper over UDP first); NFS itself is TCP only.</summary>
    public bool Udp { get; set; } = true;

    /// <summary>rpcbind's Unix socket for <see cref="PortmapperMode.System"/>; empty finds it (/run/rpcbind.sock).</summary>
    public string? RpcbindSocket { get; set; }

    /// <summary>Where the overlays (everything the hypervisor writes) and namespaces live; default &lt;data directory&gt;/instant-recovery.</summary>
    public string? OverlayDirectory { get; set; }

    /// <summary>Space always left free on the overlay directory's volume; writes beyond it fail with "no space" in the VM's datastore.</summary>
    public long FreeSpaceReserveBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>Decompressed chunks of the recovered disks kept in memory, shared by all running instant recoveries.</summary>
    public long CacheBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Chunks loaded ahead when a disk is read sequentially (booting, Storage vMotion).</summary>
    public int ReadAheadChunks { get; set; } = 8;

    /// <summary>Chunks read from the repositories at the same time.</summary>
    public int MaxConcurrentChunkLoads { get; set; } = 16;

    /// <summary>The largest NFS READ and WRITE offered to clients.</summary>
    public int MaxTransferBytes { get; set; } = 1024 * 1024;

    /// <summary>Calls of one connection processed at the same time.</summary>
    public int MaxInFlightPerConnection { get; set; } = 128;

    /// <summary>How long replies of non-idempotent calls are kept, so a retransmission gets the same answer.</summary>
    public TimeSpan ReplyCacheLifetime { get; set; } = TimeSpan.FromMinutes(2);
}
