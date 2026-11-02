# Plan: XCP-ng Backup Foundation — first end-to-end slice

Spec folder: `agent-os/specs/2026-11-01-2134-xcpng-backup-foundation/`

## Context

backdafuckup is a new backup product for XCP-ng / XenServer, with vSphere and Hyper-V coming later. It is self-hosted, has a Veeam-like web console, and will be published as **open source under AGPL-3.0, with a CLA** for outside contributors. The repo contains only product docs, research material and reports; there is no code yet. This spec builds the foundation and a first real slice:

1. Register a pool.
2. Define a scheduled job.
3. Run **full and CBT-incremental** backups, read via **NBD** into our own chunk repository on a local volume.
4. Apply retention.
5. Restore a whole VM to the original or an alternate location.
6. Follow progress live in the web UI.

Every later feature plugs into the abstractions built here: SMB/NFS/S3 targets, Alletra storage-snapshot backup, the guest agent for VSS/SQL/AD, vSphere and Hyper-V.

**Confirmed with the user during shaping**
- **Server platform:** must run on Linux *and* Windows.
- **First target:** a directory on a local volume.
- **Format:** our own chunk format.
- **Scope:** includes schedule, retention, alternate-location restore and live progress. Data path: NBD and CBT from the start.
- **Licensing policy:** the product is open source. Open-source dependencies are OK, including copyleft. Forbidden: anything obtainable only through a purchased product (Veeam binaries, VDDK), partner-gated SDKs, and undocumented APIs.
- **Project licence:** AGPL-3.0 plus a CLA.
- **Standards:** none exist yet (`agent-os/standards/index.yml` is empty). **Visuals:** none.

**Facts from research that shape the design**
- `VM.snapshot` is crash-consistent only. Quiesce/VSS was removed in Citrix Hypervisor and XCP-ng 8.1. App-consistency therefore needs our own guest agent, in a later spec.
- **CBT:**
  - `VDI.enable_cbt`
  - `VDI.list_changed_blocks(from, to)` returns a base64 bitmap with one bit per 64 KiB block. `to` must not be attached.
  - `VDI.data_destroy` keeps only the CBT log (`type=cbt_metadata`).
  - CBT is silently disabled after a host or SR crash, so we must fall back to a full backup.
- **NBD:**
  - `VDI.get_nbd_info` returns `{address, port 10809, cert, exportname}`.
  - TLS is required for networks with purpose `nbd`.
  - At most 16 concurrent connections.
  - The client must not run in dom0.
  - There is no .NET NBD client; the protocol spec is public domain.
- **Restore:** `VDI.create`, then `PUT /import_raw_vdi`, then `PUT /import_metadata` with `vdi:<old>=<new>` mapping. XAPI cannot apply incrementals, so we reconstruct full disks ourselves.
- **Environment:**
  - The repo is not a git repo yet.
  - **No .NET SDK is installed** on the dev machine. Docker is available.
  - `research/Xen/` contains proprietary Veeam binaries and must never be committed or used.

## Key decisions

| Topic | Decision |
|---|---|
| XAPI client | Our own thin async JSON-RPC client, about 40 methods, using System.Text.Json source generation and per-host TLS pinning. The XenServer.NET SDK (BSD-2) is a reference only. Reasons: the SDK is sync, uses process-wide certificate hooks, and we write the HTTP handlers and NBD ourselves anyway |
| NBD | Our own `Backdafuckup.Nbd` client: fixed-newstyle, STARTTLS with pinned cert, OPT_GO with fallback to EXPORT_NAME, simple replies, pipelining, a multi-connection pool |
| Chunks | 1 MiB fixed size, set per repository. SHA-256 (BCL, hardware-accelerated) over plaintext. zstd level 3 via ZstdSharp.Port; a chunk is stored raw if compression doesn't shrink it. All-zero chunks are not stored. Every restore point holds a complete index, giving forever-incremental with dedup |
| Scheduler | Our own `PeriodicTimer` scheduler, daily and weekly, timezone- and DST-aware, testable with `FakeTimeProvider` |
| UI | Blazor Web App with global InteractiveWebAssembly, hosted by the server. Uses only REST and SignalR. MudBlazor (MIT, targets net10.0) |
| Auth | ASP.NET Core Identity with local accounts and cookie auth. First-run setup token. CSRF header on mutating calls. AD, LDAP and OIDC later |
| DB | PostgreSQL via Npgsql EF Core 10, snake_case naming, Guid v7 keys, `xmin` concurrency. Credentials encrypted with ASP.NET Data Protection; the key ring lives on disk |
| Hosting | One process, `backdafuckup`: API, hub, engine and UI host. `AddSystemd()` and `AddWindowsService()`. Kestrel on `https://*:9443` with a self-signed certificate on first start. Serilog |
| Restore stream | `import_raw_vdi?format=raw` as an ordered stream reconstructed from chunks with parallel prefetch. A sparse `format=vhd` stream is a later optimization (risk 3) |

## Solution structure

```
backdafuckup.slnx, global.json (10.0.x), Directory.Build.props, Directory.Packages.props,
nuget.config (nuget.org only), .editorconfig, .gitignore, LICENSE (AGPL-3.0), CONTRIBUTING.md (+CLA),
THIRD-PARTY-NOTICES.md, deploy/{linux/backdafuckup.service, windows/install-service.ps1}
src/
  Backdafuckup.Core        abstractions only (BCL): hypervisor provider, data mover, repository, manifest, ChunkId, extents/bitmaps, IOperationLog, IArtifactLedger
  Backdafuckup.DataMover   hypervisor-neutral pipelines: DiskBackupPipeline, DiskImageSource      → Core
  Backdafuckup.Storage     LocalDirectoryRepository: codec, index/manifest, pins, writer registry, GC, verify → Core, ZstdSharp.Port
  Backdafuckup.Nbd         standalone NBD client + connection pool (no deps)
  Backdafuckup.Xen         XAPI client, TLS trust, HTTP handlers, XenProvider (inventory, backup/restore session, CBT, NBD transport, cleanup) → Core, Nbd
  Backdafuckup.Contracts   REST DTOs + SignalR client interface
  Backdafuckup.Server      composition root: API, SignalR hub, EF Core + migrations, Identity, Data Protection, job/restore engine, recovery, hosting → all
  Backdafuckup.Web         Blazor WASM client, MudBlazor, typed API client, SignalR client → Contracts
tests/  Backdafuckup.Testing (InMemoryDisk, FakeHypervisorProvider with simulated CBT, nbdkit/qemu-nbd fixtures),
        Nbd.Tests, Storage.Tests, DataMover.Tests, Xen.Tests, Server.Tests, Lab.Tests (opt-in E2E, env BDFU_LAB_*)
```

## Core abstractions (extension points)

- **`IHypervisorProvider`**, keyed by `HypervisorKind`, provides:
  - `ProbeCertificateAsync` (trust on first use)
  - `TestConnectionAsync`
  - `OpenAsync` → `IHypervisorSession`, which offers inventory, `BeginBackupAsync`, `BeginRestoreAsync`, artifact cleanup and orphan scan
- **`IVmBackupSession`**: `Disks`, `Metadata` blob, `GetChangedExtentsAsync(disk, since)` (returns full with a reason, or a list of extents), `OpenReaderAsync`, `GetNewChangeToken`, `CommitAsync`, `ReleaseChangeTokensAsync`. Disposing without commit rolls back.
- **`IVmRestoreSession`**: `ValidateAsync`, `CreateDiskAsync` → `IDiskWriter`, `CreateVmAsync`, `CompleteAsync`. Rolls back unless completed.
- **Data mover:** `IDiskReader` (random access, thread-safe, `MaxConcurrentRequests`), `IDiskTransport` (transport mode: xen-nbd now; later array snapshot and HotAdd), `ISnapshotLifecycleHook` (later: array snapshot right after the VM snapshot), `IDiskImageSource` (a restore-point disk as an image, with sequential prefetch or random reads), `IDiskWriter`, `IDataMover`.
- **Repository:** `IBackupRepository` (`Chunks`, `RestorePoints`, `BeginWrite(rpId, basePin)`, GC, verify, stats), `IRepositoryWriter` (`PutChunkAsync` returns Stored or Deduped; `WriteDiskIndexAsync`; `WriteBlobAsync`; `CommitAsync` with the manifest as the commit point), `IRestorePointStore` (list, open, pin, delete).

## Repository format (v1)

```
<root>/repo.json            {formatVersion, repositoryId, chunkSize, hash:"sha256", compression:{zstd,3}}
<root>/.owner.lock          exclusive lock → one server per repository
<root>/chunks/<hex0..4>/<sha256hex>      write-once; 16-byte header "BDCK" v1, codec, flags (bit0 = encrypted, reserved), rawLen, payloadLen
<root>/restore-points/<vmUuid>/<ts>_<rpId>/{manifest.json, disk-<key>.idx (zstd, "BDIX" + 32-byte entries), vm-metadata.tar.zst}
<root>/tmp/                 staging, purged on open
```
- **Atomic writes:** write to tmp, flush, rename; on Linux also fsync the directory. Commit order is chunks, then indexes and blob, then `manifest.json`. A restore point exists only if its `manifest.json` exists.
- **Deletes:** DB state `Deleting`, rename to `manifest.deleted`, remove the directory, delete the DB row. Interrupted deletes resume on startup.
- **Integrity:** every restore read verifies SHA-256. Verify runs in two depths: Quick (index hashes and chunk existence) and Full (decompress and hash every chunk).
- **GC:** mark and sweep with no mtime touching.
  - Mark every chunk ID referenced by a committed manifest.
  - Each active writer keeps a protected set of its chunk IDs, and its base restore point is pinned.
  - Writes and sweeps use 4,096 stripe locks.
  - Sweep deletes a chunk only if it is unmarked, in no protected set, and older than T0 − 10 min.
  - This layout suits S3 and immutability later, because nothing is ever updated in place.
- **Import:** adding a path that already contains a `repo.json` imports that repository and rebuilds the catalog from its manifests (disaster recovery of the backup server).

## Database (EF Core entities)

- **Users:** `AppUser` / Identity, plus an Administrator role.
- **Connections:** `HypervisorConnection` (address, master, pool uuid, trust mode, AllowInsecureNbd), `ConnectionHost` (per-host certificate pins: SPKI SHA-256), `Credential` (secret protected with Data Protection, never returned by the API), `InventoryItem` (neutral cache with `PropertiesJson` as jsonb).
- **Storage:** `Repository` (path, RepositoryUuid, ChunkSize, MaxConcurrentTasks, SettingsJson for later kinds).
- **Jobs:**
  - `BackupJob`: connection, repository, `RetentionPoints`, owned `Schedule` (None/Daily/Weekly, time, days, TimeZoneId), MaxParallelVms 2, NbdConnectionsPerDisk 4, retry count and delay, NextRunUtc.
  - `BackupJobObject`: VM selection.
- **Restore points:** `RestorePoint` (state Creating/Available/Deleting/Missing/Corrupt, kind Full/Incremental, `VmConfigJson`), `RestorePointDisk` (mode cbt or full, FullReason, `ChangeTokenValue` = cbt_metadata VDI uuid, byte statistics).
- **Sessions:** `Session` (kind Backup/Restore/RepositoryMaintenance/InventoryRefresh, state, counters, ServerInstanceId), `SessionTask`, `SessionLogEntry` (with an optional `Key`, so a line can be updated in place, Veeam-style).
- **Recovery ledger:** `ProviderArtifact` (XapiTask, VmSnapshot, SnapshotVdi, CbtMetadataVdi, RestoredVdi, RestoredVm, plus a state).
- **Settings:** `AppSetting` (session limits, history retention 180 days, misfire grace).
- **Migrations:** applied at startup under a PostgreSQL advisory lock.

## Job engine

- **Scheduler:** ticks every 30 s and queues sessions for due jobs. If a job's previous session is still running, that run is skipped with a log line. Missed runs older than 1 h are skipped with a warning on startup.
- **Runner:**
  - Queue: `Channel<Guid>`, with the DB as the source of truth.
  - Session limits: 1 backup session and 2 restore sessions at a time.
  - `ResourceBroker` hands out repository task slots, an NBD connection budget of 12 per host, and compression workers.
- **Per-VM backup flow:**
  1. Pin the base restore point, create the restore point (Creating), open a repository writer.
  2. Enable CBT where it is missing; those disks get a full backup.
  3. `Async.VM.snapshot`, recording the task and snapshot in the ledger before waiting, and tagging the snapshot via `other_config`. Then `export_metadata`.
  4. For each disk, try incremental. It requires: same disk, same capacity, a base change token, the same chunk size, and CBT still enabled. If so, call `list_changed_blocks` before NBD attaches. Any doubt means a full backup with a warning.
  5. `get_nbd_info`. If no NBD network is available, fail with an actionable error. Open the NBD pool.
  6. Pipeline: read dirty chunks → zero detection → SHA-256 → skip if equal to the base → dedupe or compress → store. Unchanged chunks keep their base index entries.
  7. Commit: manifest, DB transaction, `data_destroy` of the snapshot VDIs, `VM.destroy(snapshot)`, then destroy the old cbt_metadata VDIs.
  8. Retention: keep the newest N restore points, then queue a coalesced GC session.
- **On failure or Stop:** close NBD, destroy the snapshot, abort the writer. The base token stays valid, so the next run is still incremental. Failed VMs are retried at the end of the session.
- **XAPI rules:**
  - One XAPI session per VM task, because the NBD export name embeds the session.
  - Originator `"backdafuckup"`.
  - Idempotent reads and destroys retry 10× every 10 s (Polly). Create and snapshot calls are never retried.
  - Handle `HOST_IS_SLAVE` by redirecting to the master and `SESSION_INVALID` by logging in again.
  - Redact `session_id` and `exportname` in logs.
- **`StartupRecoveryService`:**
  - Mark stale sessions Failed.
  - Finish or roll back Creating restore points.
  - Process open ledger rows.
  - Resume interrupted deletes.
  - Run a periodic orphan scan for tagged snapshots and VDIs.

## Restore flow

- **Original location:**
  - If the original VM is gone: `import_metadata restore=true`, which keeps the UUID and MACs.
  - If it exists: requires an explicit "Replace". The old VM is shut down and renamed `<name> (replaced <ts>)`. It is deleted only if "Delete original after success" is ticked (default off).
- **Alternate location:** any registered pool. Choose host (affinity or `start_on`), SR per disk, network per VIF, a new name, regenerated MACs (on by default; forced on if the original exists in the target pool) and power on.
- **Steps:**
  1. Pin the restore point, verify its indexes, then validate: SR space, name clash, networks; vTPM produces a warning.
  2. For each disk, `VDI.create`, recorded in the ledger.
  3. `PUT import_raw_vdi?format=raw` to the master, or for local SRs to the host whose PBD is attached, with that host's pinned cert. The body is a `DiskImageContent` stream: 8 prefetch workers, an ordered window, a static zero buffer, 4 MiB writes.
  4. `PUT import_metadata` with the VDI mapping, then parse the `OpaqueRef` from the task result.
  5. Rename, remap VIFs, set affinity, optionally start.
- **On failure:** roll back by destroying the created VM and VDIs.

## Web UI (MudBlazor, Veeam-like left navigation)

- **First run and dashboard:** `/setup` (setup token) and `/login`. `/` dashboard: job results for the last 24 h, running sessions, repository capacity, protected and unprotected VMs.
- **Pools:**
  - Add Pool wizard: address → certificate trust dialog with fingerprints → credentials → test (with a second trust prompt for the master on redirect) → summary.
  - Pool detail: hosts, networks with NBD status, an "Enable NBD (TLS)" action that asks for confirmation, rescan, re-trust a certificate, remove.
- **Inventory and repositories:** `/inventory` is a read-only tree. `/repositories` covers add/import (path checks, chunk size, compression level, max tasks), detail, GC now and rescan.
- **Jobs:**
  - `/jobs` grid with Run (incremental / Active Full), Stop, Enable/Disable, Edit and Delete. Delete offers to disable CBT, because CBT blocks storage live migration.
  - Job wizard: Name → VMs → Storage (repository, retention) → Schedule → Summary.
- **Sessions:** `/sessions` list. `/sessions/{id}` shows statistics, per-VM progress and a live log via SignalR (`/hubs/live`, groups `session:{id}` and `dashboard`, updates throttled to 1 Hz).
- **Backups and restore:** `/backups` (job → VM → restore points: Restore, Verify, Delete). Restore wizard: point → mode → targets → summary with a replace warning.
- **Settings:** `/settings` covers users, limits, history retention, and an About page with third-party notices.

## Tasks

1. **Save spec documentation.** Create `agent-os/specs/2026-11-01-2134-xcpng-backup-foundation/` with:
   - `plan.md`: this plan.
   - `shape.md`: scope, what is in and out, the decisions above and all user confirmations.
   - `standards.md`: notes that no standards exist yet, lists the conventions this spec establishes (async methods with a final CancellationToken, ProblemDetails errors, XAPI retry rules, repository format versioning, test categories Unit/Integration/Lab), and suggests running `/discover-standards` afterwards.
   - `references.md`:
     - XAPI class reference and wire protocol
     - XenServer CBT/NBD developer guide
     - xs-cbt-samples (BSD-3)
     - NBD `doc/proto.md`
     - nbdkit and qemu-nbd
     - `research/Xen/Service/appsettings.json` (Veeam timeouts and retries, inspiration only)
     - HPE WSAPI spec (future SAN path)
   - `visuals/.gitkeep`.
2. **Update product docs:**
   - `tech-stack.md`, Integration Policy: open source under AGPL-3.0 plus CLA. Open-source dependencies, including copyleft, are allowed. Forbidden are purchased-product-only code and tools, partner-gated SDKs, and undocumented APIs. `research/` is never published.
   - `tech-stack.md`, decisions: MudBlazor, own XAPI and NBD clients, the chunk format, hosting.
   - `mission.md`: describe the product as open source.
   - `roadmap.md`: move CBT incrementals into the MVP. Add to Application awareness that it needs our own guest agent, since quiesce was removed. Mark the local volume as the first target.
3. **Dev environment and scaffold:**
   - Install the .NET 10 SDK on the WSL dev machine (with user OK).
   - `git init` (no commits unless asked). `.gitignore` excludes `research/` entirely, since it holds Veeam binaries and the unlicensed HPE spec.
   - LICENSE (AGPL-3.0), CONTRIBUTING with the CLA (the CLA text needs legal review).
   - `global.json`, `.slnx`, `Directory.*.props`, all projects.
   - Server skeleton: Serilog, systemd and Windows service hosting, self-signed Kestrel certificate, `/health`.
   - PostgreSQL dev container via `docker compose`.
   - CI on GitHub Actions: build and unit tests on Ubuntu and Windows; integration tests on Ubuntu with Docker.
4. **Core:** the abstractions, manifest records (System.Text.Json source generation), `ChunkId`, extents, the CBT bitmap → extents → dirty-chunk mapping, `IOperationLog`, `IArtifactLedger`. Unit tests.
5. **NBD client:**
   - Protocol: handshake, STARTTLS with a pinned-cert callback (TLS 1.2/1.3), OPT_GO with INFO_BLOCK_SIZE and fallback to EXPORT_NAME.
   - Transmission: pipelined requests with cookies.
   - Cancellation: in-flight replies are drained into a scratch buffer, so the caller's buffer is never written after the call returns.
   - `NbdConnectionPool : IDiskReader` with reconnect.
   - Tests against nbdkit and qemu-nbd containers.
6. **Storage:** `LocalDirectoryRepository` (layout, owner lock, codec, atomic writes, index and manifest, pins, writer registry, stripe locks, GC, verify, delete protocol, import). Tests.
7. **DataMover:** `DiskBackupPipeline` (bounded memory, parallel hashing and compression, base reuse, zero and unchanged detection, statistics) and `DiskImageSource`. Round-trip tests. A BenchmarkDotNet project.
8. **XAPI client:**
   - Calls: JSON-RPC 2.0, pinning, probe, login, master redirect and failover, re-login, typed records, task waiting.
   - HTTP handlers: `export_metadata`, `import_raw_vdi`, `import_metadata`, with manual 302 handling.
   - Plumbing: Polly retries, redaction, fake-server tests.
   - **Lab spike first:**
     - JSON-RPC error shape
     - **CBT bitmap bit order**
     - XAPI NBD support for OPT_GO and its maximum request size
     - `import_metadata` task-result format
     - `import_raw_vdi` redirect behaviour for local SRs
9. **Xen provider:** inventory mapping, `XenBackupSession` (CBT handling, snapshot tagging, metadata, `XenNbdTransport`), `XenRestoreSession`, artifact cleanup and orphan scan, `network.add_purpose` helper, version check (XCP-ng 8.3 / XenServer 8).
10. **Catalog DB:** entities, initial migration, Identity, Data Protection and `CredentialProtector`, startup migration under an advisory lock, session-history cleanup. Testcontainers tests.
11. **Job engine:** scheduler, queue and runner, ResourceBroker, `VmBackupTask` (full, incremental, fallback, commit, rollback), retries, retention, GC sessions, Stop, StartupRecoveryService, SessionReporter. Tests with `FakeHypervisorProvider` and `FakeTimeProvider`.
12. **Restore engine:** `RestoreSessionRunner` with validation, original and alternate modes, replace flow, pinning and rollback.
13. **REST API and SignalR:** endpoint groups for auth/setup, connections, repositories, jobs, sessions, restore points and restores; the live hub; CSRF middleware; OpenAPI. Tests with WebApplicationFactory.
14. **UI part 1:** shell, setup/login, API client, dashboard, pools with trust dialog and NBD panel, inventory, repositories.
15. **UI part 2:** jobs and wizard, run/stop, sessions with live view, backups browser, restore wizard. bUnit tests.
16. **Deployment and E2E:** systemd unit (dedicated user, ReadWritePaths), Windows service script, admin README (prerequisites: PostgreSQL, NBD network, not in dom0, required XAPI role), `Lab.Tests` with the runbook below.

**Order:** after task 4, tasks 5, 6 and 8 can run in parallel. Task 7 needs 6. Task 9 needs 5 and 8. Tasks 11 and 12 need 7, 9 and 10.

## Verification

- **Unit and integration:**
  - Bitmap decoding (bit order switchable until the lab confirms it).
  - Disk-end boundaries.
  - Manifest snapshot tests.
  - **NBD** against nbdkit (`pattern` plugin for offset-verifiable data, TLS, wrong pin, `delay` filter for out-of-order replies, `error` filter, cancellation, multiple connections) and against qemu-nbd with TLS.
  - **Storage:** a flipped byte is detected; tmp is cleaned up; concurrent puts; GC stress test with random writers and GC loops, ending with a full verify that finds no missing chunks; crash-safe deletes.
  - **DataMover:** a 2 GiB random image with zero regions → full → mutate 64 KiB blocks → incremental → both restore points hash-identical. After deleting the full and running GC, the incremental still restores.
  - **DB and engine:** Postgres via Testcontainers. Scenarios: schedule and DST, full then incremental, every fallback case, failure leaves the next run incremental, crash simulation, Stop, retention.
- **E2E lab runbook** (`Lab.Tests`, against the team's XCP-ng pool):
  1. Setup: a Debian VM with a 10 GB system disk and a 5 GB data disk, an NBD network, two SRs.
  2. Register the pool, add a repository, create a job with retention 3, run a Full, then Full verify.
  3. In the guest, write 500 MB of random data and run `sha256sum`. Run an Incremental and assert mode=cbt and that read bytes are about the changed amount. Repeat.
  4. Restore to an alternate SR with a new name, new MACs and a mapped network. The VM boots, the in-guest hash matches, and the block hash of each restored VDI (read via NBD) equals the hash of that restore point.
  5. Restore to the original location with Replace; the VM boots.
  6. Failure injection: kill the service mid-transfer (no leftovers, next run OK); `xe vdi-disable-cbt` (full fallback with a warning); delete the cbt_metadata VDI (fallback); remove the NBD purpose (clear error).
  7. Exceed the retention count, then check that GC frees space and the remaining restore points restore.
  8. The service runs and survives a reboot under systemd and as a Windows service.

## Risks and open questions (resolve during lab spike / implementation)

1. **CBT bitmap bit order.** A wrong order means silent data loss. Incrementals stay disabled until the lab spike confirms the order; the E2E block-hash check guards it permanently.
2. **XAPI NBD capabilities and throughput are unknown.** Also open: whether customers accept enabling NBD on the management network or need a dedicated network.
3. **`import_raw_vdi format=raw` writes zeros.** That inflates thin SRs and slows restores, so the sparse `format=vhd` stream may need to come into this spec.
4. **`import_metadata` edge cases:** cross-pool network matching, ISO/CD devices (`force`), vTPM state on XCP-ng 8.3 (warning only for now), UEFI NVRAM.
5. **CBT blocks storage live migration.** Document it and show it in the UI.
6. **Required XAPI RBAC role.** Enabling the NBD purpose probably needs pool-admin; to be documented.
7. **GC memory.** The in-memory HashSet is fine up to about 20 TB of unique data; a Bloom filter comes later.
8. **ZstdSharp throughput.** Benchmark it; a native libzstd binding is allowed if needed.
9. **Data Protection key ring.** Losing it means pool credentials must be re-entered. Document backing up `<dataDir>/keys`.
10. **CLA text and the AGPL licence notice need legal review before the first public release.**

---

## Implementation status (2026-11-01)

Tasks 1–16 are implemented. 62 automated tests pass: unit tests, plus integration tests against PostgreSQL via Testcontainers, nbdkit and a fake XCP-ng pool. The 2 lab tests are skipped until they run against a real pool.

**Deviations from the plan:**

- **HTTPS port 9555 instead of 9443.** 9443 is commonly taken (Portainer, monitoring agents). It is configurable via `Server:HttpsPort`.
- **UI hosting:** the UI is a standalone Blazor WebAssembly app hosted by the server (`UseBlazorFrameworkFiles`), instead of the Blazor Web App template. It still uses only REST and SignalR.
- **"Enable NBD" action:** not in the UI yet. The pool page and the admin guide show the `xe network-param-add` command instead.
- **Restore point verify:** runs inline in the API request instead of as its own session.
- **No BenchmarkDotNet project yet.**
- **Incremental backups are gated by `Xen:CbtBitOrder`.** Without that setting every backup is a full read (still deduplicated). The lab test `Cbt_bit_order_spike` determines the value on the real pool.

**Still open (needs the lab pool):**

- Run `Cbt_bit_order_spike` and `Backup_and_restore_roundtrip`.
- Check XAPI NBD capabilities and throughput.
- Check `import_metadata` edge cases: vTPM, ISO devices, cross-pool networks.

See `docs/admin-guide.md`.

### Follow-up 2026-11-02 (first test against the team's pool)

- **HTTP export fallback.** Added `export_raw_vdi` as a second transport. Jobs have a transport setting: Automatic (NBD, otherwise HTTP export) / NBD only / HTTP export only; new column `jobs.transport_mode`.
  - The data path is `ISequentialDiskReader` plus a streaming path in `DiskBackupPipeline`.
  - VHD streams are parsed by our own `VhdStreamReader`, written from the public VHD specification. Sectors marked unused must be zero, otherwise the stream is rejected.
  - `Xen:HttpExportFormat=Raw` is the escape hatch. The lab test `Http_export_vhd_matches_raw` checks the parser against the real export.
- **Transport check in the session log.**
  - Logged: the NBD networks with host IPs, the result of `VDI.get_nbd_info`, every NBD connection attempt (address, port, host, network, duration, error), and the HTTP request and stream format.
  - All NBD endpoints offered by XAPI are tried before giving up.
  - Error messages always name the concrete addresses.
- **Setup token.** A rejected password no longer invalidates the setup token.
- **Transfer statistics.**
  - The pipeline counts processed, unchanged, empty, deduplicated, new and stored bytes, and measures the busy time of source, processing and target.
  - Tasks and sessions keep `TransferMetricsDto` (jsonb columns `metrics`), with per-disk details on tasks. The current read rate, dedupe and compression ratios and the bottleneck are shown live on the session page and in the session list.
- **Snapshot release after the export.** On the team's pool, `VDI.data_destroy` returned VDI_IN_USE for more than a minute after an HTTP export, and the rollback then deleted the snapshot together with its change tracking data.
  - Now the backup waits up to `Xen:SnapshotReleaseTimeout` (default 5 min) and logs who holds the disk (VDI operations, VBDs with their VMs).
  - After the timeout the snapshot is kept and the ledger rows stay CommitPending. `PendingArtifactRetryService` retries every 10 minutes; startup recovery retries too.
  - The HTTP export uses its own connection and closes it afterwards (`Connection: close`).
- **Lab tests without NBD.** The lab settings can come from `~/.config/backdafuckup/lab.env` (or `BDFU_LAB_ENV_FILE`) instead of environment variables, so the password stays off the command line. The tests finish a deferred snapshot commit themselves and report how long the disk stayed in use. Test output is shown live (`xunit.runner.json`). They only run with `BDFU_LAB_RUN=1` set for the run; the file alone never enables them.
- **Snapshot cleanup order (root cause of VDI_IN_USE).** On the pool, `VDI.data_destroy` failed for as long as the snapshot VM's unattached VBD referenced the disk; nothing else used it. The commit now destroys the snapshot VM first (`VM.destroy` keeps the disks) and then reduces the disks. For older ledger rows, the disk is first detached from backdafuckup's own tagged snapshot VM. Confirmed on the pool: the kept snapshot was cleaned up at the next start without VDI_IN_USE.
- **Sparse restore upload (risk 3).** `import_raw_vdi?format=vhd` with a dynamic VHD written by our own `VhdImageWriter`. It sends only the 2 MiB blocks that contain data, as taken from the restore point's data extents. A block outside the extents must read as zero, otherwise the upload stops. `Xen:HttpImportFormat=Raw` is the escape hatch. The session page shows restored and transferred bytes. Not yet verified against a real host; the lab roundtrip test covers it.
- **Parallel disks per VM.** New job setting `MaxParallelDisks` (default 2, column `jobs.max_parallel_disks`). One HTTP export stream is limited by the host to about 60 MB/s, while our VHD parser handles several GB/s. A failing disk stops the others, and its own error is reported. VM rates use the wall time of the disk transfers. The default respects a 1 Gbit/s link, where two streams already reach the line rate.
