# Plan: Application-Consistent Snapshots on Slow Storage

See `shape.md` for the measurements, how XO, Veeam and Hyper-V do it, and what the user's report contributes.

## Step 1 — shorter snapshots and safer storage use (done 2026-11-04)

- **One snapshot per storage** (`Xen:OneSnapshotPerStorage`, default on):
  - The gate is server-wide, keyed by SR UUID (`XenSnapshotGate`). It is taken in a fixed order and entered before the guest is frozen.
  - The wait is logged.
- **`[NOBAK]` disks:**
  - `VM.snapshot` with `ignore_vdis`. No CBT is enabled on these disks.
  - The manifest lists them in `ExcludedDisks`. A VM restore creates them empty (`IVmRestoreSession.CreateEmptyDiskAsync`) and maps them in `import_metadata`.
  - Pools without `ignore_vdis` (`MESSAGE_PARAMETER_COUNT_MISMATCH`) back them up with a warning.
- **Free space:** every storage of the VM needs at least `Xen:MinStorageFreePercent` (default 5 %), checked after the gate and before the freeze. Otherwise the VM fails with `XenStorageSpaceException`.
- **Measurement:** "Snapshot … created in 8.1 s: 3 disk(s) on 'Alletra' (12.3 % free)." (it replaces the "Creating snapshot" line, which has the same log key).
- **Hardening:** `ReleaseChangeTokensAsync` deletes a disk only if it is a snapshot or `cbt_metadata` disk with the `backdafuckup.session` tag.

**Measured after the deployment (2026-11-04 20:56, lab job, storage `alletra-mp02`):**
- sql2 (3 disks) took 24.7 s.
- sql1 (4 disks) waited 28.6 s for sql2, then took 46.9 s.
- That is about 8–12 s per disk, even alone. Both are crash-consistent (`VSS_E_HOLD_WRITES_TIMEOUT`), as expected: shortening alone does not bring multi-disk VMs on this storage under 10 s.

Steps 2 and 3 are needed.

## Step 2 — T-SQL snapshot backup for SQL Server 2022+ (approach A)

**Flow, per SQL Server 2022+ instance in the VM:**
1. The agent opens a connection and runs `ALTER SERVER CONFIGURATION SET SUSPEND_FOR_SNAPSHOT_BACKUP = ON (MODE = COPY_ONLY)`. With more than 64 user databases (the documented maximum), it uses `GROUP = (…)` in batches, or falls back (see Risks).
   - SQL Server pauses the writes of the user databases; reads continue. The rest of the guest keeps running: the file systems are crash-consistent (journaled), and the SQL files are consistent.
2. The agent reports "suspended". The server takes `VM.snapshot` (through the gate). Any duration is fine, but SQL writes wait for that long, so the pause is logged as the main metric.
3. The agent runs `BACKUP SERVER TO DISK = '<agent folder>\bkm\<restore point>.bkm' WITH METADATA_ONLY, FORMAT, MEDIADESCRIPTION = N'backdafuckup restore point <id>'`.
   - This resumes the writes and records a COPY_ONLY FULL backup (`is_snapshot = 1`) in `msdb`.
   - The agent uploads the `.bkm` to the server. It becomes a manifest blob, `sql-snapshot-metadata`.
4. **Failure, timeout or a silent server:**
   - The agent runs `SET SUSPEND_FOR_SNAPSHOT_BACKUP = OFF`. The `MODE = COPY_ONLY` suspend keeps the differential bitmap intact.
   - The databases are then crash-consistent, with the reason.
   - Hard timeout `GuestProcessing:SqlSuspendTimeout`, default 120 s, enforced by the agent's own watchdog. The server's deadline is shorter, so the server never snapshots after the agent has thawed.
5. **Consistency:**
   - Per database "application" (guest metadata v2 already stores it per database).
   - The restore point says "application (SQL)" or "crash (system)": the VM image without VSS is crash-consistent for the rest of the guest.

**Job option *Guest processing mode* (built 2026-11-04 as "VSS for every VM" and "SQL Server snapshot where SQL Server 2022+ runs, VSS for all other VMs"):**
- The SQL Server snapshot falls back to VSS in the same backup when no database can be suspended: no SQL Server, one older than 2022, an agent without the capability, or missing rights (the last one with a warning).
- The original idea:
- **VSS:** as today.
- **SQL suspend:** SQL 2022+ only; other VSS writers are not involved.
- **Automatic:**
  - VSS when the last measured snapshot time of this VM (or disks × the storage's average time per disk) stays under 8 s;
  - otherwise SQL suspend where SQL 2022+ runs;
  - otherwise approach B (step 3);
  - otherwise crash-consistent with the reason.

**Restore:**
- Publish, restore and export keep using attach: the files are consistent, so no crash recovery is needed.
- New: `RESTORE DATABASE … FROM DISK = '<.bkm>' WITH METADATA_ONLY, DBNAME = …, MOVE …, NORECOVERY`. This leaves the database ready for log restores, which the PITR spec (step 4) builds on.

**Tests:**
- Agent.Core against the mssql container (Developer edition, 2022 CU): suspend, a write that blocks, `BACKUP … METADATA_ONLY`, the `msdb` rows, the watchdog thaw.
- Server: the orchestration with a fake agent, the `.bkm` blob in the manifest.

**Spike 2a: done, see `spike.md`.** Express supports it. Suspend per database (`GROUP` fails, and the server-wide suspend skips closed `AUTO_CLOSE` databases), on a dedicated unpooled connection (its end resumes SQL Server). The `.bkm` goes to a folder the SQL service account can write. The original checklist: on bdfu-lab-sql1 (SQL Server 2025 Express, 17.00.1000; the download link of the lab now delivers 2025), check:
- whether Express supports the T-SQL snapshot backup at all (Microsoft Learn says "SQL Server 2022 and later" and names no edition);
- the length of the write pause on Alletra;
- the `msdb` rows;
- `RESTORE FILELISTONLY … WITH METADATA_ONLY`;
- a `RESTORE … METADATA_ONLY, NORECOVERY` on sql2 from the published files.

## Step 3 — in-guest shadow copy with read-time revert (approach B)

**Backup:**
1. The agent creates a VSS snapshot set with the Windows system provider, not our hold provider. Windows itself creates the shadow copy in seconds, inside the 10 s. Writers freeze and thaw as usual; their results decide the consistency.
2. The server takes `VM.snapshot` afterwards, at any speed and without a freeze. The snapshot contains every volume plus its shadow copy (catalog and store in `System Volume Information`).
3. The agent ends the backup (`BackupComplete`, copy-only) and deletes the shadow copy. It only had to exist in the XAPI snapshot.

**Reading the backup:** for every NTFS volume with our shadow copy (identified by the shadow copy ID from the agent), the server reads the volume through the shadow copy:
- unchanged blocks come from the volume;
- blocks overwritten after the shadow copy come from the store.

The repository therefore stores the consistent state. Restore, FLR, publish and SQL attach work unchanged.

**CBT:** blocks the revert replaced (written between the shadow copy and the XAPI snapshot) are stored with the restore point. The next incremental adds them to its changed set, because the next CBT delta no longer contains them.

**The VSS store reader:** our own C# code, written from the format documentation, not from library code. The documentation is libyal's "Volume Shadow Snapshot (VSS) format". libvshadow (LGPL-3.0+, `vshadowinfo`/`vshadowmount`) serves only as a test oracle in the lab.

**Spike 3a (first):**
1. Create a shadow copy, write files, take an XAPI snapshot.
2. Read the snapshot export with `vshadowmount`.
3. Check that the shadow copy is complete and readable in the crash-consistent image. Compare a persistent and a non-persistent context: volsnap must have its metadata on disk at that moment.

**Tests:**
- Fixtures: NTFS images with shadow copies, made once in a Windows VM, stored in `tests/fixtures`.
- Reader against libvshadow output, then the CBT union, then end-to-end in the lab.

## Approach C — memory checkpoint / offline snapshot (option)

Job options like Xen Orchestra's:
- **"Snapshot with memory"** (`VM.checkpoint`): consistent only when restored with its RAM and resumed. The pause grows with the RAM size, and the snapshot needs RAM-sized space on the storage.
- **"Shut down for the snapshot":** the VM is offline for the duration of the snapshot.

The effort is small; build it on demand.

## Approach E — array snapshot (roadmap: Alletra storage snapshots)

With the storage-snapshot integration from the phase 1 roadmap:
1. Freeze with VSS.
2. Take the array snapshot of the volume set: one array call, well under 10 s.
3. Thaw.
4. Read from the array snapshot over the SAN.

**CBT boundaries:**
- This backup's changed set must reach at least the array snapshot time.
- The reference for the next backup must not be later than that time.
- So an XAPI snapshot before the freeze (the next reference) and one after (the end of the current changed set).

This belongs in the storage-snapshot spec.

## Step 4 — native SQL backups and point-in-time restore (own spec)

The report's layer 1, which is phase 2 of the roadmap:
- the agent runs FULL, DIFF and LOG backups;
- it owns the log chain and mirrors `msdb.backupset`;
- the restore planner works out the chain to `STOPAT`;
- foreign log backups are reported;
- restore tests run automatically.

The base is the `.bkm` from step 2 or a streamed FULL. Shape it with `/shape-spec` once step 2 works.

**Known before shaping (2026-11-05):**
- The restore path is public: mount or copy the snapshot's files, then `RESTORE DATABASE … FROM DISK = '<.bkm>' WITH METADATA_ONLY, DBNAME, MOVE, NORECOVERY`, then streamed log backups up to `STOPAT` ([Microsoft Learn: T-SQL snapshot backup](https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/create-a-transact-sql-snapshot-backup), examples F, L–N; "point-in-time recovery using log backups taken … after the snapshot FULL backup"). A publish already mounts the files, so a point-in-time publish needs no copy (writes, including the log restores, go to the overlay).
- **Copy-only snapshots do not start a log chain.** Lab, bdfu-lab-sql1: `shop` (FULL recovery) after two snapshot backups (`is_copy_only=1`, `database_backup_lsn=0`) still has `last_log_backup_lsn` NULL and `log_reuse_wait_desc` NOTHING, so SQL Server truncates its log as in SIMPLE recovery and refuses `BACKUP LOG`.
- **Done (2026-11-05): job setting *SQL Server backup type*.** Regular full backups are the default for new jobs (snapshot without `COPY_ONLY`; it starts the chain and becomes the differential base, also for other tools); copy-only stays selectable, and jobs from before keep it. The agent reports per database the recovery model, whether a chain existed, the age of the last log backup, the type recorded in msdb, the position in the `.bkm` and the LSNs (kept in the restore point). The session warns when a full backup started a log chain, and about chains without a log backup for a day.
- **Checked in container tests (SQL Server 2022):** a full snapshot backup starts the chain; its `.bkm` restored with `METADATA_ONLY, FILE = <position>, MOVE, NORECOVERY` onto the snapshot's files, then two log backups up to `STOPAT`, gives exactly the rows of that second. A copy-only snapshot starts no chain (`BACKUP LOG` fails with error 4214); after another tool's regular full backup, log backups restore on top of a copy-only snapshot too.
- The `.bkm` is already kept in every SQL snapshot restore point (blob `sql-snapshot`); VSS restore points keep the writer documents (`vss-documents`), but restoring those with log backups needs the VDI and stays out.

## Risks

1. **The edition support of the T-SQL snapshot backup** (Express) is undocumented. Spike 2a decides. Without Express support, approach A covers Standard and Enterprise only.
2. **A long SQL write pause on slow storage:** the pause lasts as long as the XAPI snapshot, 20–40 s for four disks on Alletra. Mitigations: `[NOBAK]`, one snapshot per storage, the timeout, and the pause in the log and in the job's statistics.
3. **More than 64 user databases** per instance: batches with `GROUP` are separate suspends at different times. Then fall back to approach B, or snapshot per batch.
4. **On-disk state of shadow copies** in a crash-consistent image (step 3). Spike 3a decides between persistent and non-persistent shadow copies.
5. **Format details of VSS stores** (step 3): our reader is checked against libvshadow and fixtures from several Windows versions (2012 R2 to 2025).
