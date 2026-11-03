# SQL Server Restore and Publish — Shaping Notes

## Scope

Restore and publish single SQL Server databases from application-consistent restore points of Windows VMs, without restoring the whole VM.

**In v1:**
- **Publish:** attach a database straight from the backup to a target instance, without copying it. The restore point is mounted into the target VM through a loopback iSCSI target in the agent, and writes go to a disposable overlay. The admin pulls out tables or data; stopping the session removes the database and unmounts the restore point.
- **Restore:**
  - over the original database;
  - as a new database on the original instance;
  - into another instance, also in another VM with the agent.
- **Export:**
  - as `.bak` (via publish on a staging instance + `BACKUP … COPY_ONLY`), into a folder in the VM and optionally as a browser download;
  - as `.mdf`/`.ldf` files (ZIP download straight from the backup).

### Out of scope (later)
- Point-in-time restore with log backups (phase 2; the VSS documents saved from now on keep this open).
- Always On availability groups, failover cluster instances.
- System databases (master, model, msdb), FILESTREAM, full-text catalogs, In-Memory OLTP containers, TDE without the certificate on the target.
- Object-level restore (single tables) beyond what publish allows.
- Publish for file-level restore (mounting a restore point into a VM): a roadmap item, built on the same infrastructure.

## Decisions

### Confirmed by the user (2026-11-03)
- **Priority:** SQL comes right after FLR; the OpenTofu lab is postponed ("sql backup + restore ist mir erstmal wichtiger").
- **Targets:** all of overwrite original, new database on the original instance, another instance or VM, and export (.bak or files).
- **Publish is required in v1.** The user's team uses Veeam's "Publish" often to pull out tables and data: "publish session is then stopped and database and restorepoint unmounted". The user also pointed out that Veeam mounts the backup as a disk on the target server.
- **Environment:** standalone instances, mostly SQL Express and small databases.
- **No SQL test VM yet;** the user sets one up (Windows + SQL Express + agent).
- **UI:** no visuals; like Veeam Explorer for SQL Server.
- **SQL access:** a stored account picked in the wizard, either a Windows account (the agent logs on with it) or a SQL login. No service-SID setup per instance.
- **.bak target:** a folder in the VM, plus an optional browser download.

### Technical decisions (from research and review)
- **Attach instead of a VSS restore.**
  - The SQL writer restores only to the same instance, and needs the Backup Components Document saved at backup time.
  - `CREATE DATABASE … FOR ATTACH` covers all targets on the same or a newer version, and recovers the snapshot files on attach.
  - `ATTACH_REBUILD_LOG` is never used.
- **Publish through a loopback iSCSI target in the agent.**
  - Tal Aloni's `ISCSI` + `DiskAccessLibrary` (LGPL-3.0-or-later) provide the target.
  - A copy-on-write disk reads from the server over the agent's HTTPS channel and writes to a sparse overlay in the VM.
  - The Windows initiator logs in non-persistently through `iscsidsc.dll`.
  - Disk identities are patched in the overlay against signature collisions.
  - Only the Windows initiator may connect: the connection must be owned by the MSiSCSI service process (spike 2c).
  - The fallback, if loopback iSCSI fails in the spike, is publish by copy into a temporary folder.
- **.bak export is publish + `BACKUP`** on a staging instance of the same major version (default: the original instance). Loose files cannot become a .bak without a running instance.
- **Save the VSS documents from now on** (writer metadata + Backup Components Document, `SaveAsXML`) for a later VSS restore with NORECOVERY (phase 2).
- **Instance facts** (id, version, edition, default folders, service account) are read on the server from the SOFTWARE and SYSTEM hives inside the backup, so they work for older restore points too.
- **Overwrite** sets the old database OFFLINE, then drops it (an offline drop keeps its files and ACLs), swaps the files and attaches. On any failure, stop or server silence, the agent rolls back at once; a journal records each step before it happens.
- **Rights:** dbcreator for publish, a new database and export; sysadmin recommended for overwrite (TRUSTWORTHY, owner). A Windows account uses the elevated linked token, so UAC does not filter admin groups.
- **SQL client:** `System.Data.Common` in Agent.Core; in-box `System.Data.SqlClient` on net48; `Microsoft.Data.SqlClient` 7.1.1 (MIT) on net10 and in tests.
- **One agent work slot** for transfers, SQL jobs and publishes. While it is held, the agent takes no self-update; the server queues further sessions for that agent.
- **Secrets:** sessions store only the credential id; the secret goes to the agent only in the in-memory job over the pinned HTTPS channel.
- **Tests:**
  - mssql container (Developer edition, test use only), opt-in with `BDFU_SQL_TESTS=1` because of memory;
  - a test disk built at run time with DiscUtils (GPT, NTFS, SYSTEM/SOFTWARE hives).

## Context

- **Visuals:** none (Veeam Explorer for SQL as the model).
- **References:** see `references.md`. The FLR pipeline, agent transfer, sessions and credentials are reused.
- **Product alignment:** roadmap MVP "MS SQL Server: application-aware backup and restore"; phase 2 "SQL transaction log backup & point-in-time recovery". The guest agent spec had already decided "SQL: restore into an instance and export as .bak; PITR later" and copy-only VSS backups, so Veeam keeps running in parallel.

## Standards Applied

- The foundation, guest agent and FLR conventions (see `standards.md`): untrusted guest data, fail loudly, worker threads, audit with host/IP/port, open-source dependencies only.
- SQL additions in `standards.md`: parameterized T-SQL and `QUOTENAME`, untrusted names, no secrets in logs or sessions, a journal before destructive steps, and only the Windows initiator (MSiSCSI service) on the loopback target.
