# backdafuckup – Administrator Guide

## Requirements

| Item | Notes |
|---|---|
| Backup server | Linux (systemd) or Windows Server, x64. Must **not** run in dom0 of an XCP-ng host. A VM in the pool or a separate machine both work. |
| .NET | Either publish self-contained (no runtime needed) or install the ASP.NET Core 10 runtime. |
| PostgreSQL | 15 or later, for the catalog (jobs, restore points, history). |
| Repository storage | A directory on a volume mounted on the backup server, e.g. an iSCSI LUN from an HPE MSA. |
| XCP-ng / XenServer | XCP-ng 8.3 or XenServer 8, with a pool admin account for the connection. |
| NBD network | Recommended: a pool network with the NBD purpose that the backup server can reach. Without it, backups fall back to the slower HTTP export. |

### Network ports

| From → to | Port | Used for |
|---|---|---|
| Browser → backup server | TCP 9555 (HTTPS) | Web console |
| Guest agent in the VM → backup server | TCP 9555 (HTTPS) | Agent connection, self-update, file restore into Windows VMs. Outbound from the VM only. |
| Backup server → XCP-ng hosts | TCP 443 (HTTPS) | XAPI, HTTP export, restore upload |
| Backup server → XCP-ng hosts on the NBD network | TCP 10809 (TLS) | NBD disk reads |
| Backup server → Windows VMs | TCP 445 (SMB) | Push installation of the agent |
| Backup server → file servers, domain controllers, DFS namespace servers | TCP 445 (SMB) | File restore to a network share, DFS referrals |
| Backup server → Linux VMs | TCP 22 (SSH) | File restore into Linux VMs |
| Backup server → PostgreSQL | TCP 5432 | Catalog (usually on the same machine) |

The backup server also needs DNS for the host names of pools, shares and VMs.

### Disk transports

Each job has a **Disk transport** setting (job editor → Storage → Advanced):

| Setting | Behaviour |
|---|---|
| Automatic (default) | NBD if the pool offers a reachable NBD endpoint, otherwise the HTTP export, with a warning in the session log. |
| NBD only | Fails the VM if NBD cannot be used. |
| HTTP export only | Always uses the HTTP export. |

- **NBD** (TCP 10809, TLS): reads each disk over several parallel connections and, for incrementals, only the changed blocks.
- **HTTP export** (`export_raw_vdi` over the XAPI HTTPS port 443): needs no network setup. It uses one stream per disk and transfers the whole allocated disk on every backup, also for incrementals. The repository still stores only new data.

The session log shows how the transport was chosen:
- the pool networks with NBD purpose and the hosts' IP addresses on them;
- the result of `VDI.get_nbd_info`;
- every NBD connection attempt, with address, port, host, network, duration and error;
- the HTTP request and the format of the exported stream.

The HTTP export uses the dynamic VHD format by default, so only allocated blocks are transferred. If a VHD stream is ever rejected, `"Xen": { "HttpExportFormat": "Raw" }` exports every byte of the disk unprocessed. The lab test `Http_export_vhd_matches_raw` compares both formats on your pool.

On a 1 Gbit/s link, one HTTP export stream reached about 60 MB/s on a test pool, roughly half the line rate. backdafuckup's own processing was not the limit: the VHD parser handles several GB/s.

### Parallel processing

A job processes several VMs at the same time ("VMs processed in parallel", default 2), and within a VM several disks ("Disks per VM processed in parallel", default 2). Each disk is its own stream: one HTTP export, or the configured number of NBD connections.

Parallel streams help as long as the network has room. On a 1 Gbit/s link (about 110 MB/s of data), two HTTP export streams already reach the line rate; more streams only share it. For NBD, keep the total number of connections in mind: VMs × disks × "NBD connections per disk" should not exceed the 16 connections XAPI allows.

### VM snapshots

- **One snapshot per storage at a time** (`"Xen": { "OneSnapshotPerStorage": true }`, the default): VMs on the same storage repository are snapshotted one after another. On shared LVM storage the storage manager handles snapshots one by one anyway; started together, each one takes as long as all of them. The wait happens before the guest is frozen and shows in the session log ("Waited 12.3 s for the snapshot of another VM on 'storage'"). Reading the disks still runs in parallel.
- **Disks left out (`[NOBAK]`):** a disk with `[NOBAK]` in its name (the same marker as in Xen Orchestra) is not snapshotted and not backed up, e.g. a scratch disk or the cloud-init config drive. A restore of the VM creates it again as an empty disk of the same size: on the storage chosen for it, at the original location on its original storage, otherwise next to the first restored disk. It needs XCP-ng 8.3 (`VM.snapshot` with `ignore_vdis`); older pools back the disk up anyway and log a warning.
- **Free space:** before the snapshot every storage of the VM must have at least `"Xen": { "MinStorageFreePercent": 5 }` percent free (0 turns the check off). A snapshot takes space until it is released, and a full storage stops the running VMs and the storage's own coalesce. The VM then fails with the storage name, its free space and the limit; nothing is snapshotted and the guest is not frozen. The session log shows the free space of every snapshot.

### Restore upload

Restores write each disk with `import_raw_vdi` over HTTPS to the pool master, or for local storage to the host that has it attached. By default the disk is sent as a dynamic VHD that contains only the 2 MiB blocks with data. Empty regions are neither transferred nor written. The session log names the target host and how much is sent, and the session page shows the restored and transferred bytes.

`"Xen": { "HttpImportFormat": "Raw" }` sends every byte of the disk instead. Use it if a host rejects the VHD upload. The lab test `Backup_and_restore_roundtrip` uses the format from `BDFU_LAB_IMPORT_FORMAT` (`Vhd` by default, or `Raw`).

### Enable NBD on a pool network

Backups read VM disks fastest over NBD (TCP 10809) with TLS:

```bash
xe network-list                                    # find the network UUID
xe network-param-add param-name=purpose param-key=nbd uuid=<network-uuid>
```

The backup server must reach that network on port 10809. Use `insecure_nbd` (no TLS) only in isolated test labs; in that case also tick "Allow unencrypted NBD" for the pool in the console.

If NBD connections fail with "Connection refused", run these commands on a host. They show which network has the NBD purpose, which IP each host has on it, and whether `xapi-nbd` listens:

```bash
xe network-list params=uuid,name-label,purpose
xe pif-list params=host-name-label,network-name-label,IP
systemctl status xapi-nbd
ss -ltnp | grep 10809
```

### Changed block tracking (CBT)

backdafuckup enables CBT on every disk it backs up. While CBT is enabled on a disk, **storage live migration** of that disk is not possible. When a disk is reset, for example after a host crash, the next backup reads it in full.

Incremental backups need no setting. XAPI reports the changed blocks as a bitmap with one bit per 64 KiB block; one byte of it describes 512 KiB of the disk. backdafuckup reads every 512 KiB range whose byte has any bit set. That is right whatever the order of the bits within a byte is, and it reads exactly the chunks a bit-exact reading would when the repository's chunk size is a multiple of 512 KiB (the default is 1 MiB). XenServer documents the most significant bit as the first block; the storage manager (sm) turns blktap's bitmap into that order.

Only with chunks smaller than 512 KiB does a bit-exact reading save reads: set `"Xen": { "CbtBitOrder": "MostSignificantBitFirst" }`, after checking it on your pool with the lab test `Cbt_bit_order_spike` (see "Lab verification" below). A wrong value would make incremental backups miss changes, so leave it unset unless the test confirms it.

## Installation (Linux)

```bash
dotnet publish src/Backdafuckup.Server -c Release -r linux-x64 --self-contained -o publish/linux
sudo useradd --system --home /var/lib/backdafuckup backdafuckup
sudo mkdir -p /opt/backdafuckup /etc/backdafuckup && sudo cp -r publish/linux/* /opt/backdafuckup/
sudo cp deploy/linux/appsettings.example.json /etc/backdafuckup/appsettings.json   # edit the connection string
sudo cp deploy/linux/backdafuckup.service /etc/systemd/system/                      # adjust ReadWritePaths
sudo systemctl daemon-reload && sudo systemctl enable --now backdafuckup
sudo journalctl -u backdafuckup | grep "setup token"
```

Then open `https://<server>:9555`, enter the setup token and create the administrator. The server uses a self-signed certificate for its host name, its fully qualified name, `localhost` and its addresses. It is issued again with the same key before it expires and when the host's name changes (for example a data folder taken over from another server), so the agents, which pin its key, keep trusting it. To use your own, configure `Kestrel:Certificates:Default` or a full `Kestrel:Endpoints` section.

## Installation (Windows)

```powershell
dotnet publish src/Backdafuckup.Server -c Release -r win-x64 --self-contained -o publish\win
Copy-Item deploy\windows\install-service.ps1 publish\win\
# As Administrator:
.\publish\win\install-service.ps1
```

Configuration goes in `%ProgramData%\backdafuckup\appsettings.json`.

At its start the server waits up to 3 minutes for the catalog database (for example PostgreSQL still starting at boot) and logs each attempt with host and port; a wrong password or database name stops it at once.

To install or update a Windows server remotely from a Linux machine (SMB + WinRM, PostgreSQL included), see [`scripts/deploy/README.md`](../scripts/deploy/README.md).

## Guest agent (application-consistent backups)

XCP-ng / XenServer removed the quiesced snapshot in 8.1, so a VM snapshot is crash-consistent: like a power failure. For application-consistent backups of Windows VMs (SQL Server, Active Directory, Exchange, other VSS-aware applications) install the **backdafuckup agent** in the VMs and switch on **Guest processing** in the job (Advanced → Guest processing).

**How it works**
1. Before the snapshot, the agent gathers the VSS writer metadata (volumes, writers, SQL databases with their files). It goes into the restore point as `guest-metadata`.
2. Right before `VM.snapshot` the agent starts a VSS snapshot set. The writers flush and freeze. Then our own VSS provider (`bdfu-vss.dll`) holds the writes and reports "frozen".
3. The server takes the VM snapshot and thaws the guest right away. The session log shows the freeze, snapshot and hold times and the writer states.
4. **10 s limit:** Windows releases held writes after **10 seconds** at the latest (`VSS_E_HOLD_WRITES_TIMEOUT`). Only the VM snapshot runs inside that window, so it must finish within 10 s. Otherwise the backup is crash-consistent and the session ends with a warning, or the VM fails if the job says so.
   - XAPI snapshots the disks of a VM one after another, so the time grows with the number of disks. Measured on shared LVM storage (FC/iSCSI), one snapshot at a time: 7–9 s for a VM with one disk, 25 s for three disks, 47 s for four disks. The session log shows the time of every snapshot with the free space of its storages ("Snapshot … created in 8.1 s: 1 disk(s) on 'storage' (23.4 % free).").
   - What shortens it: fewer disks in the snapshot (mark disks without backup value with `[NOBAK]`, see [VM snapshots](#vm-snapshots)), and only one snapshot at a time per storage (the default).
5. **Copy-only:** the backup is a VSS copy-only backup (`VSS_BT_COPY`). The SQL Server backup chain (differential base, log backups) of other backup products stays untouched. Logs are not truncated. Domain controllers are the exception: they get a VSS full backup, so that AD records it (see [Active Directory](#active-directory)).

**Method "SQL Server snapshot where SQL Server 2022+ runs, VSS for all other VMs"**

When the VM snapshot takes longer than VSS allows (see the 10 s limit above), choose this method in the job (Advanced → Guest processing) and a SQL Server account. For VMs with SQL Server 2022 or later it uses SQL Server's T-SQL snapshot backup instead of the VSS freeze:
1. VSS only gathers the metadata (databases, files, volumes), as above, and ends right away.
2. The agent suspends the writes of every user database with `ALTER DATABASE … SET SUSPEND_FOR_SNAPSHOT_BACKUP = ON` (with `(MODE = COPY_ONLY)` for copy-only backups, see *Backup type* below).
   - It does this database by database, each on its own connection. This also covers databases that `AUTO_CLOSE` has closed, the default in SQL Server Express.
   - Reads go on. Writes wait; the session log shows how many databases were suspended.
3. The server takes the VM snapshot, which may take as long as it needs.
4. The agent records the snapshot with `BACKUP DATABASE … WITH METADATA_ONLY`, which resumes the writes.
   - Each database gets a full backup of the chosen type in `msdb` (`is_snapshot = 1`, named "backdafuckup restore point …").
   - The snapshot backup metadata (`.bkm`) goes into the restore point with each database's position in it and its LSNs, and the agent deletes it in the guest.
5. If the snapshot fails, the agent resumes the writes without recording anything. It also resumes them if the server does not report the snapshot within 2 minutes (`Agent:SqlSnapshotHoldLimit`). If the agent stops or loses its connection to SQL Server, SQL Server resumes by itself.

**Backup type** (job setting *SQL Server backup type*):
- **Full backup (in the backup chain)**, the default for new jobs: a regular full backup.
  - Log backups and differential backups build on it, also those of other tools: the next differential backup of another tool is based on this snapshot.
  - For a database in FULL or BULK_LOGGED recovery that never had a regular full backup, it starts the log chain. Until then SQL Server frees the log at checkpoints as in SIMPLE recovery and refuses `BACKUP LOG` (error 4214). From then on only log backups free it, so the log grows until log backups run. The session ends with a warning naming these databases.
- **Copy-only backup (outside the backup chain):** changes nothing in the backup chains. Choose it for VMs whose SQL Server backups another tool takes with differential backups, or for backups kept apart from the chain. Log backups of an existing chain can still be restored on top of it, but it does not start a chain.
- With either type, the session warns about databases in FULL or BULK_LOGGED recovery that have a log chain but no log backup for more than a day (by any tool): their log grows.
- VSS backups are copy-only (domain controllers: full, see [Active Directory](#active-directory)), also for the VMs of this method that fall back to VSS.
- Jobs that used the SQL Server snapshot before this setting existed keep copy-only backups.

**VSS for all other VMs:** if the agent cannot suspend a single database, the same backup uses the VSS freeze, as with the method "VSS for every VM". The session log says why:
- the VM has no SQL Server, or only one older than 2022 (a note);
- the agent has not updated itself yet (a note);
- the SQL Server is 2022 or later, but no database could be suspended, e.g. missing rights (a warning).

A job can therefore hold SQL Servers and other VMs. If at least one database was suspended, the VM stays with the SQL Server snapshot; databases of other instances in it (e.g. an older SQL Server next to SQL Server 2022) are then crash-consistent with the reason.

What it means:
- **Consistency:** the SQL Server databases are application-consistent; files outside SQL Server are crash-consistent. The restore point shows "crash" with this explanation, and the SQL browser shows each database's own state.
  - The session ends with a warning if a database of a VM with the SQL Server snapshot could not be suspended (offline, missing rights).
  - System databases (`master`, `model`, `msdb`) cannot be suspended; restore them from a native SQL Server backup.
- **Writes wait as long as the snapshot takes.** On the shared LVM storage of the lab that was 25–47 s for three or four disks. Applications with the usual 30 s command timeout may see timeouts; a write whose client gave up is still committed afterwards. Keep the snapshot short with `[NOBAK]` and one snapshot per storage.
- **Account:** a Windows account or a SQL Server login that is `sysadmin` on the instances. Windows accounts log on in the guest; the agent never stores the account.
- **Edition:** works with SQL Server Express too (tested with SQL Server 2025 Express and 2022 Developer).
- The SQL Server service account writes the `.bkm` into the instance's backup folder (registry `BackupDirectory`, else `InstanceDefaultBackupPath`).

Restore points show their consistency ("application", or "crash" plus the reason) under *Backups & Restore*.

**Requirements**
- Windows Server 2016 or later, or Windows 10/11, for the .NET 10 build (self-contained, no runtime needed). Older systems such as Server 2012 R2 get the .NET Framework 4.8 build, which needs .NET Framework 4.8 installed.
- The VM reaches the backup server over HTTPS (port 9555). The agent connects outbound only; nothing has to be opened in the VM.
- The guest tools (XCP-ng or Citrix VM Tools) report the VM's IP addresses, which the push installation uses.
- The agent identifies its VM by the BIOS serial number, which XCP-ng sets to the VM UUID. No mapping is needed.

**Setup**
1. *Guest Agents → Settings*: enter the address under which the VMs reach the server, e.g. `https://backup.example.local:9555/`.
   - The agents pin the server's own certificate (SPKI SHA-256, the hash of its public key), so the self-signed certificate works, also after it was issued again with the same key.
   - With a certificate from `Kestrel:Endpoints`, Windows certificate validation applies. You can also configure pins with `Agent:ServerPins`.
2. **Push installation:** add an administrator account under *Windows Credentials*, select the VMs under *Guest Agents* and choose *Install agent*.
   - The server connects over **SMB 445** with NTLMv2 to the first reachable IP address of the VM.
   - It copies the agent to `C:\Program Files\backdafuckup Agent` (via `C$`, otherwise `ADMIN$`), creates the service **BackdafuckupAgent** (Local System, automatic start) and starts it.
   - If the .NET 10 build does not keep running, it replaces it with the .NET Framework 4.8 build.
   - A job with *Install the agent where it is missing* does the same during the backup.
3. **Without push** (group policy, software distribution): *Guest Agents → Download setup* gives a ZIP with both builds, `agent.json` and `Install-Agent.cmd`.
   - Run the script as administrator or Local System, e.g. as a computer startup script.
   - On a computer that already has the agent it returns at once (the agent updates itself); `--force` reinstalls.
   - The ZIP contains the enrollment secret: give only the computers that need it read access. *Renew secret* invalidates it for new installations.

**Accounts for the push installation**
- Domain accounts in the local Administrators group work directly: `DOMAIN\user` or `user@domain`.
- Local accounts: UAC filters network logons of local administrators. Only the built-in `.\Administrator` works without changes; other local accounts need `LocalAccountTokenFilterPolicy=1`, which weakens UAC.
- SMB signing is supported. If NTLM is blocked by policy, use the setup package.

**What the agent does on the guest**
- **Folder:** `%ProgramFiles%\backdafuckup Agent`, readable by SYSTEM and administrators only. It holds the program, `agent.json` (server address, pin, enrollment secret), `agent.key` (the agent's own key) and `agent.log`.
- **Service:** `BackdafuckupAgent`, with recovery set to restart. It registers the VSS provider "backdafuckup VSS provider" as COM+ application (Local System).
  - The provider only takes part in backdafuckup's own freeze. Other backup products keep using the Windows system provider.
- **Server connection:** a long poll to the server (one open HTTPS request). The server only offers the commands *info, prepare, freeze, thaw, abort* and *restore files*. The agent runs no commands or scripts from the server; the only program it takes from the server is its own update (see *Self-update*).
  - *Restore files* writes the files that someone restores in the console into this VM, as Local System and with the permissions from the backup (see [File-level restore](#file-level-restore)). Only local fixed volumes are accepted, never the agent's own folder.
  - So every console user can write files into each VM with the agent (all console users are administrators so far). Give console access accordingly. The same holds for Linux VMs and the stored Linux credentials.
- **Self-update:** after a server update the agent downloads the new files (SHA-256 checked) and restarts. This never happens during a backup.
- **Remove:** `"%ProgramFiles%\backdafuckup Agent\backdafuckup-agent.exe" uninstall` removes the service, VSS provider and folder.

**Endpoint protection:** a push installation looks like lateral movement to EDR products (file copied to an admin share, service created remotely). See [Microsoft Defender for Endpoint](defender.md).

## Backups & Restore

The page lists every VM with restore points once, with its newest restore point. Expanding a VM loads all its restore points (Restore, Verify, Delete); the *Restore* button of a VM restores from its newest one.

- **By job** (the default): one block per job, with its schedule and how many of its VMs missed a run, have no restore point, or are crash-consistent with a warning.
  - A VM in two jobs is listed under both, each with its own restore points.
  - VMs that are in a job but have no restore point of it yet (just added, or every run failed for them) are listed with *no restore point*.
  - Restore points of deleted jobs are listed last under *Without job*.
- **By VM:** one row per VM with all its jobs.
- **Missed a run:** a VM's newest restore point in a job is older than the job's most recent scheduled start that is at least 12 hours ago (a job with hundreds of VMs runs for hours). Jobs without a schedule and disabled jobs never mark a VM.
- **Filters:** name (VM or job), jobs, repository, content (Active Directory, SQL Server), consistency of the newest restore point, state (missed a run, no restore point, up to date) and the age of the newest restore point. Sorting by name, oldest restore point first, or size. On a phone only the search is shown; *Filters* opens the rest and shows how many are set.
- **Links:** filters, grouping and sort are in the address, e.g. `/backups?state=stale&state=nopoint` for every VM that needs attention; reload and Back keep them.
- Long lists show 100 VMs per block, then *Show all*.
- **API:** `GET /api/restore-points/vms` returns one row per VM with the same filters (`q`, `jobId`, `repositoryId`, `content`, `consistency`, `state`, `olderThanDays`; see the API reference). The restore points of a VM: `GET /api/restore-points?vmId=…&jobId=…`.

JSON responses of the API are compressed (Brotli or gzip) when the client accepts it, except those of `/api/setup`, `/api/auth` and `/api/tokens`, which can carry secrets.

**Entire VM to the original location:**
- The VM gets the MAC addresses of the backup, so the guest keeps its network adapters and their settings (static addresses, DNS servers, DHCP reservations). New MAC addresses are an option only for another location.
- **With *Replace the existing VM*:**
  - the original is shut down and renamed `<name> (replaced <time>)`, and kept unless *Delete the replaced VM* is chosen;
  - it keeps the same MAC addresses, so do not start both VMs at the same time;
  - the restored VM gets a new VM id, because the original still holds the old one. Its jobs then back up the restored VM, starting with a full backup; the session log names each job.

## File-level restore

*Backups & Restore → Restore files* opens a restore point in a file browser: volumes and folders on the left, the folder content on the right.

**How it works**
- The server reads the file systems straight from the repository. Nothing is mounted, no VM is started, and the backup is never changed.
- It finds partitions (MBR, GPT) and LVM volume groups, also across several disks.
- **Drive letters and mount points** come from the backup itself: Windows from the registry (`MountedDevices`), Linux from `/etc/fstab`. This also works for restore points taken without the agent.
- **Readable:** NTFS, FAT, ext2/3/4 and XFS, also on linear LVM volumes.
- **Not readable:** BitLocker, LUKS, ReFS, Btrfs, Storage Spaces, dynamic disks and striped or thin LVM volumes. The browser lists them with the reason.
- **Crash-consistent restore points:** the file system journal is not replayed, so the newest changes before the snapshot can be missing. The browser shows a hint.

**Downloads**
- **One file** downloads as it is.
- **Several files or folders** download as one ZIP file.
  - The ZIP uses ZIP64, so files over 4 GiB and more than 65,535 entries work. One ZIP holds at most 1,000,000 files and folders (`FileRestore:MaxZipEntries`).
  - It keeps modification times, Windows attributes (read-only, hidden, system) and Unix permissions.
  - **Not in the ZIP:** symbolic links, junctions, special files and files whose content cannot be read correctly (deduplicated, EFS-encrypted, cloud placeholders). They are listed with the reason in `_backdafuckup-skipped.txt` inside the ZIP.
  - **Changed names:** names that could leave the target folder when extracted get `_` instead: `\`, a leading drive letter such as `C:`, control characters, and names of only dots and spaces. A name that then collides gets ` (2)`.
- **Errors while reading:** if a file cannot be read in the middle of a download (damaged repository), the download breaks off. The browser reports it as failed instead of saving a short file, and the session log names the file.
- **Download links:** the browser first sends the selection and gets a link back. The link works once, for the same user, for 60 seconds (`FileRestore:DownloadLinkLifetime`).

**Restore to a network share**
- Select files or folders, choose *Restore… → Copy to a network share*, and enter the folder as `\\server\share\folder`. Missing folders are created, and the selected items go directly into that folder.
- The account is a *Windows Credential* (`DOMAIN\user`) with write access to the folder.
- The backup server connects over **SMB 445** (SMB 2/3, NTLMv2). The log names the server with its address, e.g. `fs01 (10.0.0.5):445`.
- **Replace existing files** overwrites files of the same name. **Keep existing files** writes the restored file as `RESTORED-<name>` (then `RESTORED-<name> (2)`, …). Folders are merged in both modes.
- **Kept:** content, modification and access times, the creation time of Windows files, and the attributes read-only, hidden, system and archive.
- **Not kept:** permissions, owners, alternate data streams, and Linux permission bits. Hard links become separate copies.
- **Left out and reported:** links and special files, files whose content cannot be read correctly, and Linux names that Windows does not allow (`\ : * ? " < > |`, a trailing dot or space, device names such as `CON`).
- **Safe writes:** every file is written as `.bdfu-restore-<id>.tmp` and renamed when complete. A failed file never leaves half a file behind. If the backup service stops in the middle of a file, the temporary file can remain.
- **Links on the share:** a link or junction in the way is never written through. Windows servers report them; Samba follows its own symbolic links on the server (`follow symlinks`, `wide links`) and shows them as normal folders.
- **Progress and problems:** every restore is a session of type *FileRestore* under *Sessions*, with progress and one line per problem (the first 500).
  - The session ends with *Warning* when something was left out.
  - It stops early when the connection to the share is lost or after 100 errors in a row, for example when the share is full.
  - *Stop* keeps what was restored so far.
- **Limits:** at most 2 file restores run at the same time (`Engine:MaxConcurrentFileRestoreSessions`). A restore uses its own copy of the restore point, so it goes on when the browser closes.
- **DFS namespaces:** a path such as `\\corp.example\dfs\Exchange` is resolved like Windows does it, with DFS referrals over `IPC$`:
  1. a domain controller names the namespace servers;
  2. a namespace server names the share the link points to;
  3. the restore works directly on that share.

  The log shows each step (`DFS: … → \\fs01\exchange`). Use the full DNS name of the domain: short NetBIOS names such as `\\corp\dfs` do not resolve on a Linux backup server. If a file cannot be renamed into place on the target, it is copied to its name instead (not atomic), and the session ends with *Warning*.

**Restore into a Windows VM (guest agent)**
- *Restore… → Back into the VM* writes the files through the backdafuckup agent in the VM of the restore point. The agent must be online and at least the version that reports file restore (it updates itself after a server update).
- **Original location:** the drive letter from the backup plus the original folder, e.g. `C:\Users\x\Documents`. Not available for volumes without a drive letter in the backup.
- **Another folder:** e.g. `D:\Restore`; the selected items go directly into it. Missing folders are created.
- **Permissions:** with *Restore owner and permissions*, owner, group and the explicit access rights (DACL) come from the backup. Inherited entries are taken from the target folder, as when copying in Windows. This is on by default for the original location and off for another folder, where local accounts and inheritance may not fit. The audit list (SACL) is not restored.
- **How it works:** the server plans the items and the agent fetches them over its own HTTPS connection to the server (port 9555, outbound from the VM as for backups).
  - Every file is written as `.bdfu-restore-<id>.tmp` next to its target and replaces the old file only when complete. A read-only, hidden or system file is replaced too.
  - The agent reports the SHA-256 of every written file; the server compares it with what it sent.
  - Files in use in the VM fail one by one and are listed; the rest goes on.
- **Never written:** through links or junctions on the target (the item fails), into the agent's own folder, or outside the chosen location. Only local fixed volumes are accepted, no network or removable drives.
- **Not restored yet:** symbolic links and junctions of the backup, alternate data streams, the sparse flag. Hard links become separate copies.
- **Stop** lets the agent finish the current file. If the agent neither downloads nor reports for 5 minutes (`Agent:TransferSilenceLimit`), the restore fails; what was written stays. During the restore the agent does not update itself.

**Restore into a Linux VM (SSH)**
- *Restore… → Back into the VM (SSH)* connects to the VM over SSH (port 22) with a *Linux credential* (Credentials → Linux): `root`, or an account that may use `sudo`. Enter the sudo password in the credential if sudo asks for one; sudo with `requiretty` works too.
- **Address:** empty uses the IP addresses the guest tools report; otherwise enter the address and port.
- **Original location:** the mount point from the backup's `/etc/fstab` plus the original folder, e.g. `/home/alice/docs`. Not available for volumes without an fstab entry. **Another folder:** e.g. `/srv/restore`; the selected items go directly into it.
- **How it works:**
  1. The files are uploaded over SFTP into `/var/tmp/bdfu-<id>` (mode 0700, owned by the login account). The selection must fit there; the free space is checked first.
  2. One restore script runs as root (directly or through `sudo`). It puts every file into place as a temporary file next to its target and renames it when complete. It sets mode, times and, with *Restore owner and group*, owner and group (otherwise those of the restore folder), applies the SELinux label where SELinux is on, and creates symbolic links last.
  3. The staging folder is removed at the end.
- **Never written:** through a symbolic link on the target (the item fails), or outside the chosen location. File names never become shell code: they travel base64-encoded and reach commands only as quoted arguments.
- **Host keys:** the first connection trusts the VM's SSH host key; later connections must present the same key or fail with both fingerprints. After a reinstall, remove the old key under *Credentials → Known SSH hosts*.
- **Not restored:** ACLs, extended attributes and file capabilities; hard links become separate copies.

**Audit trail:** every opened browser is a session of type *FileBrowse* under *Sessions*. Its log shows who opened which restore point, every download (files, folders and size, plus skipped items) and every restore started from it. *Stop* in the session closes the browser.

**Limits and resources**
- **Closing:** a browser closes after 30 minutes without use (`FileRestore:IdleTimeout`) and when the service restarts. At most 4 browsers are open at the same time (`FileRestore:MaxMounts`).
- **Retention:** while a browser is open, its restore point is not deleted. Retention removes it at the next job run.
- **Repositories:** a repository cannot be removed while a browser on it is open.
- **Memory:** the browsers share a cache of 512 MiB (`FileRestore:CacheBytes`).

## SQL Server databases

*Backups & Restore → SQL databases* opens the SQL browser for a restore point of a Windows VM backed up with guest processing. It lists the instances (from the backup's registry), their databases with files, sizes and consistency, and the reasons a database cannot be restored. The databases come from the SQL Server writer's metadata. Its plain file lists are told apart by the first page of each file: file id 1 is the primary data file, 2 the log file, higher ids go by the extension.

**Five ways to get a database back**
- **Export files:** a ZIP with the database's files straight from the backup (primary data file first) and an attach script. No agent is involved.
- **Export .bak:** a native SQL Server backup of the database as it is in the restore point, for restoring it anywhere with `RESTORE DATABASE`.
  1. The database is published on an instance in a VM with the agent (the staging instance; the original VM works too). It keeps its name if that is free there, else it becomes `<name>_bdfu_export`.
  2. It is backed up with `BACKUP DATABASE … WITH COPY_ONLY, CHECKSUM` (and `COMPRESSION`, except on Express) into the instance's backup folder or a chosen folder, and checked with `RESTORE VERIFYONLY … WITH CHECKSUM`.
  3. It is unpublished again, also when the backup fails.

  The file is named `<database>_<restore point time>.bak` and never replaces an existing file (`_2`, `_3`, …). SQL Server's service writes it, so a chosen folder must be writable for the service (the backup folder is).
  - *Also keep it on the backup server for download:* the agent then sends the file to the backup server. The session page offers it for 24 hours (`<data directory>\sql-exports`).
  - What the .bak shows: a copy-only full backup taken on the staging instance (its version: a newer instance upgrades the database, and the .bak then restores only there or on newer versions). TRUSTWORTHY is off, and the attaching account is the owner. The staging instance's `msdb` keeps a backup history row.
  - SQL Server stores the name and the file paths the database has at backup time. Inside the .bak the database has the staging name (`<name>_bdfu_export` if its name was taken), and its files have the paths of the temporary publish (`C:\Program Files\backdafuckup Agent\publish\…`). Give the name and new paths when you restore it: `RESTORE DATABASE [shop] FROM DISK = N'…' WITH MOVE N'shop' TO N'D:\SQL\shop.mdf', MOVE N'shop_log' TO N'L:\Log\shop_log.ldf'` (`RESTORE FILELISTONLY` lists the logical names), or in SSMS on the *Files* page with *Relocate all files to folder*. The backup set is named after the original database.
- **Publish:** the agent in a target VM mounts the backup's volumes over a local iSCSI connection (nothing is copied) and attaches the database. Writes go to a temporary overlay in the VM. *Unpublish* (stopping the session), or the chosen time running out, removes the database, the mounts and the overlay. The agent does no other restore work while it publishes.
- **Restore as a new database:** the agent writes the files under the new database's name into the chosen folders, checks each one against the backup (SHA-256) and attaches them. Existing files are never replaced: if a file exists, the restore stops and deletes what it wrote.
- **Overwrite the original database:**
  1. The files are written beside the database's own (`*.bdfu-new`) and checked.
  2. The database goes offline (`SET OFFLINE WITH ROLLBACK IMMEDIATE`: open connections are dropped and their transactions rolled back).
  3. Its files are moved aside (`*.bdfu-replaced`), the restored ones are put in their place, and it goes online again.
  4. The previous files are deleted afterwards, unless *Keep the previous files* is chosen.

  The database itself is never dropped: owner, users, TRUSTWORTHY and Service Broker stay as they are. If it does not come online with the restored files, it gets its own files back and goes online again. Everything written since the restore point is lost.

**Rules and limits of overwrite**
- Only the original database: the same VM, instance and name the backup was taken of. SQL Server takes any database's files when it goes online, so nothing else is offered.
- The database must have exactly the files the backup has. If files were added, removed or moved since, restore it as a new database instead.
- The account must be `sysadmin`. The restored files need the database's space once more until the previous ones are deleted.
- Not possible for system databases, database snapshots (or databases that have snapshots), mirrored or replicated databases, availability groups, and databases with FILESTREAM or full-text files.
- If the agent is interrupted during the swap (crash, service or VM restart), it puts the database's own files back when it starts again, from its journal. The server notices the restart, asks the agent how that ended, brings the database online again with the session's account and deletes the restored copies; the session fails with "nothing was overwritten". If the database was already online with the restored files, the overwrite stands and the session ends with a warning.

**Interruptions** (all ways of getting a database back)
- **Agent restart** (service restart, crash, VM reboot): the agent sends a new run id with its next poll, and the server ends what that agent was doing at once instead of waiting for a timeout:
  - while files are written: the files written so far are deleted once the agent is back (up to 20 minutes, `SqlRestore:LeftoverWaitLimit`); a file the agent finished without reporting it is deleted only if its content is the restored file's. If the agent does not come back, the session lists the files. A partly written temporary file (`.bdfu-restore-*.tmp`, also from a file restore) is deleted by the agent when it starts again.
  - during the attach: the server checks whether the database is attached with the restored files; if not, it deletes them.
  - during a publish or an export: the agent removes the mounts and the iSCSI session from its journal when it starts; the server then removes a database left attached with the session's account.
  - a database a publish left attached after its session had ended (for example the server was restarted while the agent could not drop it): the agent reports it with each poll, and the server removes it with the account of that session and notes it in the session's log (tried again every 10 minutes). A publish whose session no longer exists is named in the server log; remove that database in SQL Server.
- **Server restart:** publishes and exports end, and the agent removes what it set up when it gets 404. A restore session fails with a summary that names the files it was writing and what to check (whether the database is attached or online). The agent finishes or rolls back an overwrite on its own. For a restore as a new database, the server checks the target once its agent is reachable (from 5 minutes after the start, for up to a day, `SqlRestore:CleanupStartDelay` / `CleanupGiveUpAfter`): if the database is attached with the restored files, the session becomes a warning (only the session was interrupted); otherwise the files the restore wrote are deleted, except any a database uses. The session's log says what happened.
- **Stop:** an attach, an overwrite or the backup of an export cannot be stopped halfway. A stop during one of them lets it finish; the session then shows the real result and says so in the log.
- An account that a job or a running session uses cannot be deleted under *Credentials*; the message names the jobs. A job with the SQL Server snapshot but no account (from before this check) falls back to VSS with a warning.

**Accounts**
- A Windows account (the agent logs on with it in the guest; `.\name` for local accounts works) or a SQL Server login, stored under *Credentials*. The agent never stores the account.
- Publish and restore as a new database need `dbcreator` or `sysadmin`; overwrite and export .bak need `sysadmin`.
- With `dbcreator` only, the account attaches the database but cannot open it afterwards: SQL Server makes it the owner, but the database's `dbo` user keeps the original owner (e.g. `sa`), and a login without access cannot take ownership either. Sysadmins (and the original owner) can open it. The dialog and the session log say so; the log names the `ALTER AUTHORIZATION ON DATABASE::[name] TO [account]` a sysadmin runs to give it to the account. The users in the database without a login are then not listed (they can only be read inside it).

**What an attach changes** (publish, restore as a new database): the attaching account becomes the owner, TRUSTWORTHY is off, Service Broker is off. Database users whose login does not exist on the target instance are listed in the session log, with the `ALTER USER … WITH LOGIN` to fix them. SQL Server sets the files' permissions itself (its service, Administrators).

**Not yet:** restore of system databases (restore them with the VM, or from their `.bak`), In-Memory OLTP and FILESTREAM databases.

### Log backups and point-in-time restore

A job backs up the transaction logs of the SQL Server databases in its VMs when *SQL Server backups* is on in its guest processing settings. The backup server runs the backups: at each due time it sends the agent a job with the job's account. The agent runs `BACKUP LOG` (or `BACKUP DATABASE`), sends the files to the repository (deduplicated and compressed like disk data) and deletes them in the VM once the server has committed them. No password is stored in the VM.

**Settings** (job → guest processing → *SQL Server backups*)
- *Log backups every* 5, 15, 30 or 60 minutes, or longer (60–1440 minutes). The times are counted from midnight in the job's time zone: every 15 minutes means :00, :15, :30, :45.
- *Full backups:* native full backups (`BACKUP DATABASE … WITH CHECKSUM`, verified with `RESTORE VERIFYONLY`) on a schedule (daily, or weekly with days and a time). By default only for databases that have no other base: SQL Server before 2022, or jobs with VSS instead of the SQL Server snapshot. *Also for databases whose base is the SQL Server snapshot* takes them for all databases.
- *Start a missing or broken log chain with a full backup at once* (default on): a new database, a database switched from SIMPLE to FULL, or a broken chain gets a full in the same run, plus a warning.
- *System databases:* full backups of `master`, `msdb` and `model` (default daily).
- *Leave out databases*, and the *folder in the VM* for the files until they are sent (default: the instance's backup folder, subfolder `backdafuckup`).
- The account must be `sysadmin` on the instances. It is needed in VSS mode too once SQL Server backups are on.

**What is backed up**
- Databases in FULL or BULK_LOGGED recovery with a log chain get a log backup. The base of the chain is the SQL Server snapshot of the image backup (SQL Server 2022 and later, method *SQL Server snapshot*, backup type *full*), or a native full.
- SIMPLE databases are noted: they can only be restored to the times of their full backups.
- Left out: database snapshots, read-only, offline and restoring databases, and availability group replicas.
- Compression: `COMPRESSION` except on Express, where the repository compresses instead.

**Sessions:** one session per job and day, *SQL Server backups – <job> – <date>*, with one task per VM. Each run adds to it; the session shows the worst result of the day and the summary of the last run. Its page lists the runs (time, state, log and full backups, bytes); a run opens to the lines of each VM, with every database's backup and LSNs. The session log keeps one line per VM and run plus the warnings, and shows the warnings and the last run unless *Show all lines* is chosen. It does not wait for image backups or maintenance, and it has no Stop button. While an image backup suspends SQL Server for its snapshot, a log backup waits (up to 30 minutes, `Agent:SqlSnapshotLockWait`); a suspend also waits for a running log backup.

**The log chain** (page *SQL Server*, chip per database)
- *intact:* a base and an unbroken chain of log backups since then.
- *no full backup:* no base yet. The next run takes a full if *Start … with a full backup at once* is on.
- *broken:* a gap. The reason names it: another tool's log backup (with its time and file), a new fork (a restore over the database), or a log backup the repository no longer has.
- *SIMPLE:* no log backups; restorable to the times of its full backups only. Switched back to FULL, the next run takes a full backup (with a warning) and the chain is *intact* again.
- Warnings in the day session: another tool's full or log backup (named with time and device; copy-only backups do not count), a switch from FULL to SIMPLE (point in time only up to the last log backup), log backups further apart than twice the interval (RPO missed, for example while the server was down; the chain stays intact), the last log backup older than twice the interval, an agent offline or too old at a due run, an agent restart during a run (host and IP; its finished files follow with the next run).
- **Exactly one tool may take log backups.** Turn off the log backups of maintenance plans, Ola Hallengren jobs and other backup products, including log truncation in other image backup products (e.g. "truncate logs" in a Veeam job). Their full backups do no harm, as long as they are copy-only.

**Interruptions:** while the backup server is down, no log backups run; the log grows in the VM, and the chain stays intact (the next run catches up and warns about the RPO). Files whose upload fails stay in the VM (`sql-backups\queue.json` in the agent folder) and are sent first with the next run. A backup interrupted by an agent restart is resolved through `msdb`: a finished one is sent, a partial file is deleted; nothing is reported as backed up that was not.

**Retention:** the point-in-time window of a VM is the window of its restore points. Kept are native and system fulls back to the oldest restore point the job keeps (plus the newest one before it), and log backups back to the oldest kept base. Retention runs once a day per VM; backups in use by a running restore stay until the next run.

**Restore to a point in time** (*SQL Server* → database)
- The timeline shows what the backups cover (green), gaps (with their reason) and the restore points, full and log backups. Choose *latest*, drag the slider, use the jump buttons, or type a time to the second (local time of the browser). The plan below says which base and how many log backups the restore uses, and the time it reaches.
- STOPAT is in SQL Server's local time; the server converts with the time zone SQL Server stores with each backup.
- **Restore as a new database:** the base (the snapshot's files from the restore point, or the native full downloaded into the staging folder), then each log backup, the last one with `STOPAT`, then RECOVERY. Downloads are checked with SHA-256 and deleted after use.
- **Overwrite the original:**
  1. The database is restored under the name `<name>_bdfu_pit` into `*.bdfu-new` beside its own files, then detached.
  2. If the job backs up its logs: the database goes to `SINGLE_USER WITH ROLLBACK IMMEDIATE` and gets a last log backup, so everything up to the swap stays restorable. That backup is sent with the next run.
  3. The existing swap (offline, files exchanged, online), then `MULTI_USER` again.

  The log chain starts anew afterwards; the next run takes a full.
- **Publish:** only when the base is a restore point. The volumes are mounted as in a normal publish, the database is restored from the `.bkm` onto the mounted files and the log backups are applied; everything goes to the overlay and is discarded with *Unpublish*. With a native full as the base, restore it as a new database instead.
- A restore fails before it starts if the time lies in a gap or before the oldest base; the message names the nearest times it can restore to.

**Full backups as .bak:** the database's page lists the native and system database fulls the agent took, newest first, with *Download*. The file comes straight from the repository through a link that works once, for a minute. Restore it anywhere with `RESTORE DATABASE … FROM DISK = N'…' WITH MOVE …`; `RESTORE FILELISTONLY` lists the logical names. System databases (`master`, `msdb`, `model`) are restored this way, by hand (`master` in single-user mode).

**Limits:** no differential backups, no availability groups or failover cluster instances, no resume of a large upload (it starts again), and one SQL backup object per run and instance in the repository (288 per VM and day at 5 minutes).

## Active Directory

For domain controllers backed up with guest processing. AD records each backup, and single objects and group policies come back from a restore point without restoring the VM. The product reads the backup's directory only through Windows' own AD Database Mounting Tool (`dsamain.exe`) and LDAP, and changes the live directory only over LDAP; it never parses or writes a database file itself.

**Backing up a domain controller**
- **Method:** guest processing with **VSS**. With the method *SQL Server snapshot* the directory is only crash-consistent (the snapshot suspends SQL Server only), and the session warns.
- **A consistent copy of the directory first (IFM):**
  - Before the VM snapshot, the agent copies the directory with Windows' own `ntdsutil "activate instance ntds" "ifm" "create full …"`. This takes seconds for a small directory.
  - The copy is `ntds.dit` in clean shutdown state plus the registry hives, in `%ProgramData%\backdafuckup\ad-ifm` (only SYSTEM and Administrators).
  - ntdsutil takes its own shadow copy, so the copy is consistent even when the VSS freeze of the VM snapshot runs out (slow storage, 10 s limit), and AD records it as a backup: `repadmin /showbackup` shows its time, event 1917, no more event 2089.
  - After the VM snapshot the agent deletes the folder; the copy lives on in the restore point. If the agent stops in between, it deletes the folder at its next start.
  - Needs free space for one more copy of `ntds.dit` plus 1 GiB on the system volume, else the backup goes on without it, with a warning.
- **VSS full:** the agent reports the domain role, and a domain controller gets a VSS full backup (`VSS_BT_FULL`), which the NTDS writer records too when the freeze holds. All other VMs stay copy-only.
  - Exception: a domain controller with SQL Server databases in a job with SQL Server log backups stays copy-only, with a warning (SQL Server would record a VSS full as a full backup of every database). Back such a VM up in a job without log backups.
- **The restore point:**
  - It carries an *AD* chip and the facts at backup time: domain, paths of `ntds.dit`, its logs and SYSVOL, invocation id, USN, functional levels, Recycle Bin, tombstone and deleted-object lifetimes.
  - The directory has its own consistency: application-consistent with the IFM copy (also in a crash-consistent restore point), otherwise from the NTDS writer. A crash-consistent directory is a warning; the recovery on the domain controller replays its logs.
  - Restore points from before this version are marked by the server when it starts, from their guest metadata.

**Opening a backup's directory** (*Backups & Restore → Restore → Active Directory objects*)
- **Choose:**
  - a domain controller of the same domain with the agent, online and with the AD DS tools (`dsamain.exe`). Best is the DC the backup is from (listed first and marked): on a DC with a VM generation id, dsamain does not open the backup of another DC that has none (events 2168, 2173, 1001), and the session says so;
  - a domain admin of the domain (member of Domain Admins or Enterprise Admins; under *Credentials*). The explore checks it when it opens and refuses another account (see *The account* below).
- **The database and its logs are copied into a work folder on that DC:**
  - The IFM copy's `ntds.dit` if the restore point has one, else the live `ntds.dit` with `edb.chk` and `edb*.log`, go into `%ProgramData%\backdafuckup\ad\<session>`. Only SYSTEM and Administrators can open the folder.
  - The copy goes through the agent, each file checked with SHA-256.
  - Free space needed: the files plus 10 %, at least 1 GiB more.
- **The copy is served there:**
  - `esentutl /r` recovers the copy.
  - `dsamain` serves it read-only over LDAP on four free ports from 50000 (LDAP, LDAPS, GC, GC-SSL).
  - dsamain listens on all interfaces. It gives data only to Domain Admins and Enterprise Admins; any other account gets the rootDSE and then `operationsError` (1, `000004DC`) on every search. Block 50000–50999 in the Windows Firewall if other machines could reach the DC on them.
- **The DC's Windows version must be the same or newer:** dsamain cannot open a database from a newer Windows (e.g. Windows Server 2025's database on a 2022 DC).
- **The copy holds every password hash of the domain.** It is deleted:
  - when the session is closed (*Close*, or *Stop* on the Sessions page);
  - after 60 minutes without use (`AdExplore:IdleTimeout`; the agent itself after 75);
  - when the backup server restarts (as soon as the DC's agent is reachable);
  - when the agent starts again (from its journal `ad-mounts.json`).

  A folder that cannot be deleted is an error in the agent log with its path: delete it by hand. At most 4 sessions are open at a time (`AdExplore:MaxOpen`).
- **Audit:**
  - The session *AD explore* logs who opened what, the copied files, every object someone viewed and the closing.
  - It stays open when the page is left. The page continues an open session of the same restore point.

**Browsing and comparing**
- **States:** each object of the backup gets its state in the live directory, matched by objectGUID (deleted objects too):
  - *unchanged*;
  - *changed*;
  - *deleted* (in Deleted Objects: with the Recycle Bin with all attributes, without it a tombstone);
  - *recycled* (Recycle Bin, past the deleted-object lifetime);
  - *gone* (removed after the tombstone lifetime).
- **What counts as a change:**
  - Attributes the DCs maintain themselves (logons, USNs, replication data) do not.
  - Links (e.g. `manager`, `member`) are compared by the objectGUID they point to.
  - A move shows as a different parent (`parentGUID`); renaming an OU changes nothing below it.
- **The search** goes page by page through the backup. By default it shows what was deleted or is gone since the backup; *Search further* continues.
- **The object view** shows every attribute in the backup and live, the differences, the group memberships, and for each attribute whether a restore can write it and why not.

**Restoring objects**
- **How:**
  1. Choose objects (or attributes in the object view).
  2. The preview lists every write with its DN and old and new values; nothing is written yet.
  3. Type the live domain to confirm.

  The restore runs as the session *AD restore*, with one task per object and every write in its log.
- **Deleted, Recycle Bin on:** reanimated with its objectGUID and SID; attributes, links and the password come back with it.
- **Deleted without Recycle Bin (tombstone), or recycled:**
  1. Reanimated with its objectGUID and SID.
  2. The attributes the tombstone lost are written from the backup.
  3. It is added back to its groups.
  4. Accounts: with a password entered in the dialog, the password is set and the account enabled as in the backup. Otherwise it stays disabled and must change the password at the next logon.
- **Gone:** with *Re-create objects that are gone*, created anew from the backup, with its attributes and memberships.
  - It has a **new objectGUID and SID**: permissions on other objects, SID-based assignments and references from other domains do not follow (the preview and the log say so).
  - Accounts as above. A re-created computer must join the domain again.
- **Changed:**
  - The chosen attributes are written back. Links are mapped to the live objects by GUID; a value whose object does not exist live is skipped with a warning.
  - *Move moved objects back* moves and renames an object into its old place.
- **Order:**
  - Parents come first, and links can point to objects restored in the same run.
  - An object whose container is not live (and not restored with it) is skipped, or goes into the container given in the dialog.
- **Passwords:** entered in the dialog only. They travel in memory and in the agent's job, are written as `unicodePwd` over a signed and sealed LDAP connection, and are never stored or logged.
- **Never:**
  - no live object is deleted;
  - no passwords or SID history from the backup;
  - no security descriptors of objects (except those of group policies);
  - no links with binary data (e.g. `msDS-KeyCredentialLink`);
  - nothing in the configuration partition (sites), in DNS zones or in other domains of the forest.
- **The account:** a member of Domain Admins (or Enterprise Admins).
  - dsamain serves the backup's directory only to them. The agent does not start it with `/allowNonAdminAccess`: that would let every account of the domain that reaches the port read the backup while the explore is open.
  - A delegated account would also not see the tombstones: only Administrators may list `CN=Deleted Objects`, so a deleted object would look *gone* to it.
  - A restore writes as this account, so the domain's own permissions and auditing apply ("Reanimate Tombstones", write and create the objects, reset passwords, write SYSVOL); domain admins have all of these.

**Restoring group policies** (tab *Group policies*)
1. A staging folder `<SYSVOL>\<domain>\Policies\{GUID}.bdfu-new` is created in the DC's SYSVOL, with SYSVOL's permissions.
2. The backup's files of the policy (`SYSVOL\domain\Policies\{GUID}` in the restore point) are copied into it with their permissions, each checked with SHA-256.
3. The folders are swapped. The current one stays as `{GUID}.bdfu-replaced` until the restore is done, and comes back if it fails.
4. The container (GPC) is restored like an object. A gone policy is re-created under the same `{GUID}`, with its `User` and `Machine` containers.
5. The version goes up in `GPT.INI` and `versionNumber`: user and computer half each to max(live, backup) + 1. Clients then apply the policy again, and DFS-R replicates it.
6. The DACL comes from the backup.
7. With *Add the links back*, the links the backup had on the domain and the OUs are added where they are missing (not on sites).

An interrupted restore is cleaned up when the agent starts:
- a staging folder is deleted;
- a set-aside folder goes back if the policy has no folder, otherwise it stays and the agent log names it.

Only folders named `{GUID}` or `{GUID}.bdfu-*` directly in `Policies` are ever touched. WMI filters are not restored.

**Restoring a whole domain controller** (VM restore)
- **New VM generation id:** XCP-ng gives a restored VM a new VM generation id. At the first start, Windows Server 2012 and later then restore AD non-authoritatively: a new invocation id, the RID pool dropped, and the newer changes taken from the other DCs.
  - The restore session logs the old and the new id.
  - If the id is missing or unchanged, it warns: start the VM without network and handle it by hand (USN rollback risk).
  - **Only a VM with a generation id gets a new one.** A VM created from a template without one has an empty `generation_id` (`xe vm-param-get uuid=<vm> param-name=generation_id`), which XAPI never fills in later, and its restored copies get none either. On such a DC `msDS-GenerationId` of its computer object stays empty. Every backup of such a DC ends with a warning, and the restore wizard and session warn. Create domain controllers from a template with a VM generation id (XCP-ng's Windows templates have one).
- **A copy beside the original:** a copy at another location has the same name, addresses and computer account as the original. Connect it only to an isolated network; the wizard and the session warn when it is powered on.
- **Not done by the product:**
  - Authoritative restores (e.g. a deleted OU with everything below it on all DCs, without object restore) are done with `ntdsutil` in Directory Services Restore Mode on a DC restored to an isolated network.
  - Recovering a whole forest follows Microsoft's *AD Forest Recovery* guide.

## API and API tokens

The console's API is described under *API reference* (`/api-docs`). Scripts and tools use an **API token** instead of a password:

- **Create** one under *API tokens*, signed in with your password: a name (what uses it) and when it expires (30 or 90 days, a year, or never). The token (`bdfu_…`) is shown once; the server keeps only its SHA-256, so copy it then.
- **Use** it as `Authorization: Bearer bdfu_…`. The request acts as the token's user, with that user's rights. Requests with a token need no `X-BDFU-Request` header (that header protects the console's cookie, which a browser sends on its own; a token it never sends on its own).
- **Limits:** a token cannot create tokens. A deleted or expired token, or one whose user is locked out, gets 401 at once. The list shows when and from which address each token was used last.
- **Example:** `curl -X POST -H "Authorization: Bearer bdfu_…" -H "Content-Type: application/json" -d '{}' https://<server>:9555/api/jobs/<job id>/run` starts a job.
- Creating and deleting a token is noted in the server log (name, prefix, user).

## Data and backups of the server itself

| Path (Linux / Windows) | Content |
|---|---|
| `/var/lib/backdafuckup` / `%ProgramData%\backdafuckup` | Encryption key ring (`keys/`), HTTPS certificate, setup token |
| `/var/log/backdafuckup` | Log files (30 days) |
| PostgreSQL | Catalog |
| Repository directories | Backup data (self-describing) |

- **Key ring:** back up the `keys/` folder. Without it, the stored pool passwords must be re-entered.
- **Repositories:** they are self-describing. After a loss of the server or the database, add the repository path again on a new installation. The existing restore points are imported automatically.

## Session statistics

Every backup session shows live statistics for the session, for each VM and for each disk. They are kept in the history. Each disk also writes a summary line to the session log.

| Metric | Meaning |
|---|---|
| Processed | The part of the disks that was dealt with. It splits into the four categories below, which together add up to Processed. |
| Unchanged | Not processed, because change tracking reports no change since the previous restore point. |
| Empty | All-zero data. It is not stored. |
| Already stored | Data that already was in the repository, from this backup, an earlier one or another VM. |
| New | New data before compression. |
| Read | Bytes received from the source. With NBD incrementals this is only the changed data. With the HTTP export it is the whole allocated disk. |
| Transferred | Bytes written to the repository, after compression. |
| Processing rate | Processed divided by the transfer time. For the session, the session run time is used instead. |
| Dedupe | (Already stored + New) / New |
| Compression | New / Transferred |
| Bottleneck | The busiest stage, with the share of time it was busy. **Source** is reading from the hypervisor, **Processing** is hashing and compression, **Target** is writing to the repository. |

## Lab verification

The opt-in lab tests run against a real pool. Use a test VM; they create and delete snapshots and a restored copy of the VM.

```bash
export BDFU_LAB_HOST=xcp-host.example.local       # pool master or member
export BDFU_LAB_USER=root
export BDFU_LAB_PASSWORD='...'
export BDFU_LAB_VM=<uuid of a small, running test VM>
export BDFU_LAB_TARGET_SR=<uuid of an SR for the restored copy>
export BDFU_LAB_WAIT_SECONDS=120                   # time for the VM to change some blocks
export BDFU_LAB_TRANSPORT=Automatic                # or NbdOnly / HttpExportOnly
BDFU_LAB_RUN=1 dotnet test --project tests/Backdafuckup.Lab.Tests --filter-trait "Category=Lab"
```

The lab tests are skipped unless `BDFU_LAB_RUN=1` is set for the run, so that a solution-wide `dotnet test` never touches a real pool.

Instead of exporting the variables, you can put the same `KEY=VALUE` lines into `~/.config/backdafuckup/lab.env` (or the file named by `BDFU_LAB_ENV_FILE`). Then the password is not on the command line or in the shell history; protect the file with `chmod 600`. Environment variables take precedence over the file.

To run a single test, add `--filter-method "*Cbt_bit_order_spike"`; add `--output detailed` to see the test output (progress, results) also for passed tests. With the HTTP export, each test reads the whole allocated disk once or twice, so a small VM keeps the run short. If XAPI still reports a snapshot disk in use after the backup, the tests wait for it like the service and print how long it took.

- **`Cbt_bit_order_spike`:** takes two snapshots and compares the real block differences with the CBT bitmap: it fails if incremental backups would miss a changed block, and prints the bit order the pool uses. Make sure the VM writes some data during the wait, e.g. `dd if=/dev/urandom of=/tmp/x bs=1M count=50; sync` (Linux) or `$b = New-Object byte[] 200MB; (New-Object Random).NextBytes($b); [IO.File]::WriteAllBytes("$env:TEMP\bdfu-cbt.bin", $b)` (Windows PowerShell).
- **`Backup_and_restore_roundtrip`:** backs up the VM with the selected transport into a temporary repository, restores it as `<name>-bdfu-labtest` to the target SR, and compares SHA-256 hashes of the restore point and the restored disks (read back with a raw HTTP export).
- **`Http_export_vhd_matches_raw`:** exports a snapshot of the VM as VHD (parsed by backdafuckup) and as raw, and checks that both give the same data. It works without NBD.

The manual checks of file-level restore (downloads, shares, Windows and Linux VMs) are in the [lab runbook](lab-runbook.md).

## Required XAPI permissions

The connection account needs to create and delete snapshots, enable CBT, use NBD or the HTTP export, create VDIs and import VMs. With RBAC, use **Pool Admin** (simplest) or **Pool Operator**. Enabling the NBD network purpose requires Pool Admin.
