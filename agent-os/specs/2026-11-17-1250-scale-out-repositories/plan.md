# Plan: Scale-out Repositories

## Context

**What the user wants.** Like Veeam's scale-out backup repositories (public docs as ideas only):
- several disks (extents) form one repository;
- backup chains are spread over them;
- balancing, moving chains back and forth, evicting an extent, maintenance mode.

**What the code does today.**
- **A repository is one directory** (`LocalDirectoryRepository`). It is its own chunk store, so dedup stays inside one repository.
- **A chain is tied to its directory.** Restore points only reference chunks, but an incremental clones its base's disk index (`src/Backdafuckup.DataMover/DiskBackupPipeline.cs:105-116`) and Kubernetes file trees reuse the previous tree. A chain (job + VM) therefore has to stay in one chunk store.
- **Every read path starts from the point.** It goes `point.RepositoryId → RepositoryManager.GetAsync → Pin → Open`.

**Design idea**
- **Extents are ordinary directory repositories.** A scale-out repository is a catalog grouping of extents. Restore points and SQL Server backups keep the *extent's* id, so restore, verify, tape and file restore keep working unchanged.
- **Jobs target the scale-out repository.** A placement service decides which extent receives each chain's new data.
- **A move copies the chain with the same ids,** switches the catalog rows, then deletes the source copy.

## User decisions (2026-11-17)

- **Placement unit:** job + VM (or Kubernetes namespace / `@cluster`), together with that VM's SQL Server backups of that job. New chains go to the extent with the most free space.
- **Unusable extent:** if a chain's extent is sealed, in maintenance or offline, the next backup starts a new full on another extent. The old points stay readable (an offline or maintenance extent is not readable while it is so) until retention deletes them or they are moved.
  - In maintenance or offline this is a setting (`FullWhenExtentUnavailable`, default on; off means the VM is skipped with a warning).
- **Converting existing repositories:** "Add extent" on a standalone repository makes it extent 1 of a new scale-out repository. Its jobs then point to the scale-out; its chains stay where they are.
- **Balancing:** placement of new chains, plus a manual *Rebalance* that shows its plan first, plus an **automatic** rebalance in a time window. It triggers when an extent is below its free-space threshold or the extents are too unequal.
- **Functions:** move chains manually, evacuate an extent, maintenance mode, seal, add and remove extents.

**My defaults** (the review asked about them; change them in the plan review if needed):
- **Evacuating:** chains keep their incrementals on the extent until the move switches them, instead of a new full at once. A new full there would cost a full plus a chain copy.
- **SQL Server runs** whose extent is briefly unavailable write to another extent for that run only; the placement stays.
- **Move sessions:** one at a time, alongside backups.
- **Same volume:** extents on the same volume are refused (free space would count twice). A setting allows it, for tests and the lab.
- **Not in v1:** detaching an extent with its data, and dissolving a scale-out.
- **Extents are directories only.** SMB, NFS and S3 extents come with those target kinds.

## Data model (migration `ScaleOutRepositories`)

**`BackupRepositoryEntity`** (`src/Backdafuckup.Server/Data/Entities.cs:282`)
- `RepositoryKind` gains `ScaleOut`. A scale-out row has `Path = ""`, `RepositoryUuid = Id`, and the same `ChunkSize` as its extents (checked; `BeginImport` refuses different sizes).
- `ScaleOutId?`: a self-referencing FK, Restrict.
- `ExtentState { Active, Sealed, Evacuating }`. An evacuation that is cancelled leaves the extent Sealed.
- `Maintenance` (bool) and `MaintenanceSinceUtc`.
- Offline is the observed `State`, now saved by a health check together with `LastError`.
- `ScaleOutSettings` (jsonb, scale-out rows only):

  | Setting | Default |
  |---|---|
  | `NewChainMinFreePercent` | 5 |
  | `FullWhenExtentUnavailable` | true |
  | `AutoRebalance` | false |
  | `Window` | days, start, end (may cross midnight), time zone |
  | `LowFreePercent` | 10 |
  | `ImbalancePercent` | 15 |
  | `MaxBytesPerRun` | – |
  | `CooldownDays` | 7 |
  | `MaxMoveMBps` | – |
  | `MoveSwitchWait` | 6 h |

**`chain_placements`**: where a chain's *new* data goes.
- Columns: `ScaleOutId`, `JobId` (cascade), `ObjectNativeId`, `ExtentId` (Restrict), `PlacedUtc`, `Reason`, `MoveSessionId?`, `EstimateBytes?`.
- Unique on (JobId, ObjectNativeId).
- The restore points stay the truth for what exists.

**`repository_move_items`**: the move journal.
- One row per object copy: session, scale-out, job, object, type (RestorePoint | SqlBackup), object id, source, target, bytes, error, attempts.
- State: Copied → Switched → SourceDeleted, or Abandoned → TargetDeleted, or Failed.
- Rescan, startup, the cleanup service, the remove guards and the console read it.

**`SessionKind.RepositoryMove`** is added at the end. Also update the metrics kind `repository_move`, `AuthState.cs:189`, the `Sessions.razor` filter and `ObjectKindUi.cs:44`.

## Two rules that make moves safe for readers

**Rule A, a double-checked open.** A new `Engine/RestorePointAccess.cs` (and the same for SQL Server backups):
1. Read the row, open its extent, pin.
2. Read again and check that the point is `Available` and the row still names the pinned extent.
3. Otherwise release and retry, at most 3 times. A `KeyNotFound` from `Pin` also means "read again".

Readers switched to Rule A:
- **Restores:** VM/namespace (`Engine/RestoreSessionRunner.cs:39-57`), cluster (`.Cluster.cs:63`), file restore (`FileRestoreSessionRunner.cs:58`), browse mounts (`FileRestoreMounts.cs:284`).
- **SQL Server:** publish/export (`SqlPublishSessionRunner.cs:89,227`; log sets through `sql_backups`/`restore_points`, not the copied set column), restore (`SqlRestoreSessionRunner.cs:89,517`), downloads (`SqlEndpoints.cs:454,497`).
- **AD:** `Ad/AdExplores.cs:486,715`. Today it reopens hours later with a cached id; it now resolves by point id every time.
- **Tape job:** `Tape/TapeBackupRunner.cs:89,108` with `TapeSetWriter.cs:72`. A point that is gone is skipped with a warning; `CopySqlBackupAsync` now pins (`PinSqlBackup`).
- **Endpoints:** verify, delete, namespace and cluster runs (`OperationEndpoints.cs:61,72,277,304,478,491`).

**Rule B, compare-and-set on the row instead of locks.**
- Retention and delete mark a row `Deleting WHERE id AND state='Available' RETURNING repository_id`, then delete on that extent.
- The switch sets `repository_id = B WHERE id AND repository_id = A AND state = 'Available'`.
- PostgreSQL row locks order the two: the loser sees 0 rows. A lost switch abandons and deletes the target copy.
- The same applies to `sql_backups`.
- A source copy is deleted only after the switch has committed, and never while it is pinned.

## Placement (`src/Backdafuckup.Server/ScaleOut/RepositoryPlacement.cs`)

**Service methods**
- `ScopeAsync(repositoryId)` returns a standalone repository, or a scale-out with its extents.
- `ForBackupAsync(job, object, …)` returns the extent, whether a new chain starts, and a reservation. It runs under the chain lease.
- `ForSqlAsync(job, vm)` and `ForImportAsync(repositoryId, estimate)` (tape restore into a scale-out).
- Choices are serialized per scale-out.

**How extent states behave**

| Extent | New chains | Next image backup of a chain on it | Reads |
|---|---|---|---|
| Active, enough room | yes | incremental there | yes |
| Active, below `NewChainMinFreePercent` | no | incremental there (warning); rebalance relieves it | yes |
| Sealed | no | new full elsewhere | yes |
| Evacuating | no | incremental there until the move switches the chain | yes |
| Maintenance / Offline | no | new full elsewhere, or skipped (setting) | no, a clear message |

**Choice and estimates**
- **Choice:** the most effective free space = probed free (`IVolumeSpace`, faked in tests) − reservations − reserve. Within 2 %, the extent with fewer running tasks wins (`MaxConcurrentTasks` finally gets a use).
- **Estimates:**
  - an existing chain: the average `NewStoredBytes` of its last 3 points;
  - a VM with history elsewhere: its logical bytes × its stored ratio;
  - otherwise: the disks (plus memory) × 0.5;
  - namespaces: the PVC sizes.
- **Out of space:** only new chains are refused when no extent has room. A disk-full error during a backup makes the next attempt start a new full on another extent.

**Hooks**
- **`Engine/BackupSessionRunner.cs`:**
  - `:49` uses `ScopeAsync`.
  - `VmBackupTask` gets scope, placement and chain locks instead of one repository. Uses at `:325,357,523,542,805,893,993`.
  - `RunCoreAsync :303`: take the chain lease, then `ForBackupAsync`, then log "→ extent X (reason)".
  - `:340`: `RepositoryId = extent`.
  - `FindBaseAsync :1025` looks on the placed extent only, so a new placement gets a full by itself.
  - When a new chain starts, the old head's change tokens are released.
  - Retention (`:217-258`) runs over all extents of the scope, with Rule B. An offline extent puts the row back and logs "removed later". GC is queued per touched extent.
- **`Sql/SqlBackupService.cs`:**
  - `:288` uses `ForSqlAsync`; `RunContext` gets `ExtentId`.
  - **Bug:** `:391` passes `Job.RepositoryId` to `SqlBackupReceiver`. It must pass the extent.
  - SQL retention (`:811-843`): Rule B, per-extent GC, offline-tolerant.
- **`Engine/EngineServices.cs`:**
  - `HasCapacity`: one `RepositoryMove` at a time, alongside backups.
  - Dispatch: the move runner.
  - `StartupRecovery`: a stale move becomes Failed ("resumes"); the balancer queues it again.
- **`RepositoryManager`** (`Engine/EngineInfrastructure.cs:165-236`):
  - refuses scale-out rows and extents in maintenance;
  - `CloseForMaintenanceAsync` waits until idle, then releases `.owner.lock`.

## Moving a chain (`ScaleOut/ChainMover.cs`)

**Chain leases.** A `ChainLocks` singleton holds one `FifoLease` per (job, object). `FifoLease` moves from `Tape/TapeLocks.cs` to `Engine/`. Image backup tasks hold the lease per attempt (not across the wait between retries). SQL Server log backups never take it.

**Steps**
1. **Check:** the target is Active and online, chunk sizes match. Set `placement.MoveSessionId`.
2. **Bulk copy without the lease, oldest point first.** Per point:
   - Rule A pin;
   - read its files and list its chunks;
   - `target.BeginImport(sameId)`;
   - for each chunk, the new `TryReuseChunk`: under the stripe lock it is protected against GC on the target, so only missing chunks are read (`PlanRead`/`ReadStoredAsync` → `PutEncodedChunkAsync`, hash-checked);
   - write the files and `CommitAsync` (Job and Base are kept; `RepositoryId` is set by the commit);
   - check by reading the indexes and blobs once;
   - journal: Copied.
3. **SQL Server objects** of (job, VM) are copied with the same ids and switched at once, one transaction each (Rule B):
   - `sql_backups.repository_id/manifest_path`;
   - `sql_backup_sets.repository_id`;
   - journal: Switched.
4. **Converge:** repeat step 2 until a pass copies at most one new point (at most 5 passes).
5. **Take the lease.** This waits for a running backup of that chain. After `MoveSwitchWait` the chain is left for the next run; its copies stay in the journal and are reused.
6. **Under the lease:** a last copy pass, then one transaction:
   - switch the points with Rule B (0 rows → Abandoned);
   - `sql_backup_sets` of the switched points;
   - placement → target (reason "moved");
   - check that no Available point of the chain is left on the source.
7. **Release the lease.** The next backup does a CBT incremental on the target, because the change tokens are unchanged.
8. **Clean up:**
   - `TryDelete…(timeout 5 s)` the source copies (pinned, or GC busy, is retried by the cleanup service);
   - delete Abandoned target copies;
   - queue GC on the source.
9. **SQL Server stragglers:** wait at most 10 min for SQL Server runs of that VM that started before the switch, then move what they wrote. A repair query keeps `sql_backup_sets.repository_id` equal to `restore_points`/`sql_backups` (also at startup).

**Crashes**
- Before the switch: the rows still name the source and the journal says Copied; the next run reuses the copies.
- After the switch: the journal says Switched; the cleanup service deletes the source.

**Evacuation**
- Repeats until the extent has no catalog rows, no undeleted switched copies and no stragglers.
- Points with no job (imported from tape, or of a deleted job) are moved one by one.
- Then a final GC; *Remove extent* becomes possible.

## Storage additions (`src/Backdafuckup.Storage`, `src/Backdafuckup.Core/Repository`)

- `IRestorePointImport` / `ISqlBackupImport.TryReuseChunk(ChunkId)`.
- `TryDeleteRestorePointAsync` / `TryDeleteSqlBackupAsync(id, wait)` → Deleted | Pinned | Busy | NotFound. Today's delete waits on `_gcMutex`, which packing holds for up to 4 h.
- `ContainsRestorePoint` / `ContainsSqlBackup`: when resuming, compare the manifest except for `RepositoryId`.
- `Activity`: pins, writers, GC.

## Sessions, cleanup service, automatic rebalance

**`ScaleOut/RepositoryMoveRunner.cs`** (modes Manual | Rebalance | Evacuate | Auto)
- One task per chain, with progress through `Engine/TaskProgress` (from Part 1).
- Optional throttle (`MaxMoveMBps`).
- A deadline (the end of the window): the session then ends "paused, continues in the next window".

**`ScaleOut/ScaleOutBalancer.cs`** (BackgroundService, 1 min tick, `TimeProvider`)
- a health check per extent (Online/Offline saved);
- the cleanup service for Switched and Abandoned copies;
- queues evacuations again;
- in the window with `AutoRebalance`: when an Active extent is below `LowFreePercent`, or the spread of used % is above `ImbalancePercent`, it plans and queues "Automatic rebalance".

**`ScaleOut/RebalancePlanner.cs`** (a pure function)
- Greedy, from the fullest Active extent, to the target with the most free space after the planned moves.
- Skips chains moved within `CooldownDays`, running chains, and chains due within 1 h.
- Respects the `MaxBytesPerRun` budget.
- Counts *expected* free space, because pack files free space only after GC/repack. GC is queued per source after a batch.

## Converting and other catalog changes

**Convert or add an extent:** `POST /api/repositories/{id}/extents {Path, Name, ScaleOutName?}`.
- Refused while a backup of an affected job runs or a move runs.
- The same chunk size is required; a directory on the same volume is refused (setting).
- One transaction:
  - insert the scale-out;
  - the old row gets `ScaleOutId` and keeps its name (Prometheus history);
  - insert the new extent;
  - `jobs.repository_id` → the scale-out (row version guards concurrent edits);
  - placements from existing points and SQL backups.
- An existing directory as extent is imported afterwards.
- Adoption fallback: no placement means the newest point of (job, object) in the scope.

**Guards and fixes**
- **Repository delete** (`Api/InfrastructureEndpoints.cs:447-473`): today it would delete an extent's catalog rows. An extent may only be removed when it has no rows, journal items or placements, no move runs, and it is not the last extent. A scale-out only without extents and jobs.
- **Rescan/import** (`:524-568`): known ids are compared across *all* repositories. A copy of a moved point is reported, never inserted.
- **Job validation** (`OperationEndpoints.cs:598`): extents are refused. When a job changes repository, its placements are deleted.
- **Tape:**
  - the restore target is standalone or scale-out (`TapeCatalogEndpoints.cs:86`, `TapeRestoreRunner.cs:103` via `ForImportAsync`);
  - SQL Server matching goes through `RepositoryGroups` (`TapeBackupRunner.cs:286,294`, `TapeRestoreRunner.cs:84`).
- **Backups filter** (`BackupVms.cs:197`): through `RepositoryGroups`.
- **GC, packs, rescan on a scale-out:** one session per extent.

## API and console

**API:** `Api/ScaleOutEndpoints.cs`, DTOs in `Contracts/ScaleOut.cs`.

| Endpoint | Does |
|---|---|
| `POST /{id}/extents` | convert, or add an extent |
| `PUT /{extentId}/extent {Sealed?, Maintenance?}` | maintenance answers 202 "entering maintenance, waiting for …" |
| `POST`/`DELETE /{extentId}/evacuate` | start or cancel an evacuation |
| `GET /{scaleOutId}/chains` | filtered, paged |
| `POST /{scaleOutId}/rebalance/plan` | moves, and each extent before and after |
| `POST /{scaleOutId}/moves` | run a manual move or a rebalance |
| `PUT /{scaleOutId}/settings` | the scale-out settings |

`RepositoryDto` gains `ScaleOutId`, `ExtentState`, `Maintenance`, `Extents`, `Settings` and `Chains` (defaults kept).

**Console**
- **`Pages/Repositories.razor`:** scale-out rows expand into their extents (state, free/total bar, chains, stored bytes). Menus: add extent/convert, seal, maintenance, evacuate, remove, GC, rescan, settings.
- **New `Pages/ScaleOut.razor`:** tabs like `Tape.razor`, refreshed every 5 s: Extents | Chains ("Move to…") | Moves | Settings.
- **Dialogs:** add extent, rebalance (the plan with GB per move and before/after bars; "Run now" / "In window"), move chain.
- **`Pages/JobEditor.razor`** (`:686`, `:791`) lists standalone and scale-out repositories, not extents. Also the selects on Backups and Tape, and the Dashboard.

## Metrics, alerts, docs

**Metrics**
- `StateMetrics` skips scale-out rows and unavailable extents when opening repositories.
- `bdfu_repository_*` gains the label `scale_out`.
- New: `bdfu_scaleout_size_bytes` / `free_bytes` (Active extents), `bdfu_repository_extent_state{state}`, `bdfu_repository_chains`, `bdfu_repository_move_bytes_total{source,target}`, `…_move_remaining_bytes`, `…_pending_source_deletes`.

**Alerts** (`deploy/monitoring/backdafuckup.rules.yml`, `prometheusrule.yaml`)
- `BackdafuckupRepositoryLow` ignores sealed and evacuating extents.
- New: `BackdafuckupScaleOutNoRoom`, `BackdafuckupExtentOffline`, `BackdafuckupEvacuationStuck` (6 h).
- A Grafana panel.

**Docs**
- `docs/admin-guide.md` "### Scale-out repositories":
  - placement, the state table, moves, rebalance, evacuation, maintenance, removing an extent;
  - after a lost database: import each extent's directory, then add it as an extent;
  - limits: dedup per extent, equal chunk size, separate volumes.
- `docs/lab-runbook.md`: rows S1–S12.
- `agent-os/specs/2026-11-17-HHMM-scale-out-repositories/`: plan, shape, standards, references. References cite only public Veeam scale-out and PBS docs. Also the roadmap entry.

## Tasks (in order; each with its tests, the full suite green, and a commit)

1. Spec folder and roadmap.
2. **Storage additions:** `TryReuseChunk`, `TryDelete*`, `Contains*`, `Activity`.
3. **Shared pieces:**
   - `FifoLease` and `TaskProgress` moved to `Engine/`;
   - `ChainLocks`, `IVolumeSpace`, `RepositoryGroups`;
   - `RestorePointAccess` (Rule A) and `CatalogDeletes` (Rule B).
4. **Readers on Rule A; retention and delete on Rule B, offline-tolerant.** This includes the AD explore fix and the tape job pin; it is useful on its own and ships first.
5. Data model and migration.
6. **`RepositoryManager`:** maintenance gate, refuses scale-out rows.
7. **`RepositoryPlacement`** and its hooks; the SQL Server receiver bug.
8. **Conversion and add extent;** delete guards, global rescan, job validation, tape target and matching.
9. **`ChainMover`** with test hooks `BeforeSwitch` / `AfterSwitch`.
10. **`RepositoryMoveRunner`**, the session kind, dispatch, `StartupRecovery`.
11. **`RebalancePlanner` and `ScaleOutBalancer`:** health, cleanup, window, re-queueing.
12. API and DTOs.
13. **Console:** pages, dialogs; `JobEditor`, Backups, Tape, Dashboard.
14. Metrics, alerts, dashboard.
15. Admin guide and runbook.

## Verification

**Storage tests** (`tests/Backdafuckup.Storage.Tests/RepositoryCopyTests.cs`)
- A chain (full, two CBT incrementals, a file tree, a memory image) is copied with the same ids: byte for byte, only missing chunks read, loose↔pack.
- `TryReuseChunk` against a GC that starts at the same time (`FakeTimeProvider`).
- `TryDelete` → Busy and Pinned.
- Resuming over an identical target copy.

**Unit tests**
- The planner: triggers, budget, cooldown, no ping-pong.
- The chooser with reservations.
- The window across midnight and DST.
- `FifoLease`.

**Integration tests** (`ApiIntegrationTests.ScaleOut.cs`, `EngineIntegrationTests.ScaleOut.cs`; temporary directories as extents, `IVolumeSpace` faked, the same-volume check off)
1. Convert: the next run is incremental on extent 1, restore works.
2. Spreading by free space with reservations.
3. Sealed → a full on extent 2, a warning, retention across extents, GC per extent.
4. Maintenance and offline, with the setting on and off.
5. Manual move: ids kept, `sql_backup_sets` updated, next run CBT on the target, the tape job copies nothing again.
6. A backup held at `FakeXenPool.OnSnapshot` during a move: "waiting", then catch-up and switch.
7. Retention at the `BeforeSwitch` hook: the copy is abandoned and deleted.
8. An open file browser keeps the source copy until it closes.
9. Crash before and after the switch, and the host restarted.
10. Evacuation with a tape-imported point, a SQL-only VM and a Missing row; then the remove guard, then success.
11. Maintenance on/off waits for activity.
12. Delete guards and job validation.
13. Rescan with a duplicate id.
14. A whole-cluster job over extents; cluster restore.
15. A memory point moved, then resumed.
16. A SQL Server straggler; point-in-time restore over extents.
17. Tape restore into a scale-out.
18. Automatic rebalance in and out of the window; pausing at the deadline.
19. The metrics are present.

**Live**
- **Throwaway server:** a throwaway server with its own empty database and two volumes (as in T13 on the VTL VM, or a lab VM): convert, spread, move, evacuate, maintenance, rebalance with real XCP-ng backups of lab VMs.
- **backup01:** only when the user asks, and with a second volume there (a system change, so asked first): convert its repository and add the volume as extent 2.
- **Rules:** no restarts at :00/:15/:30/:45, sessions checked first, nothing deleted in XCP-ng.
