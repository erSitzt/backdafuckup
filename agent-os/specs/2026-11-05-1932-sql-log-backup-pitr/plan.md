# Plan: SQL Server log backups and point-in-time restore

Spec folder: `agent-os/specs/2026-11-05-1932-sql-log-backup-pitr/`

## Context

SQL phase 1 is done: publish, restore, overwrite and export, all to the moment of a restore point. The user's report requires more:
- point-in-time recovery per database (A1, must);
- an RPO of 15 min or less, with 5 min for a critical class (A4);
- exactly one tool owns the log chain, and deviations raise an alarm (A7);
- daily full backups of the system databases (A11).

The user's site sometimes runs hourly log backups.

**What exists:**
- **Image full:** since `5de3e8e`, the SQL Server snapshot (2022+) is a full backup by default. It starts the log chain, and its `.bkm` bytes and LSNs are stored in the restore point's `sql-snapshot` blob. No production code reads them back yet.
- **Restore path:** the `.bkm` (`METADATA_ONLY, NORECOVERY`) → `RESTORE LOG` → `STOPAT` path is proven in a container test (`tests/Backdafuckup.Sql.Tests/SqlSnapshotTests.cs:103`).

**What is missing:**
- log backups;
- a native full for SQL Server before 2022 and for VSS-mode jobs;
- a place in the repository for backup files that are not disk images;
- a catalog of backup sets;
- a restore planner and the restore itself.

**User decisions (2026-11-05):**
- **The server drives the backups.** Each interval it sends the agent a job that carries the account from the backup job. No password is stored in the VM. Files whose upload fails stay in a queue in the agent. While the server is down, log backups pause; the chain stays intact, and an RPO warning follows.
- **Log interval:** default 60 min; presets 5/15/30/60; longer intervals free.
- **Native full backups** for SQL Server before 2022 are in v1. Their schedule is the user's choice (daily, or weekly with days and a time), as with other products.
- **System databases** (`master`, `msdb`, `model`) get a daily full by default, on a schedule the user can change.
- **Chain break or a new database without a base:** a native full runs at once, plus a warning.
- **Sessions:** one session per job and day.
- **UI:** a new "SQL Server" page listing the databases, and a page per database with a timeline. The base for a restore is chosen by the server.

## Approach

### Backup kinds (all run by the agent with T-SQL, uploaded into the repository)

| Kind | Statement | When | Role |
|---|---|---|---|
| Image full (exists) | `SUSPEND_FOR_SNAPSHOT_BACKUP` + `.bkm` | image job | chain base for 2022+ in SqlSnapshot mode |
| Log | `BACKUP LOG … WITH CHECKSUM[, COMPRESSION]` | every N minutes | continues the chain, truncates the log |
| Native full | `BACKUP DATABASE … WITH CHECKSUM[, COMPRESSION]`, then `RESTORE VERIFYONLY` | user schedule; at once on chain break or for a new database | chain base when there is no image full; optional for all databases |
| System full | as native full, for `master`, `msdb`, `model` | user schedule (default daily) | download as `.bak`, or write into a VM folder |

- **Compression:** `COMPRESSION` is used except on Express (`EngineEdition = 4`); the repository's zstd compresses Express backups instead.
- **Backup set fields:** NAME `backdafuckup <kind>` and DESCRIPTION `job=<id>;session=<id>`.

### Scheduling, sessions, agent lane

**Job settings.** A new owned type `SqlBackupSettings` on `BackupJob` (`Data/Entities.cs:274`):
- Enabled;
- LogIntervalMinutes (presets 5/15/30/60, free from 60 up to 1440);
- FullSchedule and SystemSchedule (`JobSchedule`, reusing `ScheduleCalculator.Next`);
- FullForAllDatabases (default off);
- FullOnChainBreak (default on);
- ExcludedDatabases;
- StagingFolder (default: the instance's backup folder).

It uses the job's `SqlCredentialId`, which must now also be set in VSS mode once SQL backups are on.

**State.** Table `sql_backup_schedules` (job, VM): next log/full/system run, last run and last result.

**`SqlBackupScheduler`.** A new `BackgroundService` beside `JobScheduler` (`Engine/EngineServices.cs:191`) with a 30 s tick:
- Runs are aligned to interval boundaries in the job's time zone (e.g. :00/:15/:30/:45).
- One run per VM, at most 8 in parallel.
- It skips a VM whose image backup is between prepare and thaw (the registry flag is set by `AgentQuiescer`), and logs the skip.
- A run that is due while the previous one is still running is skipped with a line in the log.

**Day session.** `SessionKind.SqlBackup`, named "SQL Server backups – <job> – <date>", with one task per VM:
- The session is Running while a run is in progress. Afterwards it shows the worst result of the day.
- The next run of the same day reopens it.
- It does not go through `SessionQueue`/`HasCapacity`, so image backups and maintenance do not block it.
- `StartupRecovery` (`EngineServices.cs:263`) sets a day session that was left Running to its aggregated state with "server restarted during a run", not Failed.
- It gets a filter entry on the Sessions page.

**Agent lane.** New SQL job kinds `log-backup` and `full-backup`, behind capability `sql-backup-v2`. SQL jobs already run outside the work slot (`AgentRunner.cs:387`). Changes:
- **`Busy` (`AgentRunner.cs:62`):** backup-lane jobs no longer count, so they do not hold back restores or publishes (`TryReserveWork`).
- **Self-update:** it still waits for them (server and agent).
- **Per-instance lock in the agent:** shared by log/full jobs and `snapshot-suspend`. A suspend waits for a running log backup; spike 2d decides what happens with a native full that is still running.

### Agent backup job (per instance)

1. **Queue first.** Files left over from earlier runs are uploaded first. The queue is `sql-backups\queue.json` in the agent folder, written durably: a temp file, flush, then `File.Replace`. The existing journals write non-durably (noted in `SqlReplace.cs:480`).
2. **Discover.** `sys.databases` + `sys.database_recovery_status`: state, read-only, recovery model, `last_log_backup_lsn`, `database_guid`, `recovery_fork_guid`, and `family_guid` from msdb.
3. **Chain watch input.** msdb `backupset` + `backupmediafamily` rows since the last run that are not ours, i.e. foreign full, log or `NUL` backups. Copy-only ones are noted, not counted as a break.
4. **Per database:**
   - FULL/BULK_LOGGED with a chain: log backup.
   - FULL/BULK_LOGGED without a chain (pseudo-simple, new database, after a SIMPLE switch): native full if FullOnChainBreak, otherwise a warning.
   - SIMPLE: noted (no point-in-time).
   - Skipped: system databases (except in a system job), snapshots, read-only, offline or restoring databases, AG replicas.
5. **Recording.** The queue entry `{db, path, started}` is written *before* each BACKUP. Afterwards the agent reads the backup set: `backup_set_uuid`, first/last/checkpoint/database_backup LSN, `family_guid`, `recovery_fork_guid`, start/finish, `time_zone`, size, copy-only and position.
   - After an agent restart, a pending entry is resolved through msdb by the physical device path: if found, it is queued for upload; if not, the partial file is deleted. An interrupted backup is never reported as successful.
6. **Upload.** `PUT api/agent/sql-jobs/{id}/files/{n}` streams the file with SHA-256 and the facts. The server chunks it straight into the repository (see below) and acknowledges.
   - The agent deletes the file only after the server has committed it.
   - `backup_set_uuid` is unique in the catalog, so a re-upload after a lost answer is acknowledged as "known".
7. **Prechecks.** Free space in the staging folder (estimated from `sys.dm_db_file_space_usage` for fulls) and rights: sysadmin, or `db_backupoperator` in every database plus msdb read. Each error names the host, IP, instance, path and needed/free bytes.

### Repository: SQL backup objects

- **New object type.** A SQL backup object is one run of one VM and instance, of one kind:
  - layout: `sql-backups/<vm>/<yyyyMMddTHHmmssZ>_<kind>_<id>/manifest.json`, plus one chunk index per file;
  - manifest: format, repository, VM source, instance, product version, kind, and per file the database, guids, LSNs, times, size, SHA-256 and index.
- **API on `IBackupRepository`** (`Core/Repository/IBackupRepository.cs`): `BeginSqlBackup` (a streaming writer: `Stream` → fixed `ChunkSize` chunks through the existing `PutChunkAsync` path, so it is deduplicated and zstd-compressed), `ListSqlBackupsAsync`, `OpenSqlBackupAsync`, `PinSqlBackup`, `DeleteSqlBackupAsync`.
- **Reads and verify:** `Verify` covers these objects; a file is read by its index, and a short last chunk is allowed.
- **GC:** `CollectGarbageAsync` (`Storage/LocalDirectoryRepository.cs:232`) also marks chunks referenced by SQL backup manifests.
- **Import:** `ImportRestorePointsAsync` (`Api/InfrastructureEndpoints.cs:253`) also imports SQL backup manifests into the catalog, so a catalog rebuild keeps point-in-time restore.

### Catalog and chain

**Tables:**
- `sql_databases`: VM, instance, name, `family_guid` (stable identity; `database_guid` changes on restore), recovery model, chain state (ok / no base / broken / simple / foreign tool) and reason, last log, last full, job.
- `sql_backups`:
  - links: database, repository object and file index, or `restore_point_id` for image fulls; kind;
  - unique `backup_set_uuid`;
  - LSNs as `numeric(25)`; `recovery_fork_guid`, `position`;
  - start/finish in UTC (converted with `time_zone`) plus the original offset;
  - size, SHA-256, copy-only, product version.

**Image fulls:**
- When an image backup commits, its `sql-snapshot` facts become `sql_backups` rows.
- `BackupSetQuery` (`Agent.Core/SqlSnapshot.cs:311`) gains `backup_set_uuid`, guids, dates and `time_zone`.
- A backfill reads older points' blobs; those rows lack guids and are matched by name and LSN.

**Chain model** (`Sql/SqlChain.cs`, pure and unit-tested):
- Bases are image fulls (point Available, SQL 2022+) and native fulls.
- The first log after a base contains the base's LSN; spike 2b confirms the exact rule for snapshot fulls.
- Logs must be contiguous: `first_lsn(n) = last_lsn(n−1)`, with the same fork.
- Coverage is a list of intervals; gaps carry their cause.
- `Plan(database, time | latest)` → base + logs + STOPAT, in the source's local time. Otherwise it returns an error that names the gap and the nearest points it can restore to.

**Chain watch (server, after each run):**
- **Warnings for:**
  - a gap (`first_lsn` ≠ the previous `last_lsn`);
  - foreign log backups (named with device and time);
  - a switch to SIMPLE;
  - a fork change;
  - a last log older than 2× the interval (RPO);
  - an agent offline at a due run.
- **Native full on the next run** (as decided) after a break or for a new database.
- **Where warnings show:** in the day session (Warning) and in the database list. There is no mail or webhook yet.

### Retention

The point-in-time window of a VM equals its restore-point window. Let W be the time of the oldest Available restore point that the job's retention keeps (`BackupSessionRunner.ApplyRetentionAsync:103`).
- **Native fulls:** kept if they are newer than W, plus the newest one before W.
- **Logs:** kept if they are newer than the oldest kept base.
- **System fulls:** kept back to W.

Retention runs after image retention and once a day in the scheduler. Restores pin the objects they use. Deleted objects go through GC as before.

### Point-in-time restore

**Planner input.** It takes the database (`sql_databases` id), the time or "latest", and the target, then reuses the SQL restore and publish sessions. `SqlRestoreRequestDto`/`SqlPublishRequestDto` (`Contracts/Sql.cs:125`) gain `PointInTime` and `Latest`, and the plan is stored in the session parameters.

**Agent job `restore-chain`** (capability `sql-restore-chain-v1`):
1. Get the base:
   - image full: the `.bkm` bytes inline from the `sql-snapshot` blob, with its position;
   - native full: the `.bak`, downloaded through `GET api/agent/sql-jobs/{id}/files/{n}` (job-bound, SHA-256 checked) into a staging folder the service can read (with an ACE for the service SID).
2. Map the files: `RESTORE FILELISTONLY … WITH FILE = n` gives the logical names. Logical names are not stored today, and the test only works because it uses the default names. Each name is mapped from its original physical path to the target path the server gives.
3. `RESTORE DATABASE … [METADATA_ONLY, FILE = n,] MOVE …, NORECOVERY, CHECKSUM`.
4. Each log: download, then `RESTORE LOG … NORECOVERY, CHECKSUM`, then delete; the next log is prefetched during the restore. The last log gets `STOPAT = …, RECOVERY`; with "latest", it gets `RECOVERY`.
5. Grant ACEs, then `CheckAccess` and `Describe` as after an attach (`SqlAttach.cs:97,124`).
6. On failure: `DROP` the database left RESTORING, delete the files it created, and delete the staging folder. A journal entry plus the server-side lost-agent handling (pattern `SqlRestoreSessionRunner.AttachLostAsync:411`) cover agent restarts.

**Targets:**
- **New database:**
  - Image base: the data files are transferred first (existing `AgentTransfer` with `IfExists=Fail`, `SqlRestoreSessionRunner.cs:158`); `METADATA_ONLY` then uses them in place.
  - Native base: the RESTORE creates the files.
- **Publish (image base only):** in `PublishRunner` (`Agent.Core/Publishing.cs:779`) the attach becomes the chain restore on the mounted files. Log replay writes into the overlay, and the logs are staged in the publish folder on the VM's own disk. Unpublish is unchanged. For a native base, the UI says "restore as a new database".
- **Overwrite:**
  1. Restore under a staging name into `<original path>.bdfu-new` with RECOVERY, then detach.
  2. Optionally, by default on: a final log backup of the original in `SINGLE_USER WITH ROLLBACK IMMEDIATE`, so every transaction up to the swap stays in the chain.
  3. The existing swap with rollback (`SqlReplace.cs:37`).

  Spike 2c checks that SQL Server brings an offline original online with restored files. If not, the swap ends with DROP + ATTACH and restores the owner; the rollback stays the same. The chain is broken afterwards, so a native full follows.

**Downloads.** The `.bak` of a native or system full is served straight from the repository through a one-time link (pattern `SqlExportFiles` tickets). "Write into a VM folder" uses the same download endpoint in a `fetch-files` job.

### Console

- **`Pages/SqlDatabases.razor` (`/sql`, new menu entry "SQL Server").** A table per VM and instance: database, recovery model, chain state chip with reason, last log, last full, covered window, job.
- **`Pages/SqlDatabase.razor` (`/sql/db/{id}`):**
  - a timeline of coverage intervals and gaps for the last N days, with markers for bases;
  - a date and time picker (seconds) or "latest";
  - the plan preview: base, number of logs, bytes, STOPAT;
  - actions Publish (image base only), Restore (new or overwrite) and Download `.bak` (native or system fulls).
- **Dialog:** the restore and publish dialog is moved out of `SqlBrowser.razor:164-355` into a component, which both pages use.
- **JobEditor** (`JobEditor.razor:92-148`): a panel "SQL Server backups in the VM" with the log interval (presets + free value), the full and system schedules (the same controls as the Schedule step), a chain-break full checkbox, exclusions and the staging folder.
- **Session pages:** `SessionDetail.razor` gets tiles for SqlBackup (log backups, bytes, RPO) and lists the VM tasks. The Sessions filter adds SqlBackup, plus the missing SqlExport.

## Tasks (vertical slices; the suite stays green after each)

1. **Save the spec documentation:** `plan.md`, `shape.md` (decisions above, report requirements A1/A4/A7/A11), `standards.md` (the SQL safety standards of the SQL restore spec: parameters/`QUOTENAME`, untrusted names; secrets only in memory), `references.md`, `visuals/` (none).
2. **Container spikes** (`spike.md`, `BDFU_SQL_TESTS=1`, the 2022 image plus a 2019 image, run serially because of memory):
   - **2a:** `RESTORE FILELISTONLY`/`HEADERONLY` on a `.bkm`.
   - **2b:** the LSN rule for the first log after a snapshot full and after a native full.
   - **2c:** swap of restored files under an offline original, then `SET ONLINE` (error 5173 or not).
   - **2d:** `BACKUP LOG` and `BACKUP DATABASE` concurrent with `SUSPEND_FOR_SNAPSHOT_BACKUP`.
   - **2e:** STOPAT with the container in `TZ=Europe/Berlin` and the `time_zone` conversion.
   - **2f:** backup set facts on 2019.
3. **Repository SQL backup objects:** streaming writer, list/open/pin/delete, GC marks, verify, import. Storage unit tests.
4. **Catalog and chain:**
   - tables and migration;
   - the new image-full facts (agent `BackupSetQuery` + server on commit) and the backfill;
   - `SqlChain` coverage and planner with unit tests: gaps, forks, foreign backups, STOPAT conversion.
5. **Agent backup lane:**
   - `log-backup`/`full-backup` jobs, the durable queue, recovery of interrupted backups, the upload, the instance lock, `Busy` separation, the capability.
   - Tests: agent unit tests and container tests (log, full, system full, restart in the middle).
6. **Server backups:**
   - job settings (entity, migration, DTOs, `ApiMapping`, validation);
   - `SqlBackupScheduler`, the day sessions, the upload endpoint into the repository, the chain watch, the automatic full, retention, update gating, `StartupRecovery`.
   - API tests with the real `AgentRunner` and `ScriptedSql` (new statement texts).
7. **Point-in-time restore as a new database and overwrite:**
   - the `restore-chain` job, the download endpoint, the planner in `SqlRestoreSessionRunner`, failure and restart cleanup;
   - the final log backup and the swap (per spike 2c).
   - End-to-end container tests for an image base and a native base.
8. **Point-in-time publish:** the chain restore in `PublishRunner`; API test with `IscsiGuestHost`.
9. **Console:**
   - the SQL Server pages, timeline and plan preview, the extracted dialog, the JobEditor panel, the session pages, the downloads;
   - the fetch-files job for system database backups.
10. **Docs:**
    - **Admin guide:** log backups, settings, rights, staging space, turning off other log tools (Ola, maintenance plans, Veeam truncation), time zones, retention, limits.
    - **Runbook section G** (checks G1–G14).
    - **Roadmap** (done/next: differential backups, automatic restore tests A10, alerts, AG).
11. **Lab:**
    - **Hourly and 5-minute logs on bdfu-lab-sql1/2 (2025 Express):**
      - point-in-time restore to all three targets with row checks;
      - a foreign `BACKUP LOG` and a SIMPLE switch → warning + full;
      - server service stopped for 30 min → pause, RPO warning, the chain continues;
      - agent killed during a log backup → nothing reported falsely, and no gap.
    - **Older version:** a lab VM with SQL 2019 Express for the native base, created by the user with OpenTofu.

## Verification

- **Unit tests:** chain and planner, scheduler alignment and DST, retention window, durable queue, repository object round trip, GC.
- **API tests** with the real `AgentRunner` + `ScriptedSql`:
  - scheduled log runs into a day session;
  - a lost upload answer leads to a re-upload acknowledged as "known";
  - an agent restart in the middle of a backup;
  - skip during freeze;
  - chain break → full;
  - point-in-time restore jobs and cleanup;
  - publish at a point in time.
- **Container tests** (`Category=Sql`, `BDFU_SQL_TESTS=1`), run explicitly after each SQL task:
  - log/full jobs against real SQL Server;
  - end-to-end point-in-time restore (image base and native base) with row counts at STOPAT;
  - overwrite with the final log backup;
  - a 2019 run for the native base.
- **Full suite:** green without the variable.
- **Lab:** runbook section G on the lab VMs; the user runs the checks that need the UI.

## Risks

1. **The overwrite swap with restored files may not come online.** Spike 2c checks it; the fallback is DROP + ATTACH.
2. **Log backups and the image suspend may conflict.** Mitigated by the instance lock and the skip during freeze; spike 2d checks it.
3. **Large native fulls need staging space in the VM, and an upload restarts from zero** (no resume in v1). The precheck and errors give exact numbers; resume and VDI streaming come later.
4. **Many small objects.** At 5 min there are 288 objects per VM per day. This is acceptable now; the planned pack files in the repository backlog solve it.
5. **Time zones and DST at STOPAT.** All times are stored in UTC with the original offset (spike 2e).
6. **Foreign tools keep breaking the chain.** The chain watch reports them by name, the automatic full restores coverage, and the docs say what to switch off.

## Not in this spec

Differential backups, automatic restore tests (A10), mail/webhook alerts, immutability and encryption at rest (A8/A9), AG/FCI (A13), VDI streaming, resumable uploads, `msdb` history cleanup.
