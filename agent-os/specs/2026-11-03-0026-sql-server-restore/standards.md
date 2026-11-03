# Standards for SQL Server Restore and Publish

There are no formal standards yet (`agent-os/standards/index.yml` is empty). These conventions apply:
- the foundation spec (`../2026-11-01-2134-xcpng-backup-foundation/standards.md`);
- the guest agent spec (`../2026-11-02-1430-windows-guest-agent/standards.md`);
- the FLR additions (`../2026-11-02-1713-file-level-restore/standards.md`): untrusted guest data, fail loudly, threads, audit, dependencies.

The additions below apply on top.

---

## Additions for this spec

### T-SQL
- Values always go as parameters. Names (databases, logins, files) are put into statements only through `QUOTENAME`, or as `N'…'` literals with quotes doubled where T-SQL does not allow a parameter (for example `CREATE DATABASE … ON (FILENAME = …)`).
- Database, instance and file names from a backup are untrusted. File names in the guest are derived from a sanitized "file-safe" name, separate from the SQL name.
- Long statements (attach, BACKUP, VERIFYONLY, DROP) run with `CommandTimeout = 0` and report progress (`STATS`) or heartbeats.

### Secrets
- Sessions, logs, agent commands and the agent journal never hold a password. Sessions store a credential id; the secret reaches the agent only in the in-memory job over the pinned HTTPS channel.
- Log lines name the account by its name only.

### Destructive steps
- Before the agent changes a database or a file of a live instance, it writes the step to its journal (ProgramData). The journal is enough to roll back or clean up after a crash, a stop or a lost server.
- An overwrite keeps the replaced files until the new database is online.
- Every temporary object (databases, mounts, iSCSI sessions, overlays, staged files) carries a `bdfu` marker in its name and is listed in the journal.

### Loopback iSCSI target
- It binds to `127.0.0.1` only, on a random port, for the lifetime of one publish.
- It accepts only connections owned by the running MSiSCSI service process with the local initiator IQN.
- It never writes to the backup; every write goes to the overlay. The overlay is deleted at the end.

### Log lines
- SQL targets are named with host, IP and instance, e.g. `sql01 (10.0.0.9) \SQLEXPRESS`.
- The published mount folder and the overlay size are logged.
