# SQL Server Log Backups and Point-in-Time Restore — Shaping Notes

## Scope

This spec adds native SQL Server backups that the agent runs inside the VM, and a restore to any second within the retention.

**In v1:**
- **Transaction log backups** at a fixed interval per job.
- **Native full backups:**
  - as the chain base for SQL Server before 2022 and for jobs in VSS mode;
  - optional for all databases;
  - immediately when the chain breaks or a new database appears.
- **Daily full backups** of `master`, `msdb` and `model`.
- **Storage:** all files go into the repository as their own SQL backup objects, with a catalog of backup sets.
- **Chain watch:**
  - gaps;
  - foreign log backups;
  - a switch to SIMPLE;
  - a fork change;
  - the RPO.
- **Point-in-time restore:**
  - as a publish (image base only, no copy);
  - as a new database;
  - overwriting the original, with a final log backup first.
- **Console:**
  - a "SQL Server" page with the protected databases;
  - a page per database with a coverage timeline and a time picker;
  - the job settings;
  - a day session per job.

### Out of scope (later)
- Differential backups.
- Automatic restore tests (report A10).
- Mail or webhook alerts.
- Immutability and encryption at rest (A8, A9).
- Always On and failover clusters (A13).
- VDI streaming without staging.
- Resumable uploads.
- `msdb` history cleanup.

## Decisions

### From the report "MSSQL-Sicherung auf XCP-ng – Zielarchitektur & Spezifikation für die Eigenentwicklung" (user, 2026-11-05)
- **A1 (must):** point-in-time recovery per database from transaction logs.
- **A4:** an RPO of 15 min or less, with 5 min for a critical class.
- **A7 (must):** exactly one tool owns the log chain, and foreign `BACKUP LOG` entries in `msdb` raise an alarm.
- **A11 (must):** daily full backups of `master`, `msdb` and `model`.
- **Chain break:** "Eine Lücke löst sofort ein Full aus und einen Alarm."
- **Restore order:** the full (or the T-SQL snapshot `.bkm` with `METADATA_ONLY, NORECOVERY`), then the logs up to `STOPAT`. The report's Veeam reference shows the same order with VDI; the `.bkm` makes VDI unnecessary.

### Confirmed by the user (2026-11-05)
- **Sessions:** one session per job and day.
- **System databases:** in this spec, daily full by default.
- **Log interval:**
  - default 60 minutes;
  - presets 5/15/30/60;
  - longer intervals can be chosen freely ("wie 1., aber längere intervalle frei wählbar").
- **Versions:** SQL Server versions before 2022 are in v1, with a native full as the base ("Auch ältere, gleich mit rein").
- **Server-driven:**
  - The server starts every run and sends the account inside the job; no password is stored in the VM.
  - Files whose upload fails wait in a queue in the agent.
  - While the server is down, log backups pause.
- **Backup intervals are the user's choice:** "Wenn es um die Backup intervalle geht, dann sollte die wahl beim user liegen, wie bei anderen lösungen (veeam) auch". This means:
  - the native full and system full get their own schedules (daily, or weekly with days and a time);
  - the defaults are only defaults.
- **Chain break or a new database without a base:** a native full right away, plus a warning.
- **Restore entry:** a new page per database with a timeline; the server picks the base.

### Technical decisions
- **The image full stays the base for 2022+ in SqlSnapshot mode** (since `5de3e8e`, a full by default). Its `.bkm` is already in the restore point's `sql-snapshot` blob.
- **New repository object type for SQL backups:**
  - one object per run, VM, instance and kind;
  - chunked through the existing chunk store (dedup, zstd, GC, verify, import).
  - Restore-point manifests are VM-centric and do not fit.
  - The repository had no store for loose files; blobs are buffered in memory.
- **Identity of a database:**
  - `family_guid` is the stable identity;
  - `database_guid` changes on every restore.
- **Idempotent uploads:** `backup_set_uuid` is unique in the catalog, so the agent can always re-upload after a lost answer.
- **Durable queue in the agent:**
  - the queue entry is written before the BACKUP;
  - after a crash, an entry is resolved through `msdb` by the device path.
- **Backup lane:**
  - Log and full jobs run beside the agent's work slot and do not count as `Busy` for work reservation, so a long publish does not stall them.
  - A per-instance lock orders them against `snapshot-suspend`.
- **Retention:** the point-in-time window equals the VM's restore-point window.
- **Times:** stored in UTC with the original offset from `backupset.time_zone`. STOPAT is sent in the source's local time.
- **Logical file names:** read at restore time with `RESTORE FILELISTONLY`.
- **Overwrite:**
  1. Restore under a staging name into `.bdfu-new` files.
  2. Take a final log backup in single-user mode.
  3. Run the existing swap with rollback (spike 2c checks that restored files come online under the original).

## Context

- **Visuals:** none. The UI follows the existing console (MudBlazor), and the timeline is drawn from the coverage intervals.
- **References:** see `references.md`.
- **Product alignment:**
  - roadmap phase 2, item "SQL transaction log backup & point-in-time recovery";
  - priorities agent → FLR → SQL → AD;
  - no code or design from purchased products; only public sources.

## Standards applied

See `standards.md`: the conventions of the earlier specs, plus the additions for this spec (T-SQL safety, secrets, durable journals, chain ownership).
