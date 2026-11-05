# Standards for SQL Server Log Backups and Point-in-Time Restore

There are no formal standards yet (`agent-os/standards/index.yml` is empty). These conventions apply:
- the foundation spec (`../2026-11-01-2134-xcpng-backup-foundation/standards.md`);
- the guest agent spec (`../2026-11-02-1430-windows-guest-agent/standards.md`);
- the FLR additions (`../2026-11-02-1713-file-level-restore/standards.md`): untrusted guest data, fail loudly, threads, audit, dependencies;
- the SQL restore additions (`../2026-11-03-0026-sql-server-restore/standards.md`): T-SQL, secrets, destructive steps, log lines.

The additions below apply on top.

---

## Additions for this spec

### Chain ownership
- Our backups carry `NAME = N'backdafuckup <kind>'` and `DESCRIPTION = N'job=<id>;session=<id>'`.
- Every other backup set in `msdb` counts as foreign. Copy-only sets are noted; the others are reported as a break.
- **Log backups:** never `COPY_ONLY`, because they must truncate the log.
- **Native user fulls:** not copy-only, since they are the chain base.
- **Nothing is written to `NUL`.**

### Durability
- **Durable journals:** agent journals that decide whether a backup exists (the upload queue) are written to a temp file, flushed and swapped with `File.Replace`. A missing or corrupt journal is an error in the log, never an empty queue.
- **Nothing reported early:**
  - a backup is reported only after `msdb` lists it and the file's SHA-256 is known;
  - the agent deletes a staged file only after the server confirmed the commit.

### Times
- **Catalog:** times are stored in UTC, plus the original offset (`backupset.time_zone`).
- **UI and log lines:** times are shown in the job's time zone, with the zone named.
- **STOPAT** is given in the source server's local time and logged together with the UTC time.

### Log lines
- **Per run and VM:** `bdfu-lab-sql1 (10.26.17.22) \SQLEXPRESS: 3 log backups, 4.2 MB, chain ok`.
- **Gaps and foreign backups:** named with database, time, device path and LSNs.
- **Skips:** named with the reason, e.g. image backup of the VM running, agent offline, or the previous run still running.
