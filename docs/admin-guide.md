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

Then open `https://<server>:9555`, enter the setup token and create the administrator. The server uses a self-signed certificate. To use your own, configure `Kestrel:Certificates:Default` or a full `Kestrel:Endpoints` section.

## Installation (Windows)

```powershell
dotnet publish src/Backdafuckup.Server -c Release -r win-x64 --self-contained -o publish\win
Copy-Item deploy\windows\install-service.ps1 publish\win\
# As Administrator:
.\publish\win\install-service.ps1
```

Configuration goes in `%ProgramData%\backdafuckup\appsettings.json`.

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
5. **Copy-only:** the backup is a VSS copy-only backup (`VSS_BT_COPY`). The SQL Server backup chain (differential base, log backups) of other backup products stays untouched. Logs are not truncated.

Restore points show their consistency ("application", or "crash" plus the reason) under *Backups & Restore*.

**Requirements**
- Windows Server 2016 or later, or Windows 10/11, for the .NET 10 build (self-contained, no runtime needed). Older systems such as Server 2012 R2 get the .NET Framework 4.8 build, which needs .NET Framework 4.8 installed.
- The VM reaches the backup server over HTTPS (port 9555). The agent connects outbound only; nothing has to be opened in the VM.
- The guest tools (XCP-ng or Citrix VM Tools) report the VM's IP addresses, which the push installation uses.
- The agent identifies its VM by the BIOS serial number, which XCP-ng sets to the VM UUID. No mapping is needed.

**Setup**
1. *Guest Agents → Settings*: enter the address under which the VMs reach the server, e.g. `https://backup.example.local:9555/`.
   - The agents pin the server's own certificate (SPKI SHA-256), so the self-signed certificate works.
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
