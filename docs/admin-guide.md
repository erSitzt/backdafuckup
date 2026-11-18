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
| Kubernetes (optional) | Clusters with a CSI driver that takes volume snapshots, and a container registry for the mover image. See [Kubernetes namespaces](#kubernetes-namespaces). |
| VMware vSphere (optional) | vCenter Server 7.0 or later and a Linux backup proxy VM. See [VMware vSphere](#vmware-vsphere). |

### Network ports

| From → to | Port | Used for |
|---|---|---|
| Browser → backup server | TCP 9555 (HTTPS) | Web console |
| Prometheus → backup server | TCP 9555 (HTTPS) | Metrics at `/metrics` (with a metrics token) |
| Guest agent in the VM → backup server | TCP 9555 (HTTPS) | Agent connection, self-update, file restore into Windows VMs. Outbound from the VM only. |
| Backup server → XCP-ng hosts | TCP 443 (HTTPS) | XAPI, HTTP export, restore upload |
| Backup server → XCP-ng hosts on the NBD network | TCP 10809 (TLS) | NBD disk reads |
| Backup server → vCenter | TCP 443 (HTTPS) | vSphere API |
| Backup server → ESXi hosts | TCP 443 (HTTPS) | Installing the agent through VMware Tools (file copy into the guest) |
| Backup proxy → backup server | TCP 9555 (HTTPS, WebSockets) | vSphere disk data and the proxy's control connection. Outbound from the proxy only. |
| Backup server → Windows VMs | TCP 445 (SMB) | Push installation of the agent |
| Backup server → file servers, domain controllers, DFS namespace servers | TCP 445 (SMB) | File restore to a network share, DFS referrals |
| Backup server → Linux VMs | TCP 22 (SSH) | File restore into Linux VMs |
| Backup server → PostgreSQL | TCP 5432 | Catalog (usually on the same machine) |
| Backup server → Kubernetes API servers | TCP 6443 (HTTPS) | Inventory, objects, snapshots, mover pods, hooks (exec) |
| Mover pods in a cluster → backup server | TCP 9555 (HTTPS) | Volume data of backups and restores, files of a file restore into a PVC. Outbound from the pods only. |
| Cluster agent → backup server | TCP 9555 (HTTPS, WebSockets) | For clusters reached through their agent: the control connection and one WebSocket per API connection. Outbound from the cluster only; then the server needs no route to the API server. |
| Backup server → container registry | HTTPS (the registry's port) | Pushing the mover image |
| Cluster nodes → container registry | HTTPS (the registry's port) | Pulling the mover image |

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
- **Snapshot with memory** (per VM, the switch *With memory* in the job's VM list): the VM is backed up with `VM.checkpoint`.
  - **How it works:** XAPI pauses the VM, writes its RAM to a memory image (suspend VDI) on the VM's suspend storage, else the pool's, else the suspend storage of the host it runs on, captures all its disks and resumes it.
  - **What you get:** the disks match each other, and the memory image goes into the restore point (chip *RAM*). A restore can resume the VM where it was (see *Resume with memory* below), or boot it cold, which is like a power failure of the whole VM at one moment.
  - **Suspend storage:** the host's suspend storage is often its local disk. In the lab, 8 GiB RAM written to a local disk paused the VM for over 5 minutes, to a shared iSCSI storage (HPE Alletra) for 2 minutes. Give the VM (`xe vm-param-set uuid=<vm> suspend-SR-uuid=<sr>`) or the pool (`xe pool-param-set uuid=<pool> suspend-image-SR=<sr>`) a fast shared storage as suspend storage; the session log says when the image goes to a local storage.
  - **Costs:**
    - the VM pauses for the RAM write plus every disk; the session log names the pause, and the metrics `bdfu_snapshot_duration_seconds{kind="checkpoint"}`;
    - RAM-sized space on the suspend storage during the backup (checked like the free space above);
    - the compressed RAM image in every restore point, read in full each time.
  - **The guest's clock** stops during the pause. Windows does not always set it again afterwards (in the lab once yes, twice no), so it stays behind until its next time sync. A domain member more than 5 minutes off fails Kerberos: give such VMs a working time source (the domain hierarchy or NTP) and keep the pause short.
  - **Limits:**
    - `[NOBAK]` disks are captured too: they are not backed up, and their snapshots are removed;
    - such VMs get no guest processing (VSS), so their restore points have no SQL Server or Active Directory details. VSS cannot hold the writes through the pause: in the lab the freeze ran out and SQL Server's writer aborted its backups;
    - a VM that is not running gets a normal snapshot.
  - **Cleanup:** the memory image and the checkpoint are removed after the backup like any snapshot.

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

If NBD connections time out ("no answer to the TCP connection"), the packets are dropped on the way, or the host's answers never come back. Check two things:
- **Firewall:** a firewall between the backup server and the hosts, or the hosts' iptables, may block TCP 10809.
- **Route back:** the hosts' interfaces in the NBD network need a route back to the backup server. Either they have a gateway, or the backup server has an address in the same subnet. An NBD network without a gateway, which no backup server is connected to directly, accepts no connections from outside it.

If NBD connections fail with "Connection refused", run these commands on a host. They show which network has the NBD purpose, which IP each host has on it, and whether `xapi-nbd` listens:

```bash
xe network-list params=uuid,name-label,purpose
xe pif-list params=host-name-label,network-name-label,IP
systemctl status xapi-nbd
ss -ltnp | grep 10809
```

### Changed block tracking (CBT)

backdafuckup enables CBT on every disk it backs up. While CBT is enabled on a disk, **storage live migration** of that disk is not possible. When a disk is reset, for example after a host crash, the next backup reads it in full.

**Migrations and host updates.** On XCP-ng a migration of a VM can reset its CBT, also within the pool and on the same storage, for example when hosts are evacuated for a rolling pool update. The previous backup's tracking data then no longer leads to the new snapshot. XAPI answers `SR_BACKEND_FAILURE_460 … Source and target VDI are unrelated`, and the backup reads the VM's disks in full, with a warning. The backups after it are incremental again. Another backup product with CBT on the same disks does not break the chain by itself: each product keeps its own tracking data.

When the chain of a VM breaks, the session log shows two kinds of warning:
- **Per disk:** XAPI's answer and the state of the chain. That is the previous backup's tracking data, this backup's snapshot and the disk, each with its type, format, storage, CBT flag and the time of the snapshot.
- **Once per VM:** what XAPI recorded since the previous backup's snapshot, found with `message.get` (VM_MIGRATED) and the hosts' `other_config:boot_time`. That is the VM's migrations (from which host to which, when) and the hosts' restarts. For example:

  ```
  Changed block tracking of app01: since the previous backup's snapshot (2026-11-06 15:01 UTC) XAPI recorded:
  host xcp-host-1 was restarted at 2026-11-07 08:50 UTC; app01 was migrated from host xcp-host-2 to host xcp-host-1 at 2026-11-07 08:55 UTC; …
  ```

  When XAPI recorded neither, the line says so and names the other causes: the disk moved to another storage, or CBT was turned off and on again. XAPI keeps only a limited number of messages, so on a busy pool older migrations may be missing.

Plan for one full read of the migrated VMs after each host update. With the HTTP export every backup reads the whole allocated disk anyway, so the time stays the same; with NBD the run after the update takes as long as a full backup.

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
- **Consistency:** the SQL Server databases are application-consistent; files outside SQL Server are crash-consistent. The restore point shows *crash-consistent* (grey, not a warning) and a green *SQL-consistent* when every database was recorded; the SQL browser shows each database's own state.
  - The session ends with a warning if a database of a VM with the SQL Server snapshot could not be suspended (offline, missing rights).
  - System databases (`master`, `model`, `msdb`) cannot be suspended; restore them from a native SQL Server backup.
- **Writes wait as long as the snapshot takes.** On the shared LVM storage of the lab that was 25–47 s for three or four disks. Applications with the usual 30 s command timeout may see timeouts; a write whose client gave up is still committed afterwards. Keep the snapshot short with `[NOBAK]` and one snapshot per storage.
- **Account:** a Windows account or a SQL Server login that is `sysadmin` on the instances. Windows accounts log on in the guest; the agent never stores the account.
- **Edition:** works with SQL Server Express too (tested with SQL Server 2025 Express and 2022 Developer).
- The SQL Server service account writes the `.bkm` into the instance's backup folder (registry `BackupDirectory`, else `InstanceDefaultBackupPath`).

Restore points show their consistency under *Backups & Restore*:
- *application-consistent* (green): the guest held its writes for the snapshot.
- *crash-consistent* (grey): like after a power failure, what a backup without guest processing, or a SQL Server snapshot for the files outside SQL Server, promises.
- *crash-consistent* (orange, with the reason as its tooltip): less than the job promised: the guest could not be frozen, or the disks were captured one after another. The filter *Crash-consistent with a warning* finds them.
- *SQL-consistent* (green) when every SQL Server user database in the restore point is application-consistent (VSS or a SQL Server snapshot); *SQL 2/3 consistent* (orange) when some are not; *SQL 3* for restore points whose databases are not checked yet (older ones are checked once after the server starts). The system databases (master, model, msdb) do not count: a SQL Server snapshot cannot suspend them, so they are crash-consistent there by design, and SQL Server recovers them at start; the SQL browser shows their state.
- *RAM* for a snapshot with memory, and *AD* for a domain controller.

**VMs with two or more disks:** XAPI snapshots the disks of a VM one after another, each paused on its own, so they are captured seconds apart (about 8–12 s per disk on shared LVM storage). Unless the guest held its writes for the whole snapshot (application-consistent), such a restore point is not consistent across its disks, not even like a power failure. Data that spans disks may not match after a restore, for example a database and its log, or a volume over several disks. The restore point then says "The N disks were captured one after another over X s" and counts as crash-consistent with a warning. The session log names the gap, and the metrics count it as `bdfu_snapshot_disk_skew_seconds`. The result of the session does not change.

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

## Repositories

A repository is a directory on a volume mounted on the backup server. It describes itself (`repo.json`), so it can be added to another installation and its restore points are imported again.

### Repository format and pack files

New repositories keep their chunks (the deduplicated, compressed pieces of disks, file trees and SQL Server backups) in **pack files** of 64 MiB (format 2). Repositories made by versions before pack files keep one file per chunk (format 1). About 20 million chunk files per 10 TB make garbage collection, verify, copies and tape slow; packs bring that down to a few hundred thousand files.

| Path in the repository | Content |
|---|---|
| `packs/<hh>/<id>.pack` | Chunks, written once. Each pack ends with a list of its chunks, so the index can always be rebuilt from the packs. |
| `index/` | Which chunk is in which pack and where. Rebuilt from the packs when it is damaged or missing. |
| `incoming/` | Packs being written; removed when the repository opens after a crash (nothing refers to them yet). |
| `chunks/` | Format 1: one file per chunk. In a converted repository, chunks that are not packed yet. |
| `restore-points/`, `sql-backups/` | Manifests and indexes, as in format 1. |

- **Pack size:** chosen when the repository is added (*Pack file size*, 16–512 MiB, `PackSizeMiB` in the API). 64 MiB suits most repositories; 256 MiB means fewer files for repositories that are copied to tape or network storage.
- **Memory:** the index keeps about 1.6 bytes per chunk in memory (about 32 MiB for 20 million chunks). A garbage collection needs about 16 bytes per referenced chunk while it runs.
- **Older versions:** a repository in format 2 cannot be opened by a version of backdafuckup that does not know pack files. That version refuses it at open ("Repository format version 2 … is not supported") and changes nothing in it.

**Converting an older repository.** *Backup Infrastructure → Repositories → ⋮ → Convert to pack files…* (`POST /api/repositories/{id}/packs`) starts a maintenance session:
1. It switches the repository to format 2. New backups write packs from then on and reuse the existing chunks.
2. It runs a garbage collection.
3. It moves the chunks that restore points and SQL backups use into packs, newest restore points first, so that each restore point's chunks lie together. A chunk file is deleted only after its packed copy is on disk.

One run packs for at most 4 hours (`Engine:PackLooseMaxDuration`) and queues the next run when it is not finished; backups go on meanwhile. The repository's *Storage* column says "packing old chunks" until it is done. There is no way back to format 1. Convert a copy of the repository first when it matters.

**Garbage collection with packs.**
- It deletes packs that hold no chunk in use.
- It repacks packs where at least 20 % of the bytes are no longer used (`Engine:RepackUnusedPercent`): the chunks still in use are copied into a new pack, then the old pack is deleted. At most 256 GiB are copied per run (`Engine:RepackMaxBytesPerRun`); the rest waits for the next run.
- It combines small packs (left by frequent SQL log backups).
- The session log and `bdfu_repository_unused_bytes` show what stays unused until a later run. Restores and backups keep running while it works.

**Damage and recovery.** The server log (and Loki) has a warning for each problem found when a repository is opened:
- a damaged index, which is rebuilt from the packs;
- packs that the index did not cover yet, which are indexed (after a crash);
- a pack whose list of chunks is damaged. Its chunks are not used, and the pack is kept for inspection: no data is deleted for this reason;
- packs that the index names but that are gone. Their chunks are missing.

A verify of the restore points names the chunks that are damaged or missing, with the pack file and the offset.

### Scale-out repositories

Several directory repositories on different volumes (**extents**) form one **scale-out repository**. Jobs write to the scale-out repository; it decides on which extent each chain lives.

- **A chain** is one job's restore points of one VM, Kubernetes namespace or cluster scope, together with that VM's SQL Server backups. It lives on one extent: an incremental reuses the chunks of its previous restore point, and chunks are deduplicated only within one extent.
- **New chains** go to the active extent with the most room. Room is its free space minus what running backups reserve (their expected size), minus a share kept free (*Keep free for new chains*, 5 % by default). With less than 2 % between two extents, the one with fewer running backups wins.
- **Restore points keep their ids** wherever they move. Restores, verify, tape jobs and file-level restore read each restore point from the extent it is on.

**Making one.** *Backup Infrastructure → Repositories → ⋮ → Add extent (makes it a scale-out repository)…* (`POST /api/repositories/{id}/extents`):
- The repository becomes the first extent of a new scale-out repository, keeping its name.
- Its jobs write to the scale-out from then on, and its chains stay where they are.
- More extents are added the same way on the scale-out repository.
- **Rules for extents:** every extent of one scale-out uses the same block size. Two extents on the same volume are refused, or free space would count twice.
- **An existing directory:** it is imported as the extent, with its restore points.

**Extent states**

| Extent | New chains | Next backup of a chain on it | Restores |
|---|---|---|---|
| Active | yes | incremental there; with little space too (a rebalance relieves it) | yes |
| Sealed | no | a new full on another extent; the older points stay until retention removes them | yes |
| Evacuating | no | incremental there until the move switches the chain | yes |
| Maintenance | no | a new full on another extent (or the VM is skipped, see below) | no |
| Offline (directory unreachable) | no | as in maintenance | no |

- **Seal** an extent that should no longer grow (*⋮ → Seal*).
- **Maintenance** closes an extent for work on its volume. Nothing is read or written there until it ends; it closes once the restores, file browsers and backups using it have finished (`PUT /api/repositories/{id}/extent`).
- **Unavailable extent** (maintenance or offline): with *Chain on an extent in maintenance or offline: new full on another extent* switched off, such VMs are skipped with a warning instead.
- **SQL Server log backups** of such a VM go to another extent for that run only.

**Moving chains.** *Repositories → the scale-out → Chains → ⋮ → Move to …* (`POST /api/repositories/{id}/moves`) copies a chain to another extent and then switches it:
1. **Measuring and copying:** first it measures what the target lacks: every chunk of the chain, also those it shares with other chains (dedupe), so a chain whose own new data is small can still need many GB. A move that would leave the target below *Keep free for new chains* is refused with both numbers. Then its restore points are copied oldest first, while backups and restores go on. Only chunks the target does not have yet are read, and they are stored as they are, checked.
2. **SQL Server backups:** they follow one by one.
3. **The switch:** it waits for a running backup of that chain (*Wait for its backup*, 6 hours), copies what that backup wrote, and switches the restore points to the target in one step. Every next backup of the chain is an incremental on the target.
4. **Deleting the source copies:** only once nobody reads them any more. A file browser or a published database keeps its copy until it is closed.
5. **Garbage collection** then frees the space on the source: about the chain's own new data; chunks other chains share stay.

The session shows one task per chain with its progress. A move that is interrupted (service restart) leaves its copies for the next move of the chain; nothing is lost or counted twice. *Move at most MB/s* keeps moves from slowing backups on the same disks.

**Evacuating** (*⋮ → Evacuate*, `POST /api/repositories/{id}/evacuate`) moves every chain off an extent to the active extents with the most room:
- **Points without a job** (brought back from tape, of deleted jobs) are moved one by one.
- **When it is done,** the extent is empty and sealed, and can be removed (*⋮ → Remove extent*).
- **Removal is refused** while the extent holds anything or is the last extent.
- **On disk:** the directory itself is kept.

**Rebalancing**
- *Rebalance…* shows a plan first: which chains move where, what each takes on its target (measured, shared chunks included) and each extent's free space before and after (the source gets back about the chain's own new data). *Move* runs it.
- **What it changes:** it moves chains from the fullest active extent to the one with the most room, choosing the chain after whose move the fuller of the two is emptiest, and of equal ones the chain that copies least. It stops when the extents are within the limits.
- **Chains it leaves alone:** those that are backing up, were moved recently (*Rest after a move*, 7 days), or are due within the hour.
- **Automatic rebalance** runs in the window (days, from–to, time zone) when an active extent has less than *Low free space* or the extents differ by more than *Imbalance* percentage points of use. *At most GiB per run* caps a run; a run that reaches the end of the window pauses and continues in the next one.

**Limits**
- Deduplication works within an extent, not across extents. VMs that share much data (clones of one template) deduplicate best on one extent.
- Extents are directories for now; SMB, NFS and S3 extents come with those repository types.
- **A lost database:** add each extent's directory as a repository again, then add them as extents of a new scale-out repository. Their restore points are imported with their ids.

## Backups & Restore

The page lists every VM and Kubernetes namespace with restore points once, with its newest restore point. Expanding one loads all its restore points (Restore, Verify, Delete); its *Restore* button restores from its newest one.

- **Kinds:** each kind of object has an icon and a color, before every name and as a stripe on its job's block: blue for VMs, teal for Kubernetes namespaces. A job's block also names its source (*pool …* or *cluster …*). With more than one kind, a row above the filters shows the counts and filters by kind (`/backups?kind=vm`, `kind=k8s`). *History*, *Home* and the session page mark sessions the same way and say *Namespace backup* or *Namespace restore*.

- **By job** (the default): one block per job, with its schedule and how many of its VMs missed a run, have no restore point, or are crash-consistent with a warning.
  - A VM in two jobs is listed under both, each with its own restore points.
  - VMs that are in a job but have no restore point of it yet (just added, or every run failed for them) are listed with *no restore point*.
  - Restore points of deleted jobs are listed last under *Without job*.
- **By VM** (*By VM or namespace* once there are namespaces): one row per VM or namespace with all its jobs.
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
- **Restore points from before a replace:** the catalog records which VM replaced which. A later restore to the original location from an older restore point treats the VM in use now as the original (the session log says so), not the renamed one.
- **A running VM with the same name** that is not the original (e.g. one that replaced it by hand) stops a restore to the original location before anything is written: restore to another location, or shut that VM down or rename it first. A halted one only gives a warning.

**Resume with memory** (restore points with the chip *RAM*, step *Mode* of the wizard): instead of booting from its disks, the VM continues where it was at the backup, with its programs, sessions and caches.
- **How it works:**
  - the disks and the memory image are uploaded; the memory image becomes the VM's suspend image (`type suspend`), next to its first disk;
  - the VM is created from the snapshot with memory's own metadata (`import_metadata` with `check_cpu`), so it is *Suspended*;
  - *Resume the VM* calls `VM.resume` (or `VM.resume_on` the chosen host). Without it the VM stays suspended and can be resumed in Xen Orchestra.
- **Only in the pool it was backed up in:** its memory needs the same kind of CPUs, and XAPI checks them at the import.
- **A copy (another location)** resumes **without network connections**: its NICs are removed, since its memory holds the original's addresses and open connections. Add a NIC (e.g. on an isolated network) once it may be connected. At the original location the VM keeps its networks and MAC addresses.
- **Not for domain controllers:** XAPI keeps the snapshot's VM generation id, so a resumed DC would continue its directory from the backup without Windows noticing a restore (USN rollback). Restore points with memory have no guest details (no guest processing), so the product takes a VM as a DC when its agent reports one, or one of its restore points with guest processing shows it as one; then the wizard offers only a cold start and a resume fails at once. Without either (no agent, never backed up with guest processing) it cannot tell: do not resume a DC.
- **Restore points with memory from before this version** do not name the disks of their snapshot with memory and cannot resume; back the VM up again.
- If XAPI does not create the VM suspended, or refuses to remove the NICs of a suspended copy, the restore fails with that message and removes everything it created; restore without memory instead.

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
- *Start a missing or broken log chain with a full backup at once* (default on): a broken chain gets a full in the same run, plus a warning. A database new to the backups gets its full instead of a log backup, as information, not a warning. That covers a new database, a database switched from SIMPLE to FULL, and a restored copy. SQL Server continues the log chain of the backups a copy was restored from, but those belong to another database.
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
- *SIMPLE:* no log backups; restorable to the times of its full backups only. Switched back to FULL, the next run takes a full backup and the chain is *intact* again.
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

## VMware vSphere

backdafuckup backs up VMs of a vCenter Server and restores them, with only public vSphere interfaces: the vSphere Web Services API (vim25), changed block tracking (CBT), HotAdd and VMware Tools. It needs no VDDK and no VMware software on the backup server. Disks are read and written by a **backup proxy**: a small Linux VM in the vCenter to which each disk is hot-added.

### Requirements

| Item | Notes |
|---|---|
| vCenter Server | 7.0 or later (tested with 7.0.3). A standalone ESXi host is refused: snapshots, CBT and HotAdd need vCenter. The license must allow the vSphere API (not the free ESXi). |
| vCenter account | An SSO user (e.g. `backup@vsphere.local`) with the privileges in [vCenter account](#vcenter-account). |
| Backup proxy | At least one Linux VM (x64, any distribution with systemd) in the vCenter, see [Backup proxies](#backup-proxies). |
| VMs | Hardware version 7 or later (for CBT). VMware Tools for guest processing without the agent, for the agent installation through the Tools and for guest addresses. |

### Network ports

| From → to | Port | Used for |
|---|---|---|
| Backup server → vCenter | TCP 443 (HTTPS) | vSphere API |
| Backup server → ESXi hosts | TCP 443 (HTTPS) | Only for installing the agent through VMware Tools (file copy into the guest) |
| Backup proxy → backup server | TCP 9555 (HTTPS, WebSockets) | The proxy's control connection and the disk data (NBD inside WebSockets). Outbound from the proxy only. |

### Add a vCenter

*Backup Infrastructure → Virtualization → Add pool or vCenter*, choose *VMware vCenter*. Enter the vCenter's address, check its certificate and trust it. The ESXi hosts' certificates are taken from vCenter (they are only used for file copies into guests). Then the SSO user and password.

### Backup proxies

A proxy reads the disks of the VMs' snapshots and writes the disks of restored VMs. The server attaches each disk to the proxy VM (`ReconfigVM_Task`, mode *independent non-persistent*, never with a file operation), the proxy finds it by its serial number and serves it to the server through its own connection. Nothing of the VM's disk is copied onto the proxy.

The proxy VM:
- Linux x64 with systemd, 2 vCPUs and 2–4 GB RAM are enough;
- advanced setting `disk.EnableUUID = TRUE` (the disks' serial numbers must be visible to the guest);
- a paravirtual SCSI controller with free places for hot-added disks, ideally a second one (bus 1) besides the system disk's;
- it must see the datastores of the VMs it serves: a proxy on each host with local datastores, one per cluster for shared datastores;
- it reaches the backup server on port 9555.

Install it under *Backup Infrastructure → Backup Proxies → Add proxy*: download the agent (`backdafuckup-proxy`, one file) and its systemd unit, copy them to `/opt/backdafuckup-proxy/` and `/etc/systemd/system/`, run the shown `enroll` command as root (it holds a one-time install token, valid 24 hours) and start the service. The proxy shows as online within seconds.

A job uses a connected proxy whose VM is powered on, has a free SCSI place and sees all datastores of the VM: one on the VM's host first, then one in its cluster, then the one with the fewest disks attached. The session log names the proxy and why others were not used.

### Backups

- **Snapshot:** a VM snapshot without memory (`backdafuckup temporary snapshot`), removed after the disks are read. User snapshots are never touched.
- **CBT:** switched on by the first backup of a VM (a quick reconfiguration, also while it runs). The first backup reads only the allocated areas, later ones only the blocks changed since the previous backup. A reset of CBT (e.g. after a Storage vMotion of some versions, or by the quick rollback) makes the next backup read the allocated areas again, with a warning that names the reason.
- **Disks left out, with a warning:** independent disks, raw device mappings and multi-writer disks (vSphere does not snapshot them). Snapshots with memory are not offered for vSphere VMs.
- **Guest processing:** with the backdafuckup agent in a Windows VM, as for XCP-ng (VSS, SQL Server, Active Directory). Without the agent, VMware Tools quiesce the snapshot: VSS in Windows (application-consistent), frozen file systems in Linux (file-system-consistent only, with a warning). If the Tools cannot quiesce, the snapshot is taken again without quiescing, with a warning.
- **Agent installation:** a missing agent is installed with the job's Windows credentials over SMB, or, when the VM has no address the server reaches on port 445, **through VMware Tools** (guest operations): the files are copied through the VM's ESXi host and the service is created with `sc.exe`. With User Account Control on, use the built-in Administrator account or a domain account in the local Administrators group. Agents are matched to vSphere VMs by the BIOS UUID they report.
- **After a crash** of the server, leftover backup snapshots and disks still attached to a proxy are removed by the cleanup (they are recorded before they are created).

### Restore

*Backups & Restore → restore point → Restore entire VM*:

- **To a new location:** the disks are created as new thin disks in a folder named after the VM on the chosen datastores, written through a proxy (only their data), and the VM is created from the backed-up configuration: CPUs, memory, firmware and secure boot, hardware version, disk controllers, disks, NICs. Choose the VM folder and resource pool (by default those of the original VM). The copy gets a new BIOS UUID and, by default, new MAC addresses. While the original VM exists, the copy's NICs start **disconnected**, because the guest comes up with the original's addresses and host name; connect them in vCenter when that is sorted out.
- **To the original location:** the original VM is shut down and renamed `… (replaced <time>)` (or deleted after the restore, if chosen), and the restored VM takes its name, BIOS UUID, MAC addresses and PCI slots, so the guest keeps its network configuration. Its jobs then back up the restored VM, starting with a full backup.
- **Quick rollback** (original location, VM still there): only the blocks changed since the restore point are written back into the existing VM. The VM is shut down, a safety snapshot taken, CBT lists the changed blocks of each disk, and exactly these are written from the restore point; then the safety snapshot is removed (or kept, if chosen) and CBT is reset, so the next backup reads the disks fully. If anything fails, the VM goes back to the safety snapshot. It needs the same disks (same size) and CBT on since the restore point; it is meant for changes inside the guest (a failed update, ransomware), not for damaged storage. **Not for domain controllers:** a rolled-back DC would keep its VM generation id and Active Directory would not notice that it went back in time (USN rollback); restore it fully instead, the restored VM gets a new generation id.

Not restored: vSphere tags and custom attributes, storage policies, the NVRAM (UEFI boot entries; Windows and Linux boot through their fallback boot loader), a virtual TPM's contents, CD-ROM, USB and serial devices. Disks get new UUIDs: an `/etc/fstab` that names a disk by `/dev/disk/by-id/scsi-36000c29…` must be adjusted (by file system UUID, the usual way, works).

### vCenter account

Create a role with these privileges and assign it to the backup account on the vCenter (with propagation), or on the datacenters, folders, resource pools and datastores used, including the proxy VMs:

| Area | Privileges |
|---|---|
| Datastore | Browse datastore · Low level file operations · Allocate space |
| Network | Assign network (restore) |
| Resource | Assign virtual machine to resource pool (restore) |
| Virtual machine → Change Configuration | Add existing disk · Add new disk · Remove disk · Toggle disk change tracking · Modify device settings · Advanced configuration · Rename (restore to the original location) |
| Virtual machine → Edit Inventory | Create new · Remove (restore, and its cleanup when it fails) |
| Virtual machine → Guest operations | Guest operation program execution · Guest operation modifications · Guest operation queries (agent installation through VMware Tools) |
| Virtual machine → Interaction | Power on · Power off · Guest operating system management (shutdown for restores) |
| Virtual machine → Snapshot management | Create snapshot · Remove snapshot · Revert to snapshot (quick rollback) |

## Kubernetes namespaces

backdafuckup backs up namespaces of Kubernetes clusters, with their objects and the data of their PVCs, or a whole cluster. It restores:
- the whole namespace, into the same cluster or another one;
- apps and workloads;
- single PVCs;
- single objects;
- files of a PVC;
- a whole cluster from one run, into the same cluster or another one.

It was built for RKE2 on vSphere with the vSphere CSI driver. Any CSI driver with volume snapshots works the same way. Volumes without snapshots (NFS, hostPath and local PVs; with the optional node agent also other CSI volumes and pod volumes such as emptyDirs) are backed up file by file.

### Requirements

| Item | Notes |
|---|---|
| Cluster | Tested with RKE2 1.32 and vSphere CSI 3.6.3. Either the backup server reaches the API server, or the cluster's pods reach the backup server (an agent, see below). |
| Volume snapshots | The snapshot CRDs and the snapshot controller (RKE2 ships both), the CSI driver's snapshotter, and a VolumeSnapshotClass for the driver (`deletionPolicy: Delete`). PVCs on NFS, hostPath and local volumes are backed up file by file without snapshots; CSI volumes without a snapshot class only with the node agent (see [Volumes without snapshots](#volumes-without-snapshots-file-by-file)). |
| vSphere CSI | 3.5 or later. 3.3.1 is end of life and not supported on Kubernetes 1.32. vSphere allows 3 snapshots per volume by default and costs I/O for each; backdafuckup keeps a snapshot only while its volume is read. |
| Container registry | For the mover image: reachable by the backup server (push) and the cluster's nodes (pull). |
| Server address | *Guest agents → Settings → server address*: the mover pods connect to it, like the guest agents. |

### Connect a cluster

1. Open *Backup Infrastructure → Kubernetes Clusters → Setup manifest* (for a cluster that is connected already: *Setup manifest* in its row). Copy or download `backdafuckup-setup.yaml`.
   - Turn *With the restore role* off for an account that only backs up.
   - Turn *With the node agent* on to read pod volumes and CSI volumes without snapshots (see [The node agent](#the-node-agent)).
2. Apply it with `kubectl apply -f backdafuckup-setup.yaml`, or as a Fleet bundle. It creates:

   | Object | Purpose |
   |---|---|
   | Namespace `backdafuckup-system` with `pod-security.kubernetes.io/enforce: baseline` | The work namespace: temporary PVCs and the mover pods |
   | ServiceAccount `backdafuckup` and its token Secret `backdafuckup-token` | The account the server uses |
   | ClusterRole `backdafuckup-backup` | List and watch every object, Secrets included; `get` only on the kinds it reads by name (core objects, workloads, CRDs, StorageClasses, snapshots), never on `*/*`, which would include `nodes/proxy`: the kubelet's API, and through it exec in any pod. Create and delete VolumeSnapshots and VolumeSnapshotContents. `get` and `create` on `pods/exec`, for hooks and the node agent (an exec over a WebSocket is authorised as `get`). PersistentVolumes, for the read-only copies of volumes read file by file |
   | Role `backdafuckup-work` in `backdafuckup-system` | PVCs, pods and Secrets of the work namespace; patch the agent's Deployment `backdafuckup-agent` (its image) |
   | Namespace `backdafuckup-node` and its DaemonSet (optional, *With the node agent*) | See [The node agent](#the-node-agent) |
   | ClusterRole `backdafuckup-restore` (optional) | Create, change and delete objects in every namespace: restores, and the mover pod of a file restore in the PVC's namespace. `bind` and `escalate` on roles and ClusterRoles, to restore RoleBindings to `admin` or `edit` and the ClusterRoles of a whole cluster. Together this is as much as cluster-admin. |

3. Read the token and the cluster CA. The commands are in the manifest's header:
   ```sh
   kubectl -n backdafuckup-system get secret backdafuckup-token -o jsonpath='{.data.token}' | base64 -d
   kubectl -n backdafuckup-system get secret backdafuckup-token -o jsonpath='{.data.ca\.crt}' | base64 -d
   ```
4. Choose *Add cluster* and enter:
   - the API server's host name or IP address and port, as in the kubeconfig's server URL, without `https://`;
   - a display name;
   - the token;
   - the cluster CA certificate (PEM).

   The server trusts an API server whose certificate that CA signed for the address. The token is stored encrypted, like credentials.
5. The inventory lists the namespaces and their PVCs. *Backup of its data* shows how each PVC's data is backed up:
   - *snapshot*: its driver has a VolumeSnapshotClass;
   - *files*: an NFS, hostPath or local volume, read through a read-only copy of its PV; or a CSI volume without a snapshot class that a running pod has mounted on a node with the node agent;
   - *objects only*: none of these, with the reason.

   *Pod volumes* lists the workloads' volumes that are not PVCs and what containers write into their own file systems; *Usage numbers* gives them sizes (see [Pod volumes](#pod-volumes-and-container-file-systems)). *Rescan* reads the inventory again.

### Through an agent (clusters the server cannot reach)

When the backup server cannot reach a cluster's API server (a firewall or NAT in between), an agent in the cluster connects out to the server instead.

**How it works:**
- The agent is the mover image in agent mode: a Deployment `backdafuckup-agent` in `backdafuckup-system`.
- It keeps a WebSocket to the server open (`/api/agent/clusters/<id>/control`, TCP 9555).
- For every connection the server's Kubernetes client opens to the API server, the agent opens a WebSocket of its own and joins it to a TCP connection to the API server in the cluster (`kubernetes.default.svc`).
- TLS runs from the server through the agent to the API server, so the agent sees no request. Exec for hooks goes through the tunnel too.
- On this server the cluster gets a port on the loopback address that the client connects to. The server checks the API server's certificate against the cluster CA and the name `kubernetes.default.svc`.
- The agent sends its ServiceAccount token and the cluster CA. It sends the token again whenever the kubelet renews it. The server keeps both in memory only.
- The agent authenticates with the cluster's **connect token**, which only the setup manifest holds.

**To add a cluster this way:**
1. Set the server address under *Guest agents → Settings* and the mover image's registry (below).
2. *Add cluster → Through an agent in the cluster*, with a display name.
3. The console shows the setup manifest with the agent. It contains:
   - the roles as above;
   - a Secret `backdafuckup-agent` with the connect token;
   - the pull secret in *Create* mode;
   - the Deployment, with the image by digest.

   The mover image is pushed first if needed. Keep the manifest like a password.
4. Apply it. Once the agent connects, the server reads the cluster and fills its inventory; the wizard waits for that.

**An existing cluster** changes to agent access with *Reach it through an agent* on its page. Jobs and restore points stay. The stored ServiceAccount token and pins are replaced by the connect token, and *Setup manifest with the agent* shows the manifest to apply. Going back to direct access means adding the cluster's token again.

**The agent pod:**
- It runs as user 1654, not as root, without capabilities, with a read-only root file system.
- Resources: 50m CPU and 64 MiB requested, 256 MiB limit.
- It needs no permissions of its own; the server's calls use the ServiceAccount's roles.
- The mover pods of backups and restores still connect to the server directly (TCP 9555), as with direct access.

**Status:** the cluster list shows *agent online* with its pod and node, or *agent offline* since its last contact. While the agent is offline, backups and restores of the cluster fail with "The agent of cluster … is not connected to this server".

**Updating the agent:** the server keeps the agent's image at its own mover image.
- When the agent connects (after a server update, the server restarts and the agent reconnects), the server compares the image of the Deployment with its own. If they differ, it pushes its image when needed and patches the `agent` container's image; the agent restarts with it and connects again.
- It waits while sessions of clusters run, because the tunnel is down for the restart.
- The permission to patch the Deployment comes from the restore role, or, in a manifest without it, from the work role of manifests since this version. A backup-only cluster with an older manifest needs it applied once more; until then the server log says so.

### The mover image

Volume data moves through mover pods: a small Linux container with the .NET mover on Ubuntu 24.04 "chiseled". The server ships it and pushes it to your registry. Set it up under *Kubernetes Clusters → Mover image*:

- **Registry and repository:** the registry API's host name and port, without `https://`, and the image path. For Nexus, use the Docker connector of a hosted repository.
- **Push user and password:** an account that may push to the repository.
- **User-Agent:** the default starts like the Docker client's, because proxies in front of registries often let only Docker clients through.
- **Pull secret**, one of three:
  - *Create* (the default): `backdafuckup-registry` in `backdafuckup-system`, from an optional read-only pull user, otherwise from the push user;
  - *Existing*: a docker-registry Secret there that you name, e.g. one a reflector copies in;
  - *None*: the nodes pull without a login.
- **Push:**
  - The server pushes the blobs the registry lacks, then the manifest.
  - The tag is `<version>-<digest prefix>`, so registries that forbid redeploying a tag accept every build.
  - Pods use the image by its digest, so the mover always matches the server.
  - After a server update, a backup pushes the new image itself before its first snapshot. A missing registry therefore stops a run before anything is created.
- **TLS to the server:** the mover pods check the server certificate against its pins, like the guest agents. With a certificate from your own CA, set `Agent:ServerPins`, because the image contains no internal CAs.

**The mover pods in `backdafuckup-system`:**
- They run as root, because block devices belong to root.
- They have no capabilities, no privilege escalation, a read-only root file system and seccomp `RuntimeDefault`.
- Resources: 100m CPU and 64 MiB requested, 256 MiB limit.
- This is why the work namespace enforces the Pod Security level "baseline".

### Backup jobs

Create a job under *Backup Jobs → New* with the kind *Kubernetes namespaces*, a cluster and its namespaces. Schedule, retention and repository work as for VMs. Guest processing does not apply to clusters and is refused.

**Namespaces by their labels:**
- *And every namespace with these labels* takes a label selector as `kubectl -l` does: `key=value`, `key!=value`, `key in (a,b)`, `key notin (a,b)`, `key`, `!key`. Separate requirements with commas; all of them have to hold.
- Each run asks the cluster which namespaces have the labels then, so a namespace that gets them later is backed up from its next run on. The work namespace is never included.
- The editor shows which namespaces of the inventory have the labels now. The session log names the namespaces each selector matched.
- A selector that matches no namespace gives a warning. A run with nothing left to back up ends with *Warning*.
- *Backups & Restore* lists the namespaces with their restore points, not the selector.

Each run makes one restore point per namespace:
1. **Objects:**
   - What is read: every object of the namespace, in the API's preferred version. That covers the built-in kinds (core, apps, batch, networking, policy, RBAC, autoscaling) and every namespaced custom resource, in its storage version.
   - Also read: the namespace itself, the PVs and StorageClasses of its PVCs, and the CRDs that have objects in it.
   - Left out: events, endpoints and leases, because the cluster makes them. A kind the account may not list gives a warning.
2. **Pre-hooks**, see below.
3. **Snapshots:** one VolumeSnapshot per PVC that has a snapshot class, taken one after another. Then the post-hooks run, unless volumes are read file by file (step 5).
4. **Data, one volume after another:**
   - The snapshot is exposed in `backdafuckup-system`: a VolumeSnapshotContent and VolumeSnapshot on the same snapshot, with `snapshot.storage.kubernetes.io/allow-volume-mode-change`, and a block PVC from it.
   - A mover pod reads the device and sends its non-zero blocks to the server. The server stores them like a VM disk, with dedup and compression.
   - Then the pod, the PVC and both snapshots are deleted.
5. **Volumes without snapshots, file by file:** NFS, hostPath and local PVCs, CSI volumes without a snapshot class and pod volumes asked for (see below). Then the post-hooks run: Velero's order, pre-hook → snapshots → file reads → post-hook.

**What to expect:**
- **Every run reads every volume in full.** CSI drivers of vanilla clusters have no changed block tracking. The repository stores only the chunks that changed, and the session log notes the full read as information.
- **The copy takes time.** On vSphere the block PVC is a full copy of the snapshot on the same datastore, made before the mover starts. The server waits up to 4 hours for the mover.
- **PVCs whose data cannot be read** (a CSI volume without a snapshot class and no node agent, a block-mode PVC without snapshots, a hostPath PV that no pod uses) are backed up as objects only. The restore point and the session log say why.

**Hooks** use Velero's annotations on the pod, so existing annotations keep working:

| Annotation | Meaning |
|---|---|
| `pre.hook.backup.velero.io/command` | The command as a JSON array, e.g. `["redis-cli", "save"]`. It runs in the pod before the first snapshot. |
| `pre.hook.backup.velero.io/container` | The container; default: the pod's first. |
| `pre.hook.backup.velero.io/on-error` | `Fail` (the default): the namespace is not backed up. `Continue`: the backup goes on and is crash-consistent with a warning. |
| `pre.hook.backup.velero.io/timeout` | Default `30s`; `s` and `m` are understood. |
| `post.hook.backup.velero.io/…` | The same, after the last snapshot, or after the volumes read file by file. Post-hooks also run when a snapshot or a read fails. |

Only running pods run hooks, through `pods/exec`.

**Consistency:**
- Each volume is crash-consistent at the moment of its snapshot. The volumes of a namespace are captured one after another, because vSphere has no group snapshots.
- When every pre-hook succeeded, the restore point is *app-consistent (hooks)*.
- A failed hook with `on-error: Continue` makes it crash-consistent with a warning.
- Volumes read file by file are read while in use, not at one point in time: the consistency names them and the number of files that changed while they were read.

**The whole cluster:** turn on *Back up the whole cluster* in the job editor.
- Each run backs up every namespace of the cluster at that time, except those left out:
  - *Leave out what the cluster's platforms make* (on by default): the namespaces of the platforms detected at that run (see [Platforms](#platforms)), so a new platform or a catalog update reaches the job. In the job object this is `@platform`.
  - *Also leave out*: names, or patterns with `*`.
  - `backdafuckup-system` is always left out.
  - Jobs saved before platforms were detected keep their fixed list (`kube-system`, `kube-public`, `kube-node-lease`, `cattle-*`, `fleet-*`, `cluster-fleet-*`, `local`). The job editor offers *Use the platform's list*.
- Each run also makes a restore point *Cluster-scoped objects* (`@cluster`):
  - **Included:** the CRDs and their cluster-scoped objects (for example cert-manager's ClusterIssuers), the ClusterRoles and ClusterRoleBindings people made, and the StorageClasses, VolumeSnapshotClasses, IngressClasses, PriorityClasses and RuntimeClasses. The webhook configurations are included for the record.
  - **Left out:** what the cluster's platforms make (system and default roles, a distribution's or manager's roles and webhooks, the CRDs the platforms install), the snapshot API's CRDs, backdafuckup's own objects, nodes, APIServices. The log counts them per platform.
- The session log names the platforms of the run, the namespaces it backs up, and those the platforms' list left out, per platform.

**Secrets:** the restore point holds the namespace's Secrets with their values.
- The repository protects them like disk images, through its file permissions.
- There is no encryption at rest yet.
- The console shows the keys of a Secret, never its values.

### Volumes without snapshots (file by file)

Volumes that no CSI snapshot can reach are read file by file into the repository: each file's content is stored in chunks like disk data (deduplicated across runs and clusters), each folder as a listing with owners, modes, times and inode numbers. Two readers:

- **A read-only copy of the PV** (NFS, hostPath and local PVs; no extra rights):
  - In `backdafuckup-system` the server makes a PV with only the volume's source, node affinity, mount options and capacity, forced to `ReadOnlyMany`, `Retain`, no StorageClass, read-only (`nfs.readOnly`), hostPath `type: Directory`, bound ahead to a PVC there. Annotations (above all the provisioner's), finalizers and the reclaim policy are never copied, so deleting the copy never touches the export or its folder. It carries the label `backdafuckup.io/shadow-of`.
  - A mover pod mounts it read-only and reads the files. It runs as root with `DAC_OVERRIDE` (read every file), `SETUID` and `SETGID` (see `root_squash` below), 512 MiB memory limit, on the node of a pod that uses the volume when there is one.
  - A hostPath PV without node affinity has its data on one node: it is read on the node of the pod that uses it; with no such pod it stays objects only.
  - The mover may take up to 15 minutes to connect (mount). While it waits, the session log reports the pod's state and the warning events of its PVC.
  - Afterwards the pod, the PVC and the copy of the PV are deleted, the PV last. backdafuckup deletes no other PV: only one with `app.kubernetes.io/managed-by: backdafuckup` and `Retain`.
- **The node agent** (CSI volumes without a snapshot class; see [The node agent](#the-node-agent)): it reads the volume where the kubelet has mounted it for a running pod (`pods/<uid>/volumes/kubernetes.io~csi/<pv>/mount`). A volume that no running pod has mounted, or on a node without the agent, stays objects only. Block-mode PVCs cannot be read this way.

**What is kept:** files, folders, symbolic links, hard links (stored once), FIFOs and device files (as entries), owners, modes, modification and change times, sparse regions (holes are not stored). Not kept: extended attributes and ACLs; access times are not recorded, so unchanged folders deduplicate.

**Incremental runs:** each run compares the volume with the job's newest restore point that has it ("compared with the restore point of …"). A file whose size, modification and change times and inode are unchanged is taken from that restore point without being read; the server checks every such file against its own copy of the previous listing. Files that changed during the previous read are read again. The log names the files and bytes not read again. A run whose data is all file by file and all compared is an *Incremental* restore point. *Active full* reads every file.

**`root_squash`:** an NFS export that maps root to nobody refuses root a file or folder that only its owner may read. The mover then reads it as its owner, or else as its folder's owner (`setfsuid`/`setfsgid`); nothing to do, and the run log does not mention it. A file it cannot read even so is a warning with its path, whom the mover also tried and the reason, e.g. "Volume data: 1 entry not read: locked.bin: cannot open the file (also tried as its owner 1000:1000): Permission denied." Such a file's mode lets not even its owner read it; the restore names it as not read at the backup.

**Changes while reading:** a file that changed while it was read, or less than 2 seconds before, is flagged (the consistency counts those that changed while read) and read again by the next run. Use pre- and post-hooks to make the application hold still (e.g. `["redis-cli", "save"]`, or a database's checkpoint) if the files must be consistent with each other.

### Pod volumes and container file systems

Data that is not in a PVC is not backed up unless asked for: an emptyDir, an inline CSI volume, or what a container writes into its own file system (its writable layer). backdafuckup names it, with its size when the cluster has usage numbers.

**Asking for pod volumes** (Velero's annotations, so existing annotations keep working):

| Where | Meaning |
|---|---|
| Pod annotation `backup.velero.io/backup-volumes` | The pod's volumes to back up, comma-separated, e.g. `cache,uploads`. |
| Job switch *Back up every pod volume that is not a PVC* | Every pod volume of the job's namespaces, except those in `backup.velero.io/backup-volumes-excludes`. |

- **Never read:** hostPath volumes (their data belongs to the node), a pod's own NFS mounts (give them a PV and PVC), ConfigMaps, Secrets, projected and downward API volumes, and what a platform puts into pods (e.g. Istio's sidecar volumes; a platform's volumes in its own namespaces).
- **Reading them needs the node agent.** Without it, or without an agent pod on the volume's node, volumes asked for are a warning ("asked for, not backed up … needs the node agent") and the run ends *Warning*.
- **Volumes not asked for** are named in the log as information, as a warning from 100 MiB.
- **Container file systems** are never backed up. The log names the containers that wrote most (writable layer from 100 MiB as a warning, else by write rate).
- **The inventory**, tab *Pod volumes*: each workload's volumes with kind, size, size limit and whether they are asked for; the platforms' volumes hidden; read-only hostPath mounts left out. The namespaces show how many pod volumes they have and how much data is outside PVCs.

**Restore:** pod volumes come back as files: browse, download, *Into a PVC of the namespace (mover pod)*, or onto a share. A namespace restore starts the pods with empty volumes and names where their files are; backdafuckup does not change workloads (no init container), because emptyDir data lives only as long as its pod.

### Usage numbers

*Kubernetes Clusters → (cluster) → Usage numbers* gives the inventory and backups the sizes of pod volumes, containers' writable layers and PVCs, and containers' write rates. Two sources; the node agent's numbers come first where both have one:

- **The node agent:** the kubelets' statistics (`/stats/summary`) of every node.
- **Prometheus:** a Prometheus API that has the cluster's storage metrics. Enter:
  - its URL (this server queries it at each scan and backup, so it must reach the address);
  - an optional user and password (basic authentication) or a token (bearer), stored encrypted;
  - TLS: the system's CAs, a CA certificate (PEM), or, after a test that shows an untrusted certificate, that certificate pinned;
  - an optional label filter for a Prometheus of several clusters, e.g. `cluster="prod"`;
  - optionally other queries. The defaults:

    | Numbers | Default query (metric) |
    |---|---|
    | Pod volumes | `ephemeral_storage_container_volume_usage` (the ephemeral-storage-exporter) |
    | Writable layers | `ephemeral_storage_container_rootfs_used_bytes` (the ephemeral-storage-exporter) |
    | PVCs | `kubelet_volume_stats_used_bytes` |
    | Write rates | `rate(container_fs_writes_bytes_total[1h])` (cAdvisor) |

    `$namespace` becomes the namespace, `$filter` the label filter. Each query gives series with the labels `namespace` (or `pod_namespace`), `pod` (`pod_name`) and `volume` (`volume_name`), `container`, or `persistentvolumeclaim`.
- *Test* runs every query and names how many series each found, or what failed.
- **NFS and hostPath PVCs get no size from the kubelet:** it measures the file system the volume is on, which for an NFS export is the whole export. A size larger than the PVC's capacity is ignored too.

### The node agent

The node agent is optional. It reads what the API cannot reach: pod volumes asked for (emptyDirs, on disk or in memory, and inline CSI volumes), CSI volumes without a snapshot class that a running pod has mounted, and the kubelets' usage numbers.

**Install it:** *Setup manifest* (or *Setup manifest with the agent*) with *With the node agent* on, then apply the manifest. It needs the mover image (pushed first if needed) and the server address under *Guest agents → Settings*, like the movers. It adds:

| Object | Purpose |
|---|---|
| Namespace `backdafuckup-node` with `pod-security.kubernetes.io/enforce: privileged` | Pod Security allows its host path mount only at "privileged". |
| ServiceAccount `backdafuckup-node`, ClusterRole and binding `backdafuckup-node` | `get` on `nodes/stats` only: the kubelet's statistics, never `nodes/proxy`. |
| Role and RoleBinding `backdafuckup-node-update` | The server's account may patch the DaemonSet `backdafuckup-node` (its image), nothing else there. |
| DaemonSet `backdafuckup-node` | One pod per node, control-plane and tainted nodes included (tolerates every taint). |

**The agent pod:**
- The mover image with `BDFU_MOVER_MODE=node`. 20m CPU and 64 MiB requested, 512 MiB limit.
- Root (uid 0), all capabilities dropped but `DAC_READ_SEARCH` (read every file and folder), no privilege escalation, read-only root file system, seccomp `RuntimeDefault`, no host network.
- The kubelet's directory (from the cluster's platform, e.g. `/var/lib/kubelet`) mounted read-only with `HostToContainer` propagation, so volumes the kubelet mounts later are seen. No other host path.
- Its self-check at start: its own pod's folder must be there, else the kubelet directory is wrong for the platform (set the platform by hand); the pod log says so.

**How the server uses it:**
- It finds the running agent pods by their label and reaches the one on the volume's node by exec (`/app/backdafuckup-mover node-request`), sending the request on standard input, never in the command (the API server's audit log keeps commands).
- A request names an operation (`check`, `read` or `stats`), and for a read the pod's UID, the volume and a one-time channel token. The agent builds the path only from the kubelet's fixed patterns (`pods/<uid>/volumes/kubernetes.io~empty-dir/<name>`, `kubernetes.io~csi/<name>/mount`) and refuses any other name.
- It sends the files to its own configured server address, checked against its pins, like the movers. At most 2 reads run at once per node.
- The kubelet's statistics come from `https://<node IP>:10250/stats/summary` with the agent's token. TLS is always checked: against the cluster CA for the node's address (RKE2), else against the kubelet's own certificate in its directory (kubeadm and kind sign their own). With neither, there are no kubelet numbers.

**Updates:** the server keeps the DaemonSet at its own mover image. It patches the image when it differs: at *Rescan*, and for a cluster reached through its agent when that agent connects (after a server update), but not while a session of the cluster runs.

**Remove it:** `kubectl delete namespace backdafuckup-node` and `kubectl delete clusterrole,clusterrolebinding backdafuckup-node`; apply the setup manifest without the switch.

### Platforms

Kubernetes distributions, cluster managers and addons make objects of their own, and make them again: namespaces like `kube-system` or `cattle-system`, roles, webhooks, the CRDs they install, records such as Rancher's App objects. backdafuckup knows these from a catalog of platforms and recognises a cluster's platforms by what it shows: its version, its API groups, its namespaces, a sample of nodes (provider ids, labels, annotations) and its CSI drivers.

- **When:** at every scan (adding, *Rescan*, the agent's connect) and at the start of every backup run. The clusters page names them (*Platform*, e.g. "RKE2 · Rancher · Fleet"); its *Platform* tab lists what gave each away.
- **What they decide:**
  - which namespaces a whole-cluster job leaves out with the platform's list;
  - what the cluster-scoped objects leave out;
  - what a restore leaves out as made by the platform: into another cluster everything the source's platforms made (they make their own there); into any cluster what they make in every namespace (e.g. OpenShift's image pull secrets, Istio's root CA ConfigMap);
  - which labels and annotations mean nothing in another cluster (e.g. Rancher's project fields);
  - CRDs a platform installs never come from a backup. Objects of such an API (e.g. RKE2's HelmCharts, OpenShift's Routes) go into a cluster that has it; elsewhere they stay out, named in the preview and the log.
- **Restore points** record the platforms of their run; restores use those. Restore points from before platforms were recorded are restored with the rules of that time (Kubernetes, RKE2, k3s, Rancher, Fleet, Calico, Cilium, vSphere CSI).
- **Set by hand:** *Platform → Set platforms…* chooses them instead of detecting them (for a cluster whose platform is not recognised, or not readable); *Set by hand* off detects them again.

| Platform | Kind | Tested | Recognised by | Leaves out (namespaces) |
|---|---|---|---|---|
| Kubernetes | base | yes | always | `kube-system`, `kube-public`, `kube-node-lease` |
| RKE2 | distribution | yes | version `+rke2r…`, node annotation `rke2.io/node-args` | — |
| k3s | distribution | no | version `+k3s…`, node annotation `k3s.io/node-args` | — |
| kind | distribution | yes | provider id `kind://` | `local-path-storage` |
| OpenShift | distribution | no | API group `config.openshift.io`, namespace `openshift-config` | `openshift`, `openshift-*` |
| GKE | distribution | no | version `-gke.`, provider id `gce://`, node label `cloud.google.com/gke-nodepool` | `gke-managed-*`, `gke-system`, `gmp-system`, `gmp-public` |
| EKS | distribution | no | version `-eks-`, node label `eks.amazonaws.com/nodegroup` | `amazon-cloudwatch`, `amazon-guardduty` |
| AKS | distribution | no | node label `kubernetes.azure.com/cluster`, provider id `azure://` | `gatekeeper-system`, `app-routing-system` |
| MicroK8s | distribution | no | node label `microk8s.io/cluster=true` | — |
| k0s | distribution | no | version `+k0s` | — |
| Tanzu Kubernetes Grid | distribution | no | version `+vmware.`, namespace `tkg-system` | `tkg-system`, `vmware-system-*` |
| Rancher | manager | yes | namespace `cattle-system`, API group `management.cattle.io` | `cattle-*`, `local` |
| Fleet | manager | yes | namespace `cattle-fleet-system`, API group `fleet.cattle.io` | `fleet-*`, `cluster-fleet-*`, `cattle-fleet-*` |
| Calico / Canal | addon | yes | API groups `crd.projectcalico.org`, `operator.tigera.io` | `calico-system`, `calico-apiserver`, `tigera-operator` |
| Cilium | addon | no | API group `cilium.io` | `cilium-secrets` |
| vSphere CSI | addon | yes | CSI driver `csi.vsphere.vmware.com`, API group `cns.vmware.com` | `vmware-system-csi` |
| Istio | addon | yes | API group `networking.istio.io` | — (its root CA ConfigMap in every namespace) |
| Antrea | addon | no | API group `crd.antrea.io` | — |
| Longhorn | addon | no | CSI driver `driver.longhorn.io`, API group `longhorn.io` | `longhorn-system` |

*Tested* means checked in the lab; the other platforms' rules come from their public documentation, named in the catalog (`src/Backdafuckup.Kubernetes/Platforms/platforms.json`). For a platform that is not recognised or not right, `scripts/k8s-platform-facts.sh` (read-only; kubectl and jq) prints what detection looks at, with node names, label values and namespaces that may be internal replaced, for a test fixture.

**OpenShift (untested):** mover pods run as root, so the backdafuckup ServiceAccount needs an SCC that allows it (for example `anyuid`), which the setup manifest does not create. A namespace's UID range (`openshift.io/sa.scc.*`) is kept in another cluster on purpose: restored volume data keeps its owners, which pods of a new range could not read.

### Restore

*Backups & Restore → Restore* on a namespace offers three ways:

**Namespace, apps or PVCs:**
- **What:**
  - **Whole namespace.**
  - **Apps and workloads.** Workloads are grouped by `app.kubernetes.io/instance`, else by the Helm release, else by `app.kubernetes.io/name`. A workload brings what it needs:
    - the ConfigMaps and Secrets of its volumes and environment, and its pull secrets;
    - its ServiceAccount, with the Roles and RoleBindings that name it;
    - its PVCs (a StatefulSet's from its claim templates);
    - the Services that select its pods and the Ingresses to them;
    - its HPA and PDBs.
  - **Single PVCs**, also under new names next to the originals.
- **Where:** the original namespace or another one, created when missing.
- **Existing objects** are left as they are, or replaced on request.
  - A PVC with data that already exists stops the restore before anything is written.
  - The exception is *Replace the PVCs in place* (below).
- **Order and cleaning:**
  - Objects go in Velero's order: CRDs, namespace, StorageClasses, PVCs, Secrets, ConfigMaps, ServiceAccounts, roles, Services, custom resources, workloads.
  - They are restored without uid, resourceVersion, timestamps, owner references, status and managed fields.
  - Objects made by a controller are not restored, nor what the cluster makes itself: ServiceAccount tokens, `kube-root-ca.crt`, the default ServiceAccount.
- **Volume data:**
  - A mover writes the image into a new block PVC in `backdafuckup-system`, on the original StorageClass.
  - A snapshot of it is exposed in the target namespace, and the PVC is created from that snapshot. This needs the snapshot class, and on vSphere it costs one more full copy of the volume.
  - Once the PVC is bound, the snapshots and the work PVC are deleted. A PVC may take up to 6 hours to be bound.
  - Why not hand the written volume to the PVC directly: on vSphere, deleting a CSI PV object, even with `Retain`, makes the driver's syncer unregister the volume. The volume can then be neither detached nor deleted until the syncer's next full sync.
- **Replace the PVCs in place** (with typed confirmation):
  1. The controllers of the pods that mount the PVC (Deployment, StatefulSet, ReplicaSet, ReplicationController) are scaled to 0. Their replica counts go into the artifact ledger, so a crash still scales them back.
  2. The PVC is deleted and created again from the restored volume.
  3. The controllers are scaled back.

  A pod of a DaemonSet or Job, or one without a controller, stops the swap before anything changes. In the lab a Redis StatefulSet was down for about 9 s.
- **A restore that does not complete removes what it created:** a namespace it created, with everything in it; otherwise each object it created. Work PVCs and snapshots go through the ledger.

**Into another cluster:** choose the cluster under *Where*.
- **StorageClasses:** classes the target lacks start on its default class. Choose another one per class under *Volumes in the target*.
- **Preview:** before anything is written, it shows each PVC's class and data path in the target, and what stops the restore. It covers what you chose: with apps or single PVCs, a PVC left out cannot stop the restore. It warns about:
  - owners on NFS;
  - what the files path loses;
  - CRDs whose operators are not part of the restore;
  - priority, runtime and Ingress classes the target lacks;
  - pods pinned to nodes of the old cluster;
  - Ingress hosts whose DNS still points to the old cluster.
- **Data paths, chosen per volume:**
  - **As a block volume:** when the target class's driver has a VolumeSnapshotClass (the one marked default, else the only one). This is the way described above, with the target's own snapshot class.
  - **As files:** for any other class, for example NFS (nfs-subdir) or local-path.
    1. A Filesystem PVC of that class is made in `backdafuckup-system`.
    2. A mover pod on it receives the files of the volume, which the server reads from the image's file system. Only ext and XFS can be read. Owners, modes and times come along; the root folder of the volume gets those of the backup.
    3. The volume is handed over to the restored PVC: its PV keeps `Retain` while the work PVC is deleted, is reserved for the restored PVC, and gets its reclaim policy back once bound.

    What the files path loses: hard links become copies, sparse files are written in full, extended attributes and ACLs stay behind. On an NFS export that maps root to nobody (`root_squash`), owners cannot be set. The session then notes that once, and the files belong to the export's user.
  - **Volumes backed up file by file** always go back this way, into the same cluster too, from their file tree: a new volume of the (mapped) class, of the PVC's size or larger when the files need more. FIFOs and device files are left out with a warning. A shared access mode the class refuses (local-path and ReadWriteMany) makes the volume ReadWriteOnce, with a warning.
- **Left out in another cluster:**
  - finalizers;
  - what the source cluster's platforms made, and the labels and annotations they set for themselves (see [Platforms](#platforms)): for example Rancher's project RoleBindings, project fields and App records of Helm releases (Rancher makes them again from the restored release Secrets);
  - objects of an API a platform installs, when the target lacks it (named in the preview and the log);
  - a Service's `loadBalancerIP` and `externalIPs`;
  - static PVs, for example of an NFS export. Their PVCs get new volumes, because both clusters would otherwise share the data.
- **StorageClasses** of the backup are not created in another cluster.
- **The mover's pull secret** is created in the target as for a backup.

**Whole cluster from this run** (on a restore point *Cluster-scoped objects*):
- **What:** the run's cluster-scoped objects, then the namespaces you choose (all by default), into the cluster backed up or another one, with the StorageClass map. Each namespace is previewed as above.
- **Order:**
  1. The account's right to bind and escalate roles is checked first, with a SelfSubjectAccessReview. Without it, apply the current setup manifest.
  2. CRDs, waited for until they are established.
  3. ClusterRoles (aggregated ones without their rules, which the cluster fills in), ClusterRoleBindings, the classes, then the CRDs' cluster-scoped objects.
  4. Each namespace as a task of its own.
- **Not created:**
  - objects that exist (unless you replace them);
  - a StorageClass the map replaces;
  - a second default StorageClass, IngressClass or VolumeSnapshotClass, or a second global default PriorityClass;
  - VolumeSnapshotClasses, when the target does not serve the snapshot API (its volumes then come back as files);
  - webhook configurations: with a stale CA and `failurePolicy: Fail` they would block the whole cluster. They are listed with the services they call; their operators make them again once their namespaces are back.
- **A namespace whose backup failed in the run** comes from its newest older restore point, with a warning.
- **When the cluster-scoped step fails** (no right to bind roles, an unreadable restore point, the cluster out of reach), the namespaces are not restored. Objects that fail on their own are warnings: the namespaces follow, and the session ends as *Warning* at least.

**Objects: compare and pick:**
- **Compare:** the restore point against the live namespace. Each object is *same*, *changed*, *deleted since* or *new since*. The deployment revision annotation and a PVC's `volumeName` do not count as changes.
- **Show:** an object as stored and as it is now. Secrets show their keys only.
- **Restore:** the picked objects exactly as stored, without what they need. Existing ones are replaced on request. PVCs with data come back only when they do not exist; otherwise use *Single PVCs*.

**Files and folders of a PVC:**
- The file browser reads the PVC's image: ext4 or XFS on the whole device. A volume backed up file by file is browsed from its file tree, whatever its file system was. The ZIP download works as for VMs.
- **A pod volume** (no PVC): *Into a PVC of the namespace (mover pod)* writes its files into a PVC you name, into a folder you choose; or download them, or copy them onto a share.
- **Back into the PVC** (the default target) writes into the live volume while the workload keeps running:
  - **The mover pod:** a mover pod in the PVC's own namespace mounts it as a file system. It runs on the node of the pods that use the PVC, tolerating that node's taints; for ReadWriteMany it runs anywhere.
  - **Target:** the original location or another folder in the PVC. Existing files are replaced, or kept with `RESTORED-<name>` copies next to them. Owner and group come from the backup, or from the folder each item goes into.
  - **How files are written:** as by the SSH restore script into Linux VMs. Each file is written under a hidden temporary name, flushed, given its owner, mode and times, and then renamed over the old one. Links come after the files and folder times last. Nothing is written through a symbolic link or outside the volume. Each item that fails is listed in the session log.
  - **Privileges:** the pod runs as root with `CHOWN`, `DAC_OVERRIDE`, `FOWNER` and `FSETID`, which the Pod Security level "baseline" allows.
  - **Refused before anything is created**, with the ZIP download as the alternative:
    - a namespace that enforces "restricted";
    - a block PVC;
    - a ReadWriteOncePod PVC that is in use.
  - **Modes come from the backup.** The kubelet applies `fsGroup` only when it mounts a volume, not to files written later.
  - **SELinux:** nodes with per-pod MCS labels would deny the second pod access to the volume.

### Cleaning up

**Labels and ledger:**
- Every object backdafuckup creates in a cluster carries `app.kubernetes.io/managed-by: backdafuckup` and `backdafuckup.io/run`: snapshots and their contents, PVCs, pods and Secrets.
- These objects and the workloads it scales go into the artifact ledger.
- They are removed when the session ends, also after a failure. What a crash leaves behind is removed at the server's start and retried every 10 minutes (`bdfu_artifacts_pending`).

**To check a cluster:**
```sh
kubectl get volumesnapshot,volumesnapshotcontent -A -l app.kubernetes.io/managed-by=backdafuckup
kubectl get pv -l backdafuckup.io/shadow-of
kubectl -n backdafuckup-system get pvc,pods,secrets
```
Only `backdafuckup-token` and, in *Create* mode, the pull secret `backdafuckup-registry` stay. Do not delete the others by hand while a session runs. The node agent's namespace `backdafuckup-node` holds only its DaemonSet; it creates nothing during a backup.

### Limits

- **Volumes go into another cluster's StorageClass without snapshots only as files**, from ext or XFS, or from a volume backed up file by file. Any other file system needs a target class with snapshots.
- **Webhook configurations and APIServices are never restored**; the operators that own them make them again.
- **Namespaces are chosen by name or by labels**, one selector per job, or as the whole cluster with exclude patterns.
- **No changed block tracking and no group snapshots** on vanilla vSphere CSI.
- **File by file:** no extended attributes or ACLs; restores write hard links as copies and sparse files in full. Volumes read file by file are not one point in time (use hooks).
- **Not read:** a pod's hostPath volumes, block-mode PVCs without snapshots, CSI volumes without snapshots that no running pod has mounted, and what containers write into their own file systems (named in the log, with sizes when there are usage numbers).
- **GitOps:** Fleet or Argo CD may set restored objects back to the state in Git. Restore those from Git, and use backdafuckup for the data and for objects made by hand.

### Troubleshooting

| Message | Cause | What to do |
|---|---|---|
| `TooManyRequests` … "storage is (re)initializing" | The API server restarts its watch cache. | backdafuckup retries 429 and 503 up to 5 times and follows `Retry-After`. If it persists, check the API servers. |
| "403 Forbidden: the ServiceAccount lacks a permission (apply the current setup manifest)" | The cluster has an older manifest, or one without the restore role. | Apply the current manifest from the console. |
| Manifests from before this version | Their backup role grants `get` on `*/*`, which includes `nodes/proxy`, `pods/proxy` and `services/proxy`. Nothing in backdafuckup uses them. | Apply the current setup manifest; `kubectl auth can-i get nodes --subresource=proxy --as=system:serviceaccount:backdafuckup-system:backdafuckup` then answers `no` (`nodes/proxy` would ask for a node named proxy). |
| "The mover pod … cannot start (ImagePullBackOff)" | The image is not in the registry, or the nodes may not pull it. | *Mover image → Push*; check the pull secret mode. |
| "The mover for … did not connect to https://…:9555/… within … minutes" | The pod was not scheduled, its volume was not attached, or it cannot reach the server. | Check the server address under *Guest agents → Settings* and the firewall from the pod network to TCP 9555; see the pod's events. |
| "The certificate of the backup server … is not trusted" (in the mover's log) | The server certificate is from your own CA. | Set `Agent:ServerPins`. |
| "The snapshot … was not ready to use after 10 minutes" | The CSI snapshotter cannot create the snapshot (limit reached, storage, class). | Check the VolumeSnapshot's events and the `csi-snapshotter` log. On vSphere, check the volume's snapshot count. |
| "The namespace … enforces the Pod Security level "restricted"" | File restore into a PVC of such a namespace. | Download the files as ZIP, or allow "baseline" for the namespace during the restore. |
| "The PVC … is ReadWriteOncePod and used by …" | No second pod may mount the PVC. | Scale the workload to 0 for the restore, or download the files. |
| "PVC …: the StorageClass … does not exist in cluster … (it has …)" | The target lacks the class of a backed-up PVC (another cluster). | Map the class to one the target has; the preview shows them. |
| "… restored, but owners are not kept: the volume does not let the mover give files to other users" | The target volume is on an NFS export with `root_squash`, or the like. | Allow root on the export for the restore, or fix the owners afterwards; the files are there. |
| "The account may not escalate ClusterRoles in cluster …" | The cluster has a setup manifest from before the cluster restore. | Apply the current setup manifest (its restore role has `bind` and `escalate`). |
| "PVC …: objects only, not its data – no VolumeSnapshotClass for the CSI driver …" | The driver has no snapshot class, and no node agent reads the volume. | Add a VolumeSnapshotClass for the driver, or install the node agent; it reads the volume while a running pod has it mounted. |
| "Pod volumes asked for, not backed up: … needs the node agent" | A pod's `backup.velero.io/backup-volumes`, or the job's switch, asks for volumes, and the cluster has no node agent (or none on that node). | Install the node agent, or remove the annotation or the switch. |
| "The node agent backdafuckup-node/… on node … cannot be reached by exec: … 403" | The setup manifest predates the node agent: exec over a WebSocket needs `get` on `pods/exec`. | Apply the current setup manifest. |
| "… does not exist (the pod runs elsewhere, or was replaced)" (node agent) | The pod moved or restarted between the plan and the read; its volume folder is gone. | Run the backup again. |
| Node agent log: its own pod's folder is missing | The kubelet directory of the cluster's platform is not where the kubelet keeps its pods. | Check the cluster's platform (*Platform*); set it by hand if it is not recognised. |
| "The kubelet … cannot be reached, or its certificate is neither signed by the cluster CA for … nor its own …" | The kubelet's serving certificate is from another CA, and its own certificate file is not in the kubelet directory. | No kubelet numbers then; reads still work. Usage numbers can come from Prometheus instead. |
| "Usage numbers of …: …" (warning) | The Prometheus query failed, or the node agent's statistics did. | *Usage numbers → Test* names each query's result; check the URL, sign-in, CA or pin, and the label filter. |
| "The agent of cluster … is not connected to this server" | The agent pod is not running, or cannot reach the server. | Check `kubectl -n backdafuckup-system logs deploy/backdafuckup-agent`: it names the server address and the error (certificate, connection). |
| "Cannot connect to the backup server … 401" (in the agent's log) | The connect token is not the cluster's current one (the cluster was added again or switched). | Apply the current setup manifest with the agent. |
| "the agent cannot reach the API server …" (server log) | The agent cannot open a TCP connection to `KUBERNETES_SERVICE_HOST`. | Check network policies in `backdafuckup-system`. |

## Tape libraries

backdafuckup drives tape libraries and standalone drives itself, with the SCSI commands of the public standards (SPC, SSC, SMC). No vendor driver or tool is needed. This version adds libraries, drives, cartridges and the library actions; tape jobs follow.

### Attach the devices

- **Linux:** the server talks to the devices through `/dev/sg*` (SCSI generic), never through `/dev/st*`, which rewinds the tape when it is closed. The service user needs the group `tape`; the unit file sets `SupplementaryGroups=tape`. No root is needed. Log in to iSCSI targets with `iscsiadm`.
- **Windows:** the service (LocalSystem) sends SCSI pass-through commands. When a driver claims the device (`\\.\Tape0`, `\\.\Changer0`), it goes through that driver, otherwise through the adapter; no vendor driver is needed. Log in to iSCSI targets with the Microsoft iSCSI initiator (`New-IscsiTargetPortal`, `Connect-IscsiTarget`).
- Devices are known by their **serial number**, not by their device name. When a re-login or a reboot renumbers them, they are found again.
- A device can be open in one program only. Stop other tools that hold it (`mtx`, `tapeinfo`, other backup software); the error then says that the device is open elsewhere.

### Tape agents (libraries on another host)

A tape agent runs on a Linux host whose drives and changers the backup server cannot reach itself, e.g. a library on SAS or Fibre Channel of another server. It forwards their SCSI commands; everything else stays on the server, and the library works like one attached to the server.

1. **Add it** under *Tape → Tape agents* (`POST /api/tape/agents`). This needs the server address under *Guest Agents → Settings*. The console shows its settings file `agent.env` **once**: the server address, the agent's id, its connect token and the SHA-256 of the server's public key. The agent trusts exactly that key.
2. **Install it** on the host. `scripts/build-tape-agent.sh` builds one self-contained file with its systemd unit (`deploy/linux/backdafuckup-tape-agent.service`):
   ```sh
   useradd --system --no-create-home --shell /usr/sbin/nologin backdafuckup-tape
   install -D -m 755 backdafuckup-tape-agent /opt/backdafuckup-tape-agent/backdafuckup-tape-agent
   install -D -m 600 agent.env /etc/backdafuckup-tape-agent/agent.env
   install -m 644 backdafuckup-tape-agent.service /etc/systemd/system/
   systemctl daemon-reload && systemctl enable --now backdafuckup-tape-agent
   ```
   The service runs as its own user in the group `tape` and reaches no device but `/dev/sg*`. `backdafuckup-tape-agent devices` lists what it would report.
3. **Add the library** with the agent as host. The agent connects out to the server (port 9555), so no port has to be open on its host.

The agent shows *Online* while it is connected. When it is gone, actions of its libraries fail with a message that says since when and from where it was last seen; it reconnects by itself. *New connect token* disconnects it until its `agent.env` has the new token. The gauge `bdfu_tape_agent_connected{agent}` is 0 while an agent is gone.

**Speed.** Every SCSI command crosses the network to the agent. So that the drive does not wait for each answer, the server sends writes ahead without waiting, and reads blocks ahead when it brings copies back: up to `Tape:PipelineMiB` (32 MiB) per drive. The agent runs a drive's commands strictly in the order they came; after a command that does not end GOOD (a filemark, the end of data, early warning, an error) it runs none of the commands sent ahead behind it, and the server sends them again once it has seen the answer. Agents and servers of different versions work together (the agent's protocol, 1 or 2): an agent of protocol 1 gets every command on its own, as before. Update agents with the server to get the speed. `Tape:PipelineMiB` 0 turns this off.

### Add a library

1. **Scan** a host, this server or a tape agent (*Tape → Add library*; `POST /api/tape/hosts/{host}/scan`, `local` for this server). It lists the changers with their drive elements, the drives, and devices it could not ask.
2. **Check the drives.** A changer that names its drives (DVCID) maps them itself (*dvcid*). For one that does not, the drives are assigned in serial order and marked *order*: check and correct the mapping.
3. **Name it** (`POST /api/tape/libraries`). The first inventory runs at once.

A **standalone drive** is added the same way, as one drive without a changer. Its cartridges are put in and taken out by hand; an inventory asks the drive which cartridge it holds (by the barcode in the cartridge memory). Jobs on it ask for cartridges, see [Standalone drives](#standalone-drives).

### Cartridges

- Known by their **barcode**, of any length. Until a cartridge has been in a drive, its kind comes from the barcode: `CLN…` and `…CU` are cleaning cartridges, the LTO WORM media ids `LT` to `LZ` WORM. After a load, the cartridge memory decides.
- **Location:** slot, mail slot, drive, or *offline* (not in the library at the last inventory). The slot a cartridge was in is its home slot; unload and import put it back there when it is free.
- **Inventory** (`POST /api/tape/libraries/{id}/inventory`) reads where every cartridge is, in seconds. **Scan** lets the robot check every element first (minutes on a large library), e.g. after the door was open.
- A cartridge without a readable barcode label is moved but not tracked; the inventory names its slot.

### Library actions

Load, unload, import, export and scan run as **tape sessions** (*Sessions*, type *Tape*) with a log (`POST /api/tape/libraries/{id}/actions`):

- **Load** takes a free, empty drive unless one is chosen; a cartridge in that drive goes back to its slot first. The session waits until the drive is ready. **A new LTO-9 cartridge is calibrated on its first load, which takes up to 2 hours.** Cleaning cartridges are not loaded for data.
- **Unload** rewinds, ejects and puts the cartridge back into its home slot.
- **Export** moves a cartridge into a free mail slot. **Import** moves every cartridge from the mail slots into a slot (its home slot if free). The mail slots are looked into every 30 seconds; the library then shows how many cartridges wait there.
- **One thing at a time:** a drive and the robot serve one action at a time. Others wait in order, and the console shows who holds the drive and who waits.
- **Errors** name the device, its serial, the host and the device path, e.g. "MOVE MEDIUM on the changer of Lab (serial … on this server, /dev/sg1) failed: …". A device that cannot be reached answers the API with 502.

| Setting | Default | |
|---|---|---|
| `Tape:MailSlotPoll` | `00:00:30` | How often the mail slots are looked into; `00:00:00` turns it off. |
| `Tape:LoadTimeout` | `02:30:00` | How long a load may wait for the drive. |
| `Tape:ReadyPoll` | `00:00:02` | How often a loading drive is asked. |
| `Tape:OperatorPoll` | `00:00:10` | How often a session waiting for a cartridge in a standalone drive asks the drive. |
| `Tape:OperatorTimeout` | `12:00:00` | How long such a session waits before it fails. |
| `Tape:CacheDirectory` | `tape/cache` in the data directory | Where a direct VM restore from tape keeps the chunks it reads. |
| `Tape:PipelineMiB` | `32` | How much a drive behind a tape agent keeps in flight (writes sent ahead, blocks read ahead); 0 sends every command on its own. |

The lab VTL (`deploy/vtl`, Holo VTL) behaves like an HPE MSL6480 with LTO-9 drives; its README lists what it does differently from a real library.

### Media pools and sets

A **media pool** is the tapes of one library used for one purpose (*Tape → Media pools*):

- **Sets.** Copies go into the pool's open **media set**. Chunks are written once per set, so a restore needs tapes of one set only. A new set starts by the pool's rule: every run, daily, weekly (the default; weeks begin on Monday), monthly, or never. The old set then closes.
- **Retention.** A closed set is kept for the pool's days, then it **expires**: its tapes are labelled again when the pool needs tapes. WORM tapes are never reused.
- **Tapes.** A run appends to the set's last tape while it has room. Then it takes an expired tape of the pool, a free one, a blank one, or one it has not read yet. A tape with other data (not backdafuckup's, or another pool's set that has not expired) is never written to. New tapes are labelled on first use. A tape left in another drive is taken from there when no session uses that drive; a tape in a drive that another session holds is left alone.
- **WORM only.** The pool takes only WORM cartridges.
- **Block size.** 256 KiB by default, which reads back on every host and through iSCSI.
- **The end of a tape.** A file is started only where it fits, with a reserve before the end (`Tape:ReserveMiB`, 128 MiB, or 0.25 % of a larger cartridge). An archive stops at the reserve or at the drive's early warning and goes on on the next tape. A file cut off by the end of the tape is written again on the next one.

### Encryption and keys

- **By the server** (*Software*): AES-256-GCM per record. Chunk ids, file names, manifests and VM names are encrypted; tape labels and file headers stay readable.
- **By the drive** (*Hardware*, LTO, application-managed encryption): the drive encrypts; the MSL6480 must be set to application-managed encryption.
- **Keys.** A key comes from a **passphrase** (*Tape → Media pools → Keys*), which is never stored. The server keeps only the derived key (Argon2id, protected with its key ring) and shows its fingerprint, e.g. `3F2A-91C0-7B11-02DE`. Every set gets its own key from it, and every tape's label names the fingerprint.
- **Key file.** Download the key file once, right after creating the key, and keep it apart from the tapes. Without the passphrase or the key file, encrypted tapes cannot be read, not even by backdafuckup. A key cannot be removed while a pool or a kept set uses it.
- **Existing keys** (tapes of another server, a lost database): *Existing key* with the key file's content, or with the passphrase once a tape's label has been read here (the label says how the key was made).

### Tape jobs

A tape job copies restore points of backup jobs into a pool (*Tape → Tape jobs*; `POST /api/tape/jobs`):

- **What:** the newest restore point of each VM that the open set does not have yet (the default), or every new one.
- **When:** on demand (*Run now*), on a schedule, and/or after a backup job succeeded.
- **SQL Server backups** (*Include SQL Server backups*, on by default): the log and full backups of every VM whose restore points are in the set, from the oldest of those restore points on. Each run adds the newer logs, so a point-in-time restore from tape reaches up to the last run.
- **One copy at a time.** Each copy is written as archives with the chunks the set lacks, then a restore-point file with its indexes and where all its chunks are. A synchronizing filemark commits it before the database records it. The set's catalog is written at the end of every run and before every tape change.
- **Repository contents.** The restore points are read from the repository's pack files in storage order, without decompressing them. Pack files keep the drive streaming.
- **Logs.** The log names each tape, its number in the set, and how much was new. Each restore point is a task of the session, its progress the share of its new data written; the SQL Server backups are one task.

### Bring a copy back

*Tape → On tape → Bring back…* (`POST /api/tape/copies/{id}/restore`) reads the copy into a repository with the same block size. It loads the tapes it needs in order and reads only the needed parts of each file. The copy becomes a restore point of its own, named *Imported from tape*, that belongs to no job: job retention never deletes it. From there it is restored like any other (VM, files, SQL Server, Active Directory). The VM's SQL Server backups on tape from that restore point on come back with it, for point-in-time restores. The tapes it needs must be in the library; offline ones are named. The session's task *<VM> from tape* shows how much of the copy has been read; a direct VM restore adds the VM's own task after it.

### Restore a VM directly from tape

*Tape → On tape → Restore VM…* opens the usual restore wizard for the copy (`GET /api/tape/copies/{id}/restore-point`, `POST /api/tape/copies/{id}/restore-vm`):

- The server reads only that restore point's chunks into a **cache** on its own disk (`Tape:CacheDirectory`, by default `tape/cache` in the data directory), then frees the drive and restores the VM from the cache to the pool, as from a repository: original or alternate location, storage and networks, with memory.
- The cache needs the copy's size plus 5 %; with less free space the session fails at once and says so. The cache is deleted at the end, and at the next start after a crash.
- Nothing is added to a repository. To keep the restore point, or for Kubernetes namespaces and clusters, bring the copy back instead.

### Standalone drives

A pool can use a standalone drive. Jobs and restores then ask a person for each cartridge:

- **The request** shows on every page of the console (*Tape: Insert …*) and in the session's log, until the right cartridge is in the drive (`GET /api/tape/requests`). After 30 minutes the alert *BackdafuckupTapeOperatorWaiting* fires; after `Tape:OperatorTimeout` the session fails.
- **Writing** asks for the set's last tape by name while it has room, otherwise for a blank, free or expired cartridge. A cartridge that does not fit is ejected, and the request says why: a tape of another set that is kept, other data, the wrong kind (WORM or not).
- **Names.** A new tape gets a name such as `Weekly-3F2A91-2` (pool, set, number in the set). The name is in its label and its cartridge memory, and the log says to **write it on the cartridge**. Cartridges are told apart by the serial number in their memory.
- **When it is full,** or the run ends, the drive ejects the tape: take it out.
- **Bringing a copy back** asks for each tape it needs by name, in order. A wrong one is ejected and named.

### A lost database: rebuild the catalog from the tapes

The tapes describe themselves, so a new server (or one whose database was restored from an older state) gets its tape catalog back from them:

1. **Keys first,** for encrypted sets: *Tape → Media pools → Keys → Existing key*, with its key file (paste its content) or its passphrase. A passphrase works once the tapes' labels have been read here (step 3 reads them; add the key, then run step 3 again).
2. **The library:** add it again (*Add library*) and put the tapes in; the inventory finds them.
3. **Rebuild the catalog** (the library's menu; `POST /api/tape/libraries/{id}/actions` with `rebuild`). A tape session reads the label of every tape the catalog does not know, then for each set it does not know the newest catalog on the set's highest tape in the library, and the copies written after that catalog.
4. **What comes back:** the pool (under its own id; created with default rules if it is gone: check when it starts new sets and how long it keeps them), the set (closed, kept for the pool's retention from now), its tapes (those not in the library as *offline*), and its copies under *On tape*. Tape jobs are not on tape: create them again.

A set whose highest tape is not in the library comes back as far as the tapes there know it; import the others and rebuild again. A set encrypted with a key this server does not have is named in the log with the key's fingerprint. For a standalone drive, read the tapes with `bdfu-tape-recover`.

### Without the server: bdfu-tape-recover

The format on tape is documented in [tape-format.md](tape-format.md). `bdfu-tape-recover` (`scripts/build-tape-recover.sh` builds it for Linux and Windows, one file each) reads tapes with nothing but a drive (and a changer, if there is one):

```sh
bdfu-tape-recover devices
bdfu-tape-recover catalog --barcode BDF002L9 --passphrase-file pass.txt      # the set's tapes and copies
bdfu-tape-recover restore --copy 01a1e649 --barcode BDF002L9 --key-file key.json --to-repository /mnt/restore/repo
bdfu-tape-recover restore --copy 01a1e649 --to-images /mnt/restore/images        # raw disk images (zeros stay holes)
```

- **Catalog:** it reads the newest complete catalog of the tape it is given. Give it the set's highest tape, which has the whole set.
- **Tapes:** it loads the tapes it needs with the changer, or asks for each one by its name.
- **Key:** for encrypted sets it takes the key file, a passphrase file, `BDFU_TAPE_PASSPHRASE` or a prompt, and checks the key against the fingerprint on the tape.
- **Repository:** a repository directory it wrote can be added in the console like any other; its restore points come with it.

## API and API tokens

The console's API is described under *API reference* (`/api-docs`). Scripts and tools use an **API token** instead of a password:

- **Create** one under *API tokens*, signed in with your password: a name (what uses it), its access, and when it expires (30 or 90 days, a year, or never). The token (`bdfu_…`) is shown once; the server keeps only its SHA-256, so copy it then.
- **Access:** *Full API access* acts as you. *Metrics only* reads `/metrics` (Prometheus) and gets 403 everywhere else, with a message that names its scope; the API calls it scope `full` or `metrics`.
- **Use** it as `Authorization: Bearer bdfu_…`. The request acts as the token's user, with that user's rights. Requests with a token need no `X-BDFU-Request` header (that header protects the console's cookie, which a browser sends on its own; a token it never sends on its own).
- **Limits:** a token cannot create tokens. A deleted or expired token, or one whose user is locked out, gets 401 at once. The list shows when and from which address each token was used last.
- **Example:** `curl -X POST -H "Authorization: Bearer bdfu_…" -H "Content-Type: application/json" -d '{}' https://<server>:9555/api/jobs/<job id>/run` starts a job.
- Creating and deleting a token is noted in the server log (name, prefix, user).

## Monitoring with Prometheus

The server serves Prometheus metrics at `https://<server>:9555/metrics`. It only answers the scrape and sends nothing anywhere. `deploy/monitoring/` has:
- the scrape configuration for plain Prometheus and the Prometheus Operator;
- alert rules, as a rule group and as a `PrometheusRule`;
- a Grafana dashboard.

- **Access:** create a token with *Metrics only* under *API tokens* and put it into the scrape configuration (`authorization: credentials_file`). Signed-in users and full tokens may read `/metrics` too. Metric labels name VMs, jobs, pools, storages and repositories.
- **TLS:** Prometheus verifies the server certificate. With the server's own self-signed certificate, give Prometheus that certificate as `ca_file`. Replace it when the server issues a new one, which happens shortly before expiry (5 years) or when the host name changes. A certificate from your CA (`Kestrel:Certificates:Default`) avoids this.
- **Settings** (`"Metrics": { … }`):
  - `Enabled` (default `true`; `false` removes `/metrics`);
  - `CatalogInterval` (default `00:01:00`): how often VM, repository, agent and SQL state are read;
  - `PoolInterval` (default `00:05:00`): how often each pool's storages and snapshots are read, read only with three XAPI calls per pool;
  - `AgentOfflineAfter` (default `00:10:00`).
  
  A scrape never waits for the database or a pool: it reads the last values.

What is measured (prefix `bdfu_`; `pool` is the connection name of a pool or vCenter, `storage` the SR or datastore name, or several joined with `+`). For vCenters the storage and snapshot gauges show the datastores and the VMs' snapshots; transport `hotadd` is a disk read through a backup proxy:

| Metric | Type | What |
|---|---|---|
| `bdfu_snapshot_duration_seconds` {pool, storage, disks, kind} | histogram | The `VM.snapshot` call (`kind` `snapshot`), or the VM's pause in a snapshot with memory (`kind` `checkpoint`). The bucket 10 s is the limit of a VSS freeze |
| `bdfu_snapshot_gate_wait_seconds` {pool, storage} | histogram | Wait for snapshots of other VMs on the same storage |
| `bdfu_snapshot_disk_skew_seconds` {pool, storage} | histogram | Time between the first and the last disk capture (XAPI snapshots disks one after another; from the disks' `snapshot_time`, whole seconds). VMs with two or more disks |
| `bdfu_snapshot_release_seconds` {pool, storage} | histogram | Removing the snapshot after the backup (`VM.destroy`, `VDI.data_destroy`) |
| `bdfu_guest_writes_held_seconds`, `bdfu_guest_freeze_seconds` {method} | histogram | How long the guest held its writes (`vss` or `sql`), and how long the freeze took |
| `bdfu_snapshots_total` {pool, storage, consistency, method} | counter | Snapshots by consistency (`application`, `crash`) and guest processing (`vss`, `sql`, `none`) |
| `bdfu_backup_disk_bytes_total` {pool, storage, transport, image, category} | counter | Disk bytes (`image` `disk`, or `memory` for the RAM image of a snapshot with memory) by transport (`nbd`, `http-export-vhd`, `http-export-raw`) and category (`read`, `unchanged`, `zero`, `deduped`, `new`, `stored`) |
| `bdfu_backup_disk_read_seconds_total` {pool, storage, transport} | counter | Time spent reading disks; read bytes divided by it is the throughput |
| `bdfu_backup_full_reads_total` {pool, reason} | counter | Disks read in full: `first_backup`, `active_full`, `memory_image`, `cbt_reenabled`, `cbt_disabled`, `change_data_gone`, `cbt_error`, `size_changed`, `index_unreadable`, `no_change_data`, `other` |
| `bdfu_transport_fallbacks_total` {pool, storage, from, to, reason} | counter | Disks read with the HTTP export instead of NBD: `no_endpoint`, `insecure_only`, `unreachable` |
| `bdfu_nbd_connect_seconds` {pool, host, result} | histogram | NBD connection attempts: `ok`, `timeout`, `refused`, `tls`, `error` |
| `bdfu_nbd_reconnects_total` {pool, host} | counter | NBD connections that broke while a disk was read and were opened again |
| `bdfu_hotadd_attach_seconds` {pool, storage, proxy} | histogram | vSphere: attaching a snapshot's disk to a backup proxy VM (the reconfiguration of the proxy VM) |
| `bdfu_proxy_connected`, `bdfu_proxy_open_disks` {proxy} | gauge | Backup proxies: 1 while connected, 0 once gone (proxies that connected since the server started); disks the server has open through each |
| `bdfu_sessions_total`, `bdfu_session_duration_seconds` {kind, result} | counter, histogram | Finished sessions |
| `bdfu_sessions_running`, `bdfu_sessions_queued` {kind} | gauge | Sessions now |
| `bdfu_vm_backups_total` {backup_job, result} | counter | Backups of single VMs, each attempt |
| `bdfu_restore_bytes_total` {pool} | counter | Bytes written by VM restores |
| `bdfu_vm_last_backup_timestamp_seconds`, `bdfu_vm_stale`, `bdfu_vm_no_restore_point`, `bdfu_vm_restore_points` {backup_job, vm} | gauge | Per VM and job, with the same rules as *Backups & Restore* |
| `bdfu_vm_last_backup_result` {backup_job, vm, result} | gauge | 1 for the result of the VM's last attempt (last 14 days) |
| `bdfu_vm_last_snapshot_seconds`, `bdfu_vm_last_disk_skew_seconds`, `bdfu_vm_last_writes_held_seconds` {pool, vm} | gauge | The VM's last snapshot, since the server started |
| `bdfu_storage_size_bytes`, `bdfu_storage_used_bytes`, `bdfu_storage_virtual_allocation_bytes`, `bdfu_storage_min_free_ratio` {pool, storage, type} | gauge | Space of each storage, and the free share below which backups refuse to snapshot (`Xen:MinStorageFreePercent`) |
| `bdfu_storage_vdis` {pool, storage, kind} | gauge | Disks by kind: `disk`, `snapshot`, `cbt_metadata`, `unmanaged` (base copies). On LVM storage each is a logical volume, and more of them make every snapshot slower |
| `bdfu_pool_snapshots`, `bdfu_pool_snapshot_oldest_seconds` {pool, owner} | gauge | VM snapshots in the pool, ours (`backdafuckup`, tagged) or `other` |
| `bdfu_pool_vm_disks` {pool, cbt} | gauge | Regular disks with and without changed block tracking |
| `bdfu_pool_poll_success`, `bdfu_pool_poll_seconds` {pool} | gauge | The last read of the pool. On failure the storage values stay as last read |
| `bdfu_repository_size_bytes`, `bdfu_repository_free_bytes`, `bdfu_repository_stored_bytes`, `bdfu_repository_restore_points` {repository} | gauge | Volume of each repository, and what its VM restore points stored |
| `bdfu_repository_format`, `bdfu_repository_packs`, `bdfu_repository_pack_bytes`, `bdfu_repository_index_entries`, `bdfu_repository_index_memory_bytes` {repository} | gauge | Format (1 = one file per chunk, 2 = pack files) and the packs and index of each repository |
| `bdfu_repository_unused_bytes`, `bdfu_repository_packing_unfinished` {repository} | gauge | Unused bytes left in packs after the last garbage collection; 1 while a converted repository still has chunk files to pack |
| `bdfu_repository_repacked_bytes_total`, `bdfu_repository_packed_loose_chunks_total` {repository} | counter | Bytes copied out of sparse packs by garbage collection; chunk files moved into packs after a conversion |
| `bdfu_repository_extent_state` {repository, scale_out, state}, `bdfu_repository_chains` {repository, scale_out} | gauge | 1 for each extent's state (active, sealed, evacuating, maintenance, offline); the chains whose new data goes there |
| `bdfu_scaleout_size_bytes`, `bdfu_scaleout_free_bytes`, `bdfu_scaleout_max_free_ratio`, `bdfu_scaleout_pending_source_deletes` {scale_out} | gauge | All extents' size; free space on the extents that take new chains, and the largest free share among them; copies of moves still to delete |
| `bdfu_repository_move_bytes_total` {scale_out} | counter | Bytes copied between extents by moves |
| `bdfu_agents` {state}, `bdfu_agent_last_contact_timestamp_seconds` {agent} | gauge | Guest agents online and offline |
| `bdfu_sql_last_log_backup_timestamp_seconds`, `bdfu_sql_log_backup_interval_seconds` {vm, sql_instance, database} | gauge | Newest log backup per database and the job's interval. A database that the newest run of its VM no longer saw (dropped) is left out |
| `bdfu_artifacts_pending` | gauge | Snapshots and disks still to be removed or reduced (retried every 10 minutes) |
| `bdfu_k8s_snapshot_ready_seconds` {cluster, driver} | histogram | From creating a VolumeSnapshot of a PVC until it is ready to use |
| `bdfu_k8s_snapshot_lifetime_seconds` {cluster, driver} | histogram | How long a VolumeSnapshot existed, until its content was gone (keep it short: vSphere allows 3 per volume) |
| `bdfu_k8s_mover_start_seconds` {cluster, mode} | histogram | From creating a mover pod until it runs (`backup`, `restore`, `files`): scheduling, image pull, and on vSphere the copy of the snapshot |
| `bdfu_k8s_hooks_total` {cluster, phase, result} | counter | Backup hooks in pods: `pre`/`post`, `ok`/`failed`/`timeout` |
| `bdfu_k8s_agent_connected` {cluster} | gauge | 1 while a cluster's agent is connected, 0 once it is gone (clusters whose agent connected since the server started) |
| `bdfu_tape_agent_connected` {agent} | gauge | 1 while a tape agent is connected, 0 once it is gone (agents that connected since the server started) |
| `bdfu_tape_written_bytes_total` {pool} | counter | Bytes written to tape (blocks with their headers) |
| `bdfu_tape_read_bytes_total` {pool} | counter | Bytes of chunks brought back from tape |
| `bdfu_tape_media` {pool, state} | gauge | Tapes of a pool by state (Appendable, Full, Expired, Damaged, …) |
| `bdfu_tape_media_sets` {pool, state} | gauge | Media sets by state (Open, Closed, Expired) |
| `bdfu_tape_pool_usable_media` {pool} | gauge | Tapes the pool can write to now (its open set's appendable tape, and expired, free, blank or unread tapes in its library) |
| `bdfu_tape_operator_requests_pending` | gauge | Requests to the operator a session waits for (a cartridge into a standalone drive) |

Kubernetes namespaces count in the session metrics (`bdfu_sessions_total` and the others) and per job in `bdfu_vm_*` like VMs; `cluster` in the `bdfu_k8s_*` metrics is the cluster's address, or its connection's name when it is reached through its agent.

The .NET runtime (`dotnet_*`), HTTP (`http_server_*`), Kestrel and Npgsql (`db_client_*`) metrics come along. The session log also names the disk-to-disk gap of every snapshot with two or more disks: "Disks of … captured over 9 s …".

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
