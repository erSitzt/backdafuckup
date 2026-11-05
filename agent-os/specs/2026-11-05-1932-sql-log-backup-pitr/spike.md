# Spikes (task 2) — 2026-11-05

**Test file:** `tests/Backdafuckup.Sql.Tests/PointInTimeSpikes.cs`. It runs only with `BDFU_SQL_TESTS=1 BDFU_SQL_SPIKES=1` and writes what SQL Server does to `BDFU_SPIKE_OUT`.

**Containers:**
- SQL Server 2022 CU27 (UTC), the shared fixture;
- 2022 with `TZ=Europe/Berlin`;
- 2019 CU32 GDR12 (`15.0.4500.2`).

## 2a — `.bkm`: file list and header

- **`RESTORE FILELISTONLY FROM DISK = <bkm> WITH FILE = <position>`** works on the snapshot's `.bkm`. It returns `LogicalName`, the original `PhysicalName`, `Type` (D/L), `FileGroupName` and `FileId`. The test database used its own logical names (`spk_main`, `spk_second` in its own filegroup, `spk_journal`).
  - **Decision:** the restore job reads the logical names this way. Today's container test only works because it uses the default names `<db>` and `<db>_log`.
- **`RESTORE HEADERONLY`** on the `.bkm` lists one backup set per database:
  - Position, `BackupType = 1`, `IsSnapshot = 1`, `IsCopyOnly`;
  - First, Last, Checkpoint and DatabaseBackup LSN;
  - `BackupSetGUID`, `FamilyGUID`, `RecoveryForkID`, start/finish.

## 2b — LSN rules

**Snapshot full (2022):**
- The full: `first = checkpoint = …27200212`, `last = …38400001`.
- The first log afterwards has `first_lsn = …27200212` and `last_lsn = …42400001`, so it contains the full's `last_lsn`.
- Skipping it fails with 4305 ("too recent … an earlier log backup that includes LSN <full.last_lsn>").

**Native full taken later:**
- The full: `first = …52800001`, `last = …55200001`.
- The log taken before it fails with 4326 ("too early").
- The next log (`…42400001` → `…59200001`) applies.

Both bases restore to the second: 6 of 7 rows at STOPAT.

**Rules for `SqlChain`:**
- The first log after a base B is the one with `log.first_lsn ≤ B.last_lsn < log.last_lsn`.
- After that, `first_lsn(n) = last_lsn(n−1)`.
- Each log's `first_recovery_fork_guid` must equal the previous log's `last_recovery_fork_guid`, and `fork_point_lsn` must be NULL. Otherwise it is a fork, i.e. a break.

**Identity:**
- `family_guid` stays the same for every copy restored from the database, under any name. Here `spk`, `spk_r1` and `spk_r2` all share it.
- `database_guid` and the recovery fork change with every restore.
- **Decision:** a catalog database is identified by VM, instance, name and `family_guid`. A new `database_guid` or fork under the same key is a break.

**After a restore with RECOVERY**, the copy takes log backups at once. They run on the new fork (`last_recovery_fork_guid` new, `fork_point_lsn` set). So the chain watch has to check the fork, not only the LSNs.

**msdb column names:** `last_recovery_fork_guid` and `first_recovery_fork_guid`. There is no `recovery_fork_guid` column. `sys.database_recovery_status` does have `recovery_fork_guid`.

## 2c — Overwrite: restored files under an offline original

The sequence:
1. Restore under a staging name into `<original>.bdfu-new`, recover, and `sp_detach_db`.
2. On one connection: `SET SINGLE_USER WITH ROLLBACK IMMEDIATE`, then `BACKUP LOG` (the final log backup), then `SET OFFLINE`.
3. Rename the files, then `SET ONLINE`.

What happened:
- `SET ONLINE` **works**.
- The restored state was there (4 rows), `DBCC CHECKDB` was clean, and the owner was kept (`0x01`).
- The database came back with a new `database_guid` and a new fork.
- `user_access` stays `SINGLE_USER` after the swap (it lives in master), so it has to be put back to the original value.
- A log backup right afterwards works. It is on the new fork (`fork_point_lsn` set), and the chain watch sees it as a break, so a native full follows.
- The final log backup is on the old chain, so restoring to just before the overwrite stays possible.

**Decision:** point-in-time overwrite uses the existing swap with rollback (`SqlReplace`). After it, `user_access` is set back to the original value.

## 2d — Backups against the snapshot suspend

| Case | Result |
|---|---|
| A native full is running, then `SUSPEND_FOR_SNAPSHOT_BACKUP = ON` | fails at once with error 3081 (misleading text "was previously suspended for snapshot backup") |
| Suspended, then `BACKUP LOG` | waits (still waiting at the 10 s timeout) |
| Suspended, then a native full (also copy-only) | waits |
| A short log backup has just finished, then the suspend | works (3.2 s) |

**Decision:**
- **Instance lock in the agent:** backup-lane jobs and `snapshot-suspend` share a per-instance lock.
- **Suspend waits for running backups.** It waits for a running log or full backup, for up to `SqlBackup:FreezeWaitMinutes` (default 30); the session log says what it waits for. When the wait runs out, the snapshot goes on, and the affected databases are reported with error 3081 explained ("a native full backup of [db] was running").
- **Log and full jobs never start during a freeze.** The scheduler skips the VM during the freeze window, and the lock covers the rest.
- **VSS mode (before 2022):** the same lock, since a VSS snapshot of the SQL writer is also a backup that SQL Server serializes. This is checked in the lab.

## 2e — Time zones

Container with `TZ=Europe/Berlin`:
- `SYSDATETIMEOFFSET()` was `+01:00`, and `CURRENT_TIMEZONE()` returned "(UTC+01:00) Amsterdam, Berlin, …".
- `backupset.backup_start_date`/`backup_finish_date` are local time, with `time_zone = 4` (in 15-minute steps from UTC).
- `RESTORE HEADERONLY` also shows local times.
- **STOPAT is local time:** the local point restored 2 of 3 rows; the same moment in UTC failed with 4335 ("STOPAT time is too early").

**Decision:**
- **Catalog:** UTC = local − `time_zone` × 15 min, and the offset is stored too.
- **STOPAT** = target UTC + the offset of the log backup that contains the target.
- `time_zone = 127` (unknown) is an error for the time conversion; that database restores only to "latest" or to backup ends.

## 2f — SQL Server 2019

- `ALTER DATABASE … SET SUSPEND_FOR_SNAPSHOT_BACKUP` fails with error 102 (syntax), as expected.
- A log backup without a base fails with 4214.
- A native full with `CHECKSUM, COMPRESSION, NAME, DESCRIPTION` and `RESTORE VERIFYONLY` work (Developer edition).
- The `backupset` columns are the same as on 2022.
- `FILELISTONLY` and the chain full → log → log with STOPAT restore correctly: 4 rows.
