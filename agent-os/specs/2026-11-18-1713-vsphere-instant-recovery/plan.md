# Plan: Instant VM recovery for vSphere (spec 2 of 3)

Builds on spec 1 (`../2026-11-18-1713-vsphere-provider/`): vim25 client, `VSphereVmSpecBuilder`, `vsphere-config-v1`.

## Context

**Goal.** Run a VM directly from a restore point within minutes:
- the backup server presents the restore point's disks to an ESXi host as an NFS datastore;
- the VM is created on that datastore and powered on;
- its writes go to an overlay.

**Afterwards**, the admin either:
- moves the VM to production storage with Storage vMotion and finalizes, or
- stops it, and everything is discarded.

**Sources.** Public protocols only: NFSv3 (RFC 1813), ONC RPC (RFC 5531), portmapper (RFC 1833), plus the vim25 operations `CreateNasDatastore`, `CreateVM_Task`, `RelocateVM_Task` and `RemoveDatastore`. Veeam's vPower NFS serves only as an idea from the public Help Center.

## Decisions

| Topic | Decision |
|---|---|
| Where the NFS server runs | **Inside the backup server process**, on Windows and Linux. See the reasons below. |
| Protocol | **NFSv3** over TCP. Our own portmapper v2 and MOUNT v3, on TCP and UDP. NFSv4.1 is not in v1: sessions, state and a grace period make restarts much harder. |
| Implementation | **Our own NFSv3 server in C#**, no new runtime dependencies. No mature .NET NFS server is known; nfs-ganesha, unfs3, nfs4j, go-nfs and nfsserve are behaviour references only. Fallback if spike S1 fails: FUSE plus kernel nfsd on the Linux proxy. |
| Datastore | **One per session.** A session holds one or more VMs, each in its own directory. Allowlist and fsid are per session; Stop and Migrate remove exactly one datastore; crash cleanup maps one-to-one onto the ledger. |
| How the VM is created | `CreateVM_Task` from spec 1's stored config, with the disks pointing at VMDK files already on our datastore. No .vmx is generated, so ESXi asks no "moved/copied" question. |
| Server restarts | **An instant recovery survives a server restart.** File handles, fsid and fileids stay the same, the overlay is on disk, and the session resumes. |

**Why the NFS server runs in the backup server:**
- Chunks are decompressed where they are stored. Random boot reads have no extra hop.
- The proxy cannot answer a cache miss without the server anyway, so it would add a second failure domain.
- In a disaster the proxy VM may be down or sit on the failed storage.
- The logic stays in the server.

The library `src/Backdafuckup.Nfs` stays independent of the server, so a proxy can host it later, for example for VMkernel networks the server cannot reach.

## Datastore contents and NFS behaviour

**Layout.** Export `/bdfu/<sid8>` is mounted as datastore `bdfu-ir-<sid8>`.
```
<vm>/disk0.vmdk        descriptor, generated at start, then a normal writable file (ESXi rewrites CID)
<vm>/disk0-flat.vmdk   base = restore point disk (ChunkIndex) + overlay
<vm>/<vm>.nvram        from the backup, if spec 1 stored it (otherwise ESXi creates it)
<vm>/<vm>.vmx, vmware*.log, *.vswp, .lck-*, vmx~ …   written by ESXi, overlay only
```

**Descriptor**
- `createType="vmfs"`, `RW <sectors> VMFS "disk0-flat.vmdk"`, `ddb.thinProvisioned="1"`.
- Adapter type and geometry come from spec 1's metadata.
- A new `ddb.uuid` by default, because the original VM may still be running. A "keep identity" option keeps the old one.

**Swap.** Request `swapPlacement=hostLocal` where the host has a swap datastore, so the `.vswp` stays off our overlay.

**File system model (`INfsFileSystem`)**

**Namespace and identity**
- **Namespace:**
  - kept in memory and persisted as an append-only, CRC'd `namespace.log` per session;
  - CREATE, MKDIR, REMOVE, RMDIR, RENAME and SETATTR (size/mode) are fsynced before the reply;
  - mtime is checkpointed every 30 s; after a crash it is restored as max(journal, data file), so it never goes backwards.
- **fileid:** a persisted 64-bit counter, stable and unique. ESXi names `.lck-*` files after it (verify in S1).
- **File handle (≤ 64 bytes):** `ver | exportId(8) | fileId(8) | generation(4) | HMAC-SHA256 truncated to 8`, keyed with a per-installation key under `DataDirectory/keys`. fattr3 fsid is derived from the session id. Handles survive restarts, so ESXi reconnects without ESTALE.

**Data and writes**
- **Base-backed files:**
  - `<fileId>.data` is a sparse file at the disk's own offsets;
  - `<fileId>.map` is a bitmap at 64 KiB granularity (a 2 TiB disk needs a 4 MiB bitmap);
  - first write to a block: take the stripe lock, read the base, write the merged block, set the bit;
  - reads: bit set reads the overlay, bit clear reads the base, zero chunks come from a static buffer;
  - positional I/O with `RandomAccess`; on Windows the file is marked sparse (`FSCTL_SET_SPARSE`);
  - the logic follows `CowDisk` in `src/Backdafuckup.Agent.Core/Publishing.cs`, but restart-safe: the map is persisted.
- **Write stability:**
  - UNSTABLE writes carry a write verifier drawn at random at each process start, so after a restart ESXi resends uncommitted data;
  - COMMIT fsyncs the data, then the dirty bitmap pages;
  - FILE_SYNC and DATA_SYNC writes are fsynced before the reply, with group commit of about 1 ms.
- **Locking:** no NLM or NSM. ESXi's NFSv3 locking uses `.lck` files. CREATE GUARDED/EXCLUSIVE are atomic under the namespace lock; the EXCLUSIVE verifier is stored as RFC 1813 requires.
- **Duplicate request cache:** keyed by (client IP, xid, proc) for the non-idempotent procedures, for about 2 minutes.

**Protocol surface**
- **Procedures:** NULL, GETATTR, SETATTR, LOOKUP, ACCESS, READ, WRITE, CREATE, MKDIR, REMOVE, RMDIR, RENAME, READDIR, READDIRPLUS (stable cookies), FSSTAT, FSINFO, PATHCONF, COMMIT.
  - LINK, SYMLINK, MKNOD and READLINK return NOTSUPP (S1 confirms ESXi does not need them).
  - FSINFO advertises rtmax/wtmax = 1 MiB and FSF3_HOMOGENEOUS|CANSETTIME.
  - FSSTAT reports the free space of the overlay volume minus a reserve.
  - fattr3 `used` = non-zero chunks plus overlay bytes, so the disk shows as thin.
- **Speed:**
  - System.IO.Pipelines per connection, with up to 128 pipelined requests;
  - a cache just for instant recovery (default 2 GiB), through a new async `ChunkCache.GetAsync` that loads each chunk once;
  - sequential detection with read-ahead of 8–32 chunks (pattern: `SequentialImageStream` in `RestorePointDiskImage.cs`);
  - nConnect (vSphere 8.0 U1 and later), checked in S1.
- **Security:**
  - AUTH_SYS as root, no root squash;
  - an allowlist per export of the host's VMkernel IPv4 addresses (`config.network.vnic`);
  - the listener binds to a configured `ListenAddress`, which is also the `remoteHost` given to `CreateNasDatastore`;
  - NFSv3 is cleartext, so the admin guide recommends a storage or management VLAN.
- **Diagnostics:** the server records which IPs connected during a mount attempt, so a failure says either "no connection from 10.26.0.21 reached 10.26.0.5:111/2049/20048" (firewall or route) or "connection from 10.26.0.22, not in the allowlist […]".

**Ports**
- **Windows:** port 111 is taken only when the "Server for NFS" role is installed. On a bind failure the error names the owning process or service.
- **Linux:** port 111 is often held by rpcbind. `PortmapperMode = Own | System`: in `System` mode MOUNT and NFS register with the local rpcbind.
- **Systemd:** `AmbientCapabilities=CAP_NET_BIND_SERVICE` in `deploy/linux/backdafuckup.service`.
- **Windows Firewall:** rules for TCP/UDP 111, TCP 2049 and the MOUNT port (default 20048) go into `deploy/windows/install-service.ps1`.

## Lifecycle

**New session kind.** `SessionKind.InstantRecovery` is long-lived, with its own capacity entry in `HasCapacity`. New tables:
- `InstantRecoveryEntity`: session, connection, host moref, datastore name and moref, export path, exportId, allowlist, overlay directory and limit, state, migrate task moref, `LastIoUtc`.
- `InstantRecoveryVmEntity`: restore point, VM moref and instanceUuid, name, map from disk to file.

**Start** (a ledger row before every vim25 call)
1. Pin each restore point (`RestorePointAccess`).
2. Check the host's NFS mount limit (`NFS.MaxVolumes` through the host's OptionManager) against its mounted NFS datastores.
3. Create the overlay directory and the namespace (descriptors, nvram).
4. Open the export with its allowlist.
5. `CreateNasDatastore(HostNasVolumeSpec{remoteHost, remotePath, localPath, accessMode=readWrite, type=NFS})` (artifact `vsphere.nas-datastore`).
6. `CreateVM_Task` (artifact `vsphere.ir-vm`):
   - new name;
   - NICs `startConnected=false` by default, or mapped to port groups;
   - generated MACs;
   - disk backings set to `[ds] vm/disk0.vmdk` with no `fileOperation`.
7. `PowerOnVM_Task`.

Encrypted disks are refused, naming the VM and disk. *Changed in task 6:* a vTPM is a warning, not a refusal (the instant recovery VM has none, because a vTPM needs VM encryption; Windows boots, BitLocker with a TPM protector asks for its recovery key). RDMs need no refusal: physical RDMs are not in the restore point (the VM runs without them, with a warning), and virtual RDMs that were backed up become flat disks, as in a normal restore.

**While it runs** (every 10 s, in the style of `SqlPublishSessionRunner.WatchAsync`)
- Track overlay bytes and limit, read and write rates, cache hits.
- Watch the VM's datastores through the PropertyCollector:
  - the VM left our datastore (migrated by hand): finalize;
  - the VM was deleted: stop.
- Warn when ESXi has done no I/O for 10 minutes.
- Warn by age (default 24 h) through the session log, a metric and a dashboard banner.
- Nothing is ever destroyed automatically. An optional "power off after N days" is off by default.

**Migrate to production** (needs vCenter; on a standalone ESXi only Stop is offered)
1. `RelocateVM_Task{datastore, host/pool, disk locators with thin backing}`, storing the task moref.
2. Wait and show progress.
3. Check that no files of the VM are left in our namespace.
4. `RemoveDatastore` on each host where it is mounted.
5. Close the export, delete the overlay, release the pins, mark the ledger rows Done.

**Stop**
1. `PowerOffVM_Task`, then `UnregisterVM`. **Only if every file of the VM is on our datastore** (swap files aside); otherwise refuse and alert. *Changed in task 6:* `UnregisterVM` instead of `Destroy_Task`: it deletes no file anywhere, and the VM's files are in our overlay, which the server deletes itself. It also works while the datastore is unreachable (cleanup after a crash).
2. `RemoveDatastore`, close the export, delete the overlay, release the pins.

**Server restart**
- `StartupRecovery` skips InstantRecovery sessions.
- A new `InstantRecoveryRuntime` runs before the scheduler and retention:
  - re-pins through the catalog row (after a scale-out move the copy is identical: same ids and index);
  - replays the journals;
  - starts the listeners on the same ports;
  - registers the session as running;
  - resumes waiting on a stored migrate task.
- `CleanupArtifactsAsync` leaves out the artifacts of instant recoveries that can be resumed.
- Guest I/O pauses for the length of the restart. VMware Tools typically sets guest disk timeouts to 180 s; the target is under 60 s.
- The deploy script names running instant recoveries and the pause a restart causes, and continues only with an explicit flag.

**Retention and scale-out**
- Pinned restore points are deferred.
- Because pins live only in memory, retention also skips restore points referenced by an active instant recovery in the database.

**Limits** (configurable)
- `MaxConcurrentSessions` (default 4) and `MaxVmsPerSession` (default 8);
- an overlay budget per session and in total;
- a free-space reserve on the overlay volume.

## Tasks (in order; each with its tests, the full suite green, and a commit)

**Spikes first,** written up in `spike.md`. Go/no-go: if S1 is not solved within about a week, switch to the FUSE fallback.

| Spike | What it checks |
|---|---|
| S1 | A throwaway NFSv3 server (raw image plus a writable directory) on vSphere 8: `CreateNasDatastore`, `CreateVM_Task` with existing disks, nvram placement, Linux and Windows boot. Log every RPC and capture with pktcap-uw: procedures used, write stability, rsize/wsize, nConnect, the `.lck` protocol, `.vswp`, READDIRPLUS. |
| S2 | Storage vMotion to VMFS under write load: the REMOVEs on the source, `RemoveDatastore` afterwards. |
| S3 | Kill and restart the server after 30, 90 and 180 s: does ESXi reconnect, do the guests survive, how do `.lck` files behave after a retransmitted CREATE. |
| S4 | Windows server: binding port 111, firewall, throughput (at least 300 MB/s sequential; Windows Server 2022 boots in under 3 minutes). |

**Build tasks**
1. **`src/Backdafuckup.Nfs`** (net10.0, no server dependency): XDR, RPC (TCP record marking, UDP, AUTH_SYS), portmapper, MOUNT, the NFS3 dispatcher over `INfsFileSystem`. Unit tests: golden XDR vectors, fragmented records, pipelined and out-of-order replies, malformed and oversized input (requests limited to 2 MiB).
2. **The instant-recovery file system:** namespace journal, handles with HMAC, overlay data and bitmap, `IBaseImage` over ChunkIndex/IChunkReader/async cache, duplicate request cache, write verifier. Tests: property tests of random concurrent reads and writes against a reference model; crash replay (the journal cut between the data and bitmap writes); zero chunks; NOSPC at the budget limit.
3. **Conformance** (`[Trait("Category","Integration")]`, pattern of `tests/Backdafuckup.Nbd.Tests/NbdkitInteropTests.cs`): a privileged container mounts with the kernel client (`vers=3,proto=tcp,mountport=`) and runs Connectathon (cthon04) basic/general, fsx and fio with verify; unprivileged libnfs utilities; a server dispose and recreate in the middle of fio must cause no ESTALE.
4. **Hosting in the server:** `NfsServerHost` (hosted service) with `InstantRecovery` options (ListenAddress, ports, PortmapperMode, OverlayDirectory default `<DataDirectory>/instant-recovery`, CacheBytes, limits). Port-conflict and connection diagnostics, the systemd capability, the firewall rules.
5. **Data model:** migration for the two tables, session kind, capacity, StartupRecovery skip and resume, artifact filter, retention check.
6. **vSphere operations** in spec 1's provider, behind `IInstantRecoveryPlatform`:
   - VMkernel IPs and the NFS mount limit;
   - `CreateNasDatastore`; `CreateVM_Task` with existing disks and the network map; `PowerOnVM_Task`; `RelocateVM_Task`;
   - a PropertyCollector watch;
   - `PowerOffVM_Task` and `UnregisterVM` with the guard; `RemoveDatastore` only for our export (type NFS, server and path match) with no VM left on it;
   - cleanup for the new artifact kinds.

   Tests against `FakeVcenter`.
7. **`InstantRecoverySessionRunner`:** start, watch, migrate, stop, finalize. Every message names hosts, IPs, ports, datastores and VMs. Tests with a fake platform and the in-memory NFS.
8. **API and console:**
   - an "Instant recovery" entry in `RestoreWizard.razor`;
   - a new `InstantRecovery.razor`:
     - a wizard: restore point(s), host or cluster, network (disconnected by default), name, overlay limit, summary;
     - running sessions: age, overlay used and limit, MB/s, cache hits, state;
     - Migrate (target host and datastore, thin/thick, progress) and Stop (typed confirmation).

   bUnit tests.
9. **Metrics** (`BackupMetrics.cs`, `StateMetrics.cs`):
   - `bdfu_instant_recovery_sessions{state}`, `_age_seconds`, `_overlay_bytes`, `_overlay_limit_bytes`;
   - `_read_bytes_total{source=overlay|cache|repository|zero}`, `_write_bytes_total`, `_migration_progress_ratio`;
   - `bdfu_nfs_requests_total{proc,status}`, `bdfu_nfs_request_seconds`, `bdfu_nfs_connections`.

   Alerts `BackdafuckupInstantRecoveryLongRunning` (more than 24 h), `…OverlayAlmostFull` (above 85 %), `…NoIo`, `…MigrationFailed` in `deploy/monitoring/`.
10. **Documentation:**
    - `docs/admin-guide.md` section "Instant VM recovery (vSphere)": ports and firewall, the Windows role and rpcbind conflicts, overlay sizing, vCenter needed for Migrate, restart behaviour, limits, security;
    - the deploy script made aware of instant recoveries;
    - `THIRD-PARTY-NOTICES.md` (test tools only).
11. **Live checks:** `docs/lab-runbook.md` section J on the vSphere 8 test host.

| Check | What it shows |
|---|---|
| J1 | A Windows VM boots with NICs disconnected. |
| J2 | A Linux VM (LVM + XFS) boots. |
| J3 | 30 min of fio/diskspd with verify; the overlay grows. |
| J4 | Service restart under load: I/O pauses for less than 60 s, the session resumes. |
| J5 | Retention and a scale-out move during the session: the restore point is kept. |
| J6 | Migrate to VMFS (thin) under writes: hashes match, the datastore is removed, the overlay deleted, the pin released. |
| J7 | Migrate by hand in the vSphere client: the session detects it and finalizes. |
| J8 | Stop leaves nothing behind, on the host or in the server's directory. |
| J9 | Firewall blocks 2049: the error names the IPs and ports. |
| J10 | Server killed between the datastore mount and VM creation: startup cleanup removes the datastore. |
| J11 | Overlay full: the VM pauses with a question, the console warns. |
| J12 | Windows server with "Server for NFS" installed: a clear error about port 111. |

## Risks

- **ESXi NFSv3 quirks** (lock files, attribute caching, fsid checks): spike S1.
- **The restart window against guest disk timeouts:** spike S3. The listener must come up early in startup.
- **Random boot reads from compressed 1 MiB chunks:** the cache, read-ahead, and an optional warm-up of the first 64 MiB.
- **Pack files change how chunks are read.** That stays hidden behind `IChunkReader`.
- **The destroy guard:** a VM that is not entirely on our datastore is never destroyed.
- **The overlay volume filling up:** budget and reserve.

## Later: XCP-ng

The same NFS server, namespace and overlay engine can back an XCP-ng NFS SR.
- SM expects `<sr-uuid>/<vdi-uuid>.vhd`, so a dynamic VHD is synthesized on the fly over the chunk index (`VhdImageWriter` logic). The byte-level overlay absorbs tapdisk's BAT and block-append writes.
- The SR is attached with sr-introduce, pbd-create, pbd-plug and sr-scan; the VM is created through XAPI; storage live migration replaces Storage vMotion.
- The new parts are the VHD synthesis and the XAPI lifecycle, not the NFS server.
