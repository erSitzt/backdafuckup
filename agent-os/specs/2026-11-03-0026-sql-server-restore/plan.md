# Plan: SQL Server database restore and publish

Spec folder: `agent-os/specs/2026-11-03-0026-sql-server-restore/`

## Context

SQL restore is the next knock-out feature. The order is agent → FLR → **SQL** → AD; the OpenTofu lab was postponed for it (user, 2026-11-03).

What exists today:
- The agent takes application-consistent snapshots: VSS copy-only, the SQL writer is asked to take part.
- Per database the backup records only `{Instance, Database, Files}`. The file list is flat, with no type marker.
- Nothing reads that metadata back, and nothing talks to SQL Server. A single database can only be recovered by restoring the whole VM, or by downloading its files through FLR.

**User decisions (2026-11-03):**
- **In v1:**
  - **Publish** (used often): a database from the backup is attached to a target instance *without copying it*, so the admin can pull out tables and data. Stopping the session removes the database and unmounts the restore point. Veeam calls this "Publish".
  - Overwrite the original database.
  - A new database on the original instance.
  - Another instance or VM.
  - Export as .bak or as .mdf/.ldf.
- **Environment:** standalone instances, mostly SQL Express and small databases. No Always On, no failover clusters.
- **No SQL test VM yet;** the user sets one up (Windows + SQL Express + agent).
- **UI:** no visuals; like Veeam Explorer for SQL.
- **SQL access:** a **stored account** picked in the wizard: a Windows account (the agent logs on with it) or a SQL login.
- **.bak** goes to a folder in the VM and can also be downloaded in the browser.
- Point-in-time restore with log backups stays in phase 2.

## Approach (from research and review)

- **Publish through a loopback iSCSI target in the agent.** This matches Veeam's approach (its mount server is the iSCSI target, with a write cache; Veeam Help Center, *How publishing works*).
  - **Target:** the agent runs a target on `127.0.0.1` with Tal Aloni's `ISCSI` library (LGPL-3.0-or-later, netstandard2.0 + net40, 1.5.6 from 2026-06-26) and `DiskAccessLibrary`.
  - **Disk:** our own copy-on-write `Disk`. Reads come from the server over the agent's HTTPS channel; writes go to a sparse overlay file in the VM, which is thrown away at the end. The backup never changes.
  - **Windows side:** the built-in initiator logs in non-persistently (P/Invoke `iscsidsc.dll`: `AddIScsiSendTargetPortalW`, `LoginIScsiTargetW`, `LogoutIScsiTarget`, `RemoveIScsiSendTargetPortalW`; the PowerShell cmdlet for removing a portal fails, spike 2c). The disk is brought online and writable (SAN policy OfflineShared; done per disk, never globally), and the needed partitions are mounted into folders, with no drive letters.
  - No new port and no driver; the data travels over TLS.
  - **Signature collisions** when publishing into the original VM: the target patches the MBR signature or GPT disk/partition GUIDs, with their CRCs, *in the overlay* before login, and sets "no drive letter" on GPT partitions.
  - **Only the Windows initiator may connect:** the connection's owning PID (from `GetExtendedTcpTable`) must be the PID of the running MSiSCSI service (spike 2c: the login comes from its svchost, not from PID 4), the initiator IQN must match, and the port is random. The library has no CHAP; a CHAP patch in a vendored copy is the fallback if the PID check proves unreliable.
- **Restore by attach, not by VSS restore.**
  - A VSS restore through SqlServerWriter only goes back to the same instance, and needs the Backup Components Document saved at backup time, which we never kept.
  - Attach (`CREATE DATABASE … FOR ATTACH`) covers every target on the same or a newer version, and SQL runs recovery on attach.
  - Copy restores stream the files through the existing agent transfer (SHA-256 per file).
- **.bak export = publish + backup:** publish on a staging instance (default: the original instance, same major version), then `BACKUP … COPY_ONLY, CHECKSUM, STATS`, then `RESTORE VERIFYONLY … WITH CHECKSUM`, then unpublish. No full copy of the files. Veeam also needs a staging instance for .bak.
- **Keep phase 2 open:** from now on, save the Backup Components Document and the SqlServerWriter metadata (`SaveAsXML`). A later VSS restore with NORECOVERY for log replay needs them.
- **Instance facts come from the backup itself:** the server reads the guest's SOFTWARE and SYSTEM hives with DiscUtils.Registry, as `MountPointMapper` does:
  - instance id, version and edition;
  - default folders;
  - service account.

  This works for older restore points too. The primary file is the data file with file id 1 in its page-0 header; log files are told apart by that header, with the extension as a fallback.
- **Rights:**
  - `dbcreator` for publish, a new database and export.
  - `sysadmin` recommended for overwrite (TRUSTWORTHY, owner via IMPERSONATE); missing rights are logged exactly.
  - The service account and free space come from SCM, the registry and `DriveInfo`.
- **Windows account:** `LogonUser` (batch, else interactive) with the **elevated linked token** (`TokenLinkedToken`; SYSTEM holds SeTcbPrivilege). Then impersonation and Integrated Security over shared memory. Because attach checks file access with the caller's token, the account also gets an ACE on the files.
- **SQL client:**
  - Agent.Core on `System.Data.Common.DbConnection`.
  - net48 uses the in-box `System.Data.SqlClient`.
  - net10 and the tests use `Microsoft.Data.SqlClient` 7.1.1 (MIT).
  - `CommandTimeout = 0` for long statements.
- **Tests:** `mcr.microsoft.com/mssql/server` (Developer, test use only, pinned CU tag) through `Testcontainers.MsSql` 4.16.0. The test disk is built at run time with DiscUtils (GPT, three small NTFS partitions C:/D:/L: on ≤ 64 MB, SYSTEM hive with `MountedDevices`, SOFTWARE hive with SQL keys). This also gives `MountPointMapper` its first test.

## What exists (reuse) and what must change

- **Reading the backup:**
  - Browse goes through `FileRestoreMounts` (pin, cache, limits, audit).
  - Sessions open their own instance like `FileRestoreSessionRunner.cs:57-69`.
  - Volumes are picked by `MountPoint` (`GuestImage.Open(describe: true)`).
  - Raw disk reads for publish use `RestorePointDiskStream` + `ChunkCache`, one stream per request, on worker threads.
- **Agent transfer** (`AgentTransfers`, `AgentTargetPaths.Plan`, `FileRestoreTransfer`). **Changes:**
  - per-item source volumes: today one volume per transfer (`AgentTransfers.cs:19,75-87`);
  - a new `IfExists = Fail` mode, gated by `sql-restore-v1`. Today `Overwrite=false` writes `RESTORED-<name>` (`FileRestoreTransfer.cs:212-221`);
  - attach only if no item failed and every hash matched.
- **`IRestoreFileSystem`** (`RestoreFileSystem.cs:20-44`) gains `Rename`, `Delete` and `GrantAccess` (add an ACE). `SetSecurity` replaces the whole descriptor (`WindowsRestoreFileSystem.cs:55`).
- **Agent concurrency:**
  - one work slot for transfers, SQL jobs and publishes. Today a second transfer is refused after 5 s (`AgentRunner.cs:217-234`);
  - no self-update while busy (`AgentRunner.cs:152-157`);
  - the server queues sessions for the same agent;
  - heartbeats during long statements;
  - an HttpClient without the 2-minute timeout (`AgentRunner.cs:56`).

  A publish holds the slot until it ends; the wizard says so.
- **Job channel:** as the transfer lookup (`AgentEndpoints.cs:249-259`):
  - agent key via `AuthenticateCachedAsync`;
  - the job is bound to its agent, in memory, 404 after the session ends;
  - command `SqlJob {JobId}`, then the agent pulls the request and posts progress and result.
- **Block endpoint for publish:** `GET api/agent/publish/{id}/disks/{n}?offset=&length=`. It is bound to the publish and its agent. The agent reads ahead, keeps an LRU cache, and runs requests in parallel.
- **Secret handling:** session `Parameters` (`SessionQueue.cs:71`) store only the `CredentialId`. The secret is read into the in-memory job and never goes into commands, logs or the agent journal.
- **Sessions:**
  - `SessionKind.SqlRestore` (restore, overwrite, export) and `SqlPublish` (runs until stopped, like FileBrowse);
  - `HasCapacity`/`RunAsync` (`EngineServices.cs:89-115`), DI (`:445-458`), `RestoreProgress` (`FileRestoreSessionRunner.cs:557-685`) extracted;
  - StartupRecovery: a publish becomes Stopped with "server restarted"; the agent notices the 404 and cleans up;
  - a startup sweep of the export folder.
- **Catalog:** `sql_database_count` in `RestorePointManifest` too, so `ApplyManifest` keeps it after recovery and import (`EngineServices.cs:298`, `InfrastructureEndpoints.cs:267`). Backfill for older points. While a publish or browser is open, the restore point is pinned and repository removal is refused, as with FLR.
- **Downloads:** `GuestZipExport.Write` gets a multi-volume overload (`:36`). The .bak upload endpoint uses `DisableRequestSizeLimit`, streamed to disk; a file store keeps it 24 h and issues a fresh 60 s ticket per click.
- **Credentials:** `CredentialKind.SqlServer` (protected JSON) after `LinuxCredentialEndpoints.cs`.
- **UI patterns:** `Backups.razor:56-61`, `FileBrowser.razor:136-238,483-531`.

## Guest metadata v2 (backup side)

- **Native (`bdfu-vss.cpp`):**
  - per file: type (data / log / other), filespec, recursive;
  - per component: the flags;
  - `SaveAsXML` of the SqlServerWriter metadata and, after `BackupComplete`, of the Backup Components Document.
- **Server:**
  - keep the prepare components; today the stored writers lose them (`BackupSessionRunner.cs:383`, `bdfu-vss.cpp:750`);
  - per-database consistency: "application" only if SqlServerWriter ended Stable without a failure and the component was in the snapshot set. v1 points take the point's consistency.
- **Context check** (spike 2b): today's context is `VSS_CTX_APP_ROLLBACK|…` (`bdfu-vss.cpp:560`). If the SQL writer does not take part, switch to `VSS_CTX_BACKUP`. Attach recovers crash-consistent files either way.
- **Not restorable in v1, with a reason:** system databases; FILESTREAM, full-text and In-Memory containers; files on folder mount points; BitLocker volumes (cannot be published).

## Flows

- **Prechecks (all):**
  - the target agent is online with `sql-restore-v1`;
  - the instance runs;
  - target major version ≥ source;
  - Express: allocated data size ≤ 10 GB (≤ 50 GB on 2025);
  - free space for files or overlay;
  - the name is free, or the database exists with no snapshots, not published, not mirrored (overwrite).

  Errors 948, 1827, 5120 and 33111 map to clear messages, each followed by cleanup.
- **Publish (`SqlPublish`):**
  1. Server: pin, open the disks that hold the database files, and create the publish (block endpoint).
  2. Agent:
     1. start the target with the CoW disks, after patching identities in the overlay;
     2. log in;
     3. bring the disks online and writable;
     4. mount the partitions under `%ProgramData%\backdafuckup Agent\publish\<id>\<letter>`;
     5. grant ACEs;
     6. attach as `<name>`, or `<name>_published` if the name is taken. The name is shown and can be changed.
  3. The session stays Running. Its log shows the instance, database, mount folder and overlay size. The overlay has a limit (`SqlPublish:MaxOverlayBytes`) and is checked against free space.
  4. **Stop, or auto-unpublish** after the duration chosen in the wizard (default 8 h):
     1. `SET OFFLINE WITH ROLLBACK IMMEDIATE`, then `DROP`. An offline drop leaves the files alone.
     2. Remove the access paths, set the disk offline, log out, stop the target, delete the overlay.
     3. Unpin.
  5. If the server stays silent (restart, network), the agent unpublishes the same way. At agent start or VM reboot (the login is non-persistent), the journal drives the cleanup.
- **New database (original or another instance or VM):**
  1. File names: `<file-safe name>.mdf`, `_<n>.ndf`, `_log.ldf`, `_log_<n>.ldf`, in the original folders, the instance default, or a chosen folder.
  2. Transfer with `IfExists=Fail` and inherited permissions.
  3. ACEs for the service SID and the account.
  4. Attach.
  5. Log orphaned users and what attach turned off (TRUSTWORTHY, Service Broker).
- **Overwrite original (with rollback):**
  1. Read the live file list from `sys.master_files`, plus owner, TRUSTWORTHY and broker.
  2. Transfer to staging names.
  3. `SET OFFLINE WITH ROLLBACK IMMEDIATE`, then `DROP`.
  4. Rename the old files to `*.bdfu-replaced` and the new files into place, then attach.
  5. Restore owner and settings where the rights allow.
  6. Delete the replaced files, unless *Keep replaced files* is set.
  7. **If anything fails, the session stops, or the server goes silent:** the agent rolls back at once and checks ONLINE and MULTI_USER.

  The journal records every path before it is touched.
- **Export .bak:**
  1. Publish on the staging instance under `<name>_bdfu_export`.
  2. `BACKUP TO DISK WITH COPY_ONLY, CHECKSUM, INIT, FORMAT, STATS` (plus `COMPRESSION` unless `EngineEdition = 4`), into the backup folder or a chosen folder (the service SID gets write access). Then VERIFYONLY.
  3. Unpublish. Optionally upload for the browser download.
  4. **Documented:** TRUSTWORTHY is off, the account is the owner, and the staging instance gets msdb history rows.
- **Export files:** ZIP of the database's files straight from the backup. No agent needed.
- **Log lines** name host, IP and instance (`sql01 (10.0.0.9) \SQLEXPRESS`) and the account by its name only.

## Console

- **Backups:** a "SQL" chip, and a *Restore SQL database* button → `/sql/{restorePointId}`.
- **`SqlBrowser.razor`** (Veeam Explorer style):
  - header with VM, time and consistency;
  - left: instances → databases;
  - right: files, version, consistency, and the reasons if a database cannot be restored;
  - actions on top: **Publish**, *Restore* (overwrite / as new / to another instance), *Export* (.bak / files).
- **Wizards:**
  - target VM and instance (Inventory job), account, name, file locations, options;
  - for publish, the auto-unpublish duration;
  - a summary with the warnings.
- **Sessions:** `SqlPublish` is shown with *Unpublish* (Stop) and the instance and database.
- **Credentials:** a section for SQL logins.

## Tasks (vertical slices; the suite stays green after each)

1. **Save the spec documentation:** `plan.md`, `shape.md`, `standards.md` (FLR additions + SQL safety: parameters/`QUOTENAME`, names untrusted, only the Windows initiator (MSiSCSI service) on the loopback target), `references.md`, empty `visuals/`.
2. **Spikes** (`spike.md`):
   - **2a (Linux):** attach of copied and crash-consistent files in the mssql container through the path map; BACKUP/VERIFYONLY; the DiscUtils runtime image with drive letters and SQL hive keys; the SqlClient size in the net10 agent.
   - **2b (user's SQL VM, today's agent):** a backup with guest processing, then `msdb.dbo.backupset` (`is_snapshot`, `is_copy_only`) and the errorlog → decide the VSS context.
   - **2c (app01, now):** loopback iSCSI with `ISCSI`:
     - publish a disk of its own restore point into the same VM (signature patch);
     - check that the PID-4 check works;
     - bring the disk online, mount it into a folder, read and write files (the overlay grows; the backup is unchanged);
     - log out, clean up;
     - measure the throughput over HTTPS.

     Go/no-go for publish.
3. **Guest metadata v2:** native file types, flags and XML; components kept; per-database consistency; hive reader and file classifier on the server; `sql_database_count` + backfill; read API.
4. **Slice 1: browse + export files:** multi-volume ZIP, `SqlBrowser.razor` read-only, Backups chip/button.
5. **Agent groundwork:** one work slot, no self-update while busy; per-item source volumes; `IfExists=Fail`; `Rename`, `Delete` and `GrantAccess`.
6. **Agent SQL layer:** capability, job channel, Inventory; accounts (linked token, SQL login); `CredentialKind.SqlServer` + API + UI. Tests: Agent.Core against the mssql container.
7. **Publish infrastructure:**
   - server: block endpoint;
   - agent: CoW disk + overlay; target host with the PID check and identity patch; initiator, disk and mount automation (`iscsidsc`, WMI `MSFT_Disk`/`MSFT_Partition`); journal and cleanup.
   - Tests on Linux: the CoW disk, the identity patch, the block endpoint, and the target through the library's iSCSI client if available. Windows parts in the lab.
8. **SQL publish session:** attach from the mount, Running session, unpublish on stop, auto-unpublish and silence; recovery after server, agent and VM restart; wizard; THIRD-PARTY-NOTICES (`ISCSI`, `DiskAccessLibrary`).
9. **Restore as new database** (original or another instance): prechecks, attach, ACEs, orphaned users, error mapping, wizard, queueing per agent. Test: end-to-end through the real `AgentRunner` and the mssql container.
10. **Overwrite with rollback:** OFFLINE + DROP, swap, attach, owner and settings, immediate rollback. Tests: forced attach failure; stop during each phase.
11. **Export .bak:** via publish + BACKUP + VERIFYONLY + unpublish; upload endpoint and file store with 24 h expiry, fresh tickets, startup sweep. The publish part is lab-tested; on Linux the BACKUP part runs against copied files.
12. **Leftovers and robustness:** journal sweeps, server restart during each phase, refusals.
13. **Docs:**
    - admin guide "SQL Server restore and publish": rights per target, Windows logon right, the iSCSI initiator service, loopback (nothing to open in the firewall), overlay space, auto-unpublish, what attach changes, logins and orphaned users, Express limits, version upgrade cannot be undone, not in v1;
    - runbook section F; THIRD-PARTY-NOTICES (Microsoft.Data.SqlClient); roadmap (later: publish for FLR, i.e. mount a restore point into a VM).
14. **Lab** on the user's SQL VM (runbook F):
    - publish (into the original and another VM) with a query on the published database;
    - the four copy and export targets;
    - Windows account (UAC token) and SQL login;
    - ACLs;
    - net48 agent on Server 2012 R2 (iSCSI and WMI available there);
    - data on D:, log on L:;
    - no leftovers (databases, mounts, iSCSI sessions, overlays, `*.bdfu-*` files).

## Verification

- **Automated:**
  - Unit and API tests per task.
  - SQL container tests carry `Category=Sql` and run only with `BDFU_SQL_TESTS=1`, because of memory: about 3 GB free, mssql needs about 2 GB. They use one shared container, run serially, set `MSSQL_MEMORY_LIMIT_MB=2048`, run the container as root, and keep bind mounts in the scratchpad.
  - A path-map hook (`D:\SQL` → `C:\bdfu\D\SQL`) mirrors `SandboxRestoreFileSystem`.
  - The full suite stays green without the variable; I run the SQL tests explicitly after each SQL task.
- **Lab-only:** the loopback iSCSI and Windows disk handling, ACLs, LogonUser and the UAC token, shared memory, the native VSS changes, the VSS context (spikes 2b/2c, task 14).

## Risks

1. **Loopback iSCSI with a third-party target** is documented for Microsoft's own target only; others report it works. Spike 2c decides early, on app01. The fallback is publish by copy: transfer the files into a temporary folder, then attach. It needs disk space but no iSCSI.
2. **Publish throughput:** reads go block by block over HTTPS, mitigated by read-ahead and the cache. Measured in 2c. Fine for pulling out tables; a large .bak export takes longer.
3. **SQL writer participation** in the APP_ROLLBACK context (2b). Without it, databases show as crash-consistent until the context is switched; restores still work.
4. **Overwrite is destructive.** It is guarded by the journal, the immediate rollback, keeping the replaced files until the new database is online, and prechecks.
5. **Windows account logon** needs the batch or interactive logon right. A clear error names it; a SQL login is the fallback.
6. **The agent work slot:** a publish or long job blocks other work into the same VM. The server queues, and the wizard says so.
