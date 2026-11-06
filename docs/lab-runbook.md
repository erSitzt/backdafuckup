# backdafuckup – Lab runbook

Manual checks that need real VMs, a Windows file server or desktop clients, which the automated test suite cannot cover. The automated lab tests (backup/restore round trip, CBT, HTTP export) are described in the [administrator guide](admin-guide.md#lab-verification).

## Rules

- Use test VMs and test folders only. Never restore over production data.
- Do not delete anything in the pool by hand; backdafuckup removes its own snapshots. Lab VMs from [`deploy/lab`](../deploy/lab/README.md) are removed with `tofu destroy` only, in a run you start yourself.
- Automated lab tests run only with `BDFU_LAB_RUN=1`, set for that one run.
- Update the server only when no session runs. `scripts/deploy/update-windows.sh` refuses while a session is running, an open file browser included.
- Write down the server version and date of every check in the results table at the end.

## Preparation

Create test data in the VMs, note hashes, owners and permissions, and then run a backup. The lab VMs from [`deploy/lab`](../deploy/lab/README.md) get exactly this data when they are created; for other VMs run the commands below. For the Windows VM, use a job with guest processing (application-consistent); for one check you also need a crash-consistent restore point.

**Windows VM** (PowerShell as administrator):

```powershell
New-Item -ItemType Directory C:\bdfu-lab\tree\sub -Force | Out-Null
fsutil file createnew C:\bdfu-lab\big.bin 4500000000            # > 4 GiB, for ZIP64
Set-Content C:\bdfu-lab\tree\readonly.txt 'read-only'; attrib +r C:\bdfu-lab\tree\readonly.txt
Set-Content C:\bdfu-lab\tree\hidden.txt 'hidden'; attrib +h C:\bdfu-lab\tree\hidden.txt
Set-Content 'C:\bdfu-lab\tree\Grüße mit Leerzeichen.txt' 'umlauts and spaces'
Set-Content C:\bdfu-lab\tree\locked.txt 'opened exclusively in check C4'
Set-Content C:\bdfu-lab\tree\sub\acl.txt 'explicit permissions'
icacls C:\bdfu-lab\tree\sub\acl.txt /inheritance:r /grant:r '*S-1-5-32-544:F' '*S-1-5-18:F' '*S-1-5-32-545:R'   # Administrators, SYSTEM, Users in any language
(Get-Item C:\bdfu-lab\tree\readonly.txt).LastWriteTime = '2020-01-02 03:04:05'
icacls C:\bdfu-lab /save C:\bdfu-lab-acl-before.txt /t /c
Get-ChildItem -Recurse -File -Force C:\bdfu-lab | Get-FileHash -Algorithm SHA256 | Export-Csv C:\bdfu-lab-hashes-before.csv -NoTypeInformation
```

**Linux VM** (one with ext4 and one RHEL-compatible with LVM + XFS):

```sh
sudo mkdir -p /srv/bdfu-lab/tree/sub
sudo dd if=/dev/urandom of=/srv/bdfu-lab/tree/data.bin bs=1M count=20 status=none
sudo sh -c 'echo owned > /srv/bdfu-lab/tree/sub/owned.txt'
sudo chown 1234:1234 /srv/bdfu-lab/tree/sub/owned.txt; sudo chmod 0640 /srv/bdfu-lab/tree/sub/owned.txt
sudo ln -s ../data.bin /srv/bdfu-lab/tree/sub/link
sudo touch '/srv/bdfu-lab/tree/$(touch HACKED) and spaces.txt'   # a name that must never run
sudo touch -d '2020-01-02 03:04:05' /srv/bdfu-lab/tree/data.bin
sudo find /srv/bdfu-lab -type f -exec sha256sum {} + | sort -k2 > ~/bdfu-lab-before.txt
sudo find /srv/bdfu-lab -printf '%p %u:%g %m %Ts %l\n' | sort > ~/bdfu-lab-meta-before.txt   # %Ts: whole seconds
# RHEL-compatible VM with SELinux enforcing, for check D5:
sudo mkdir -p /var/www/html/bdfu-lab && sudo sh -c 'echo page > /var/www/html/bdfu-lab/index.html'
```

**Compare after a restore:**

- Windows: `icacls <folder> /save <file> /t /c`, then `Compare-Object (Get-Content C:\bdfu-lab-acl-before.txt) (Get-Content <file>)`. Compare hashes in the same way with `Get-ChildItem -Recurse -File -Force <folder> | Get-FileHash`.
- Linux: run the same `find` lines on the restore folder and compare with `diff`; the paths differ only in their prefix when the restore went to another folder.

## Checks

### A. Browse and download

| # | Steps | Expected |
|---|---|---|
| A1 | Open the Windows restore point under *Backups & Restore → Restore files*. | Volumes with the drive letters of the VM (C:, D:, …); recovery and EFI partitions hidden. |
| A2 | Open the Linux restore points (ext4; LVM + XFS). | Mount points from `/etc/fstab` (`/`, `/boot`, `/home`, …); LVM volumes readable. |
| A3 | Download one file. | SHA-256 equal to the VM. |
| A4 | Download `C:\bdfu-lab` as ZIP. Extract it with Windows Explorer, 7-Zip (`7z t`), the macOS Archive Utility and `unzip -t`. | All four read it. `big.bin` has 4,500,000,000 bytes, hashes are equal, umlaut names are correct, times are kept. |
| A5 | Open a crash-consistent restore point. | The browser shows the crash-consistent hint. |
| A6 | Look at the *FileBrowse* session. | One line per download (files, folders, size), closed by the user or after 30 minutes without use. |

### B. Network share

| # | Steps | Expected |
|---|---|---|
| B1 | Copy `C:\bdfu-lab\tree` to a folder on a Windows file server. | Hashes equal; modification and creation times, read-only and hidden kept. |
| B2 | Repeat B1 with *Keep existing files*. | `RESTORED-<name>` next to the existing files; the existing files are unchanged. |
| B3 | Repeat B1 with *Replace existing files*. | The read-only file is replaced. No `.bdfu-restore-*.tmp` is left. |
| B4 | Restore through a DFS path (`\\domain\namespace\link`). | The log shows domain controller → namespace server → link target; the file lands on the link target. |
| B5 | Wrong password; an account without write access; a host name that does not exist. | Clear errors that name `server (ip):445` or the DNS name. |

### C. Windows VM (guest agent)

| # | Steps | Expected |
|---|---|---|
| C1 | *Back into the VM → Another folder* `C:\bdfu-lab-restore`, permissions on. | Hashes and permissions as in the backup. |
| C2 | Change and delete some files in `C:\bdfu-lab`. Restore it to the original location with *Replace existing files*, permissions on. | Hashes and `icacls /save` equal to the files saved before the backup. |
| C3 | Restore to the original location with *Keep existing files*. | `RESTORED-<name>` next to the originals; the originals unchanged. |
| C4 | Open `locked.txt` exclusively: `$f = [IO.File]::Open('C:\bdfu-lab\tree\locked.txt', 'Open', 'Read', 'None')`. Then restore the tree with *Replace existing files*. After the check: `$f.Close()`. | Only `locked.txt` fails and is listed; the rest is restored; the session ends with *Warning*. |
| C5 | Create a junction in the target: `cmd /c mklink /J C:\bdfu-lab-restore\tree C:\Windows\Temp\bdfu-junction`, then restore into `C:\bdfu-lab-restore`. | The items below the junction fail; `C:\Windows\Temp\bdfu-junction` stays empty. |
| C6 | Stop a long restore (e.g. `big.bin`) under *Sessions*. | The agent finishes the current file and stops; what was written stays; no `.bdfu-restore-*.tmp` is left. |
| C7 | Repeat C2 with the .NET Framework 4.8 agent, on Windows Server 2012 R2 or 2016. | As C2. |

### D. Linux VM (SSH)

| # | Steps | Expected |
|---|---|---|
| D1 | Login as `root` (password). *Another folder* `/srv/bdfu-restore`, *Restore owner and group* on. | Hashes equal; `owned.txt` is `1234:1234` with mode `0640`; `link` is a symbolic link; times kept. |
| D2 | A sudo user with password (Ubuntu default). Restore `/srv/bdfu-lab` to the original location with *Replace existing files*. | As D1; the files in `/srv/bdfu-lab` are equal to before the backup. |
| D3 | A sudo user with `NOPASSWD`, *Keep existing files*. | `RESTORED-<name>` next to the existing files. |
| D4 | RHEL-compatible VM with `Defaults requiretty` in sudoers. | The restore works (PTY fallback). |
| D5 | RHEL-compatible VM, SELinux enforcing: restore `/var/www/html/bdfu-lab` into the original location. | `ls -Z` shows `httpd_sys_content_t`. |
| D6 | After D1–D3: `sudo find / -xdev -name HACKED`. | Nothing found; the file `$(touch HACKED) and spaces.txt` is restored under its literal name. |
| D7 | In the test VM only, create new host keys: `sudo sh -c 'rm -f /etc/ssh/ssh_host_*_key*; ssh-keygen -A; systemctl restart sshd 2>/dev/null \|\| systemctl restart ssh'`. Restore again. Then remove the old key under *Credentials → Known SSH hosts* and restore once more. | First the restore fails with both fingerprints; after removing the key it works and the log shows the newly trusted key. |
| D8 | A user without sudo rights. | Fails with "Running the restore as root on `vm (ip):22` as `<user>` failed …: `<user>` may not use sudo there". The staging folder is removed. |

### E. Operation

| # | Steps | Expected |
|---|---|---|
| E1 | Run `scripts/deploy/update-windows.sh` while a file browser is open. | The update is refused with the number of running sessions. |
| E2 | Leave a file browser unused for 30 minutes. | It closes; its session log says "closed after 30 minutes without use". |
| E3 | Keep a file browser open on the oldest restore point while the job runs and retention would remove it. | The restore point stays; the next job run removes it after the browser is closed. |

### F. SQL Server

Two Windows VMs with SQL Server and the agent (the lab's `bdfu-lab-sql1` and `bdfu-lab-sql2`: SQL Server 2025 Express, data on D:, log on L:, databases `shop` (FULL recovery), `small`, `imoltp`), a Windows account (`.\Administrator`, sysadmin) and a SQL login with `dbcreator` only (`bdfu_restore`), both under *Credentials*. A job with guest processing, method *SQL Server snapshot*, over both VMs. Check in the guest with SSMS or PowerShell (`System.Data.SqlClient`); list leftovers with `Get-IscsiSession`, `Get-Disk | ? SerialNumber -like 'BDFU*'`, `Get-Partition | ? AccessPaths -match publish`, `Get-Service MSiSCSI` and `*.bdfu-*` files in the database folders.

| # | Steps | Expected |
|---|---|---|
| F1 | Run the job with *SQL Server backup type* = full backup. | Session log: databases suspended, "recorded in msdb as full snapshot backups (in the backup chain)", the write pause. A warning names FULL-recovery databases whose log chain started. In the guest `msdb.dbo.backupset` has `is_snapshot = 1, is_copy_only = 0`; `sys.database_recovery_status.last_log_backup_lsn` of `shop` is set. |
| F2 | Run it once more with copy-only. | `is_copy_only = 1`; no log-chain warning for databases without a chain. |
| F3 | Open the restore point in the SQL browser. | Instances and databases with their files (data, log); `imoltp` is not restorable (In-Memory OLTP container), with the reason. |
| F4 | *Export files* of `shop`. | A ZIP with the three files and an attach script; hashes equal to the files in the backup. |
| F5 | *Publish* `shop` into the original VM with the Windows account. | Attached as `[shop_published]`, ONLINE, readable (row counts as in the backup); two disks over one loopback iSCSI session, partitions mounted under the agent's `publish` folder, no drive letters. |
| F6 | *Unpublish*. | Session Succeeded; no database, BDFU disk, access path, iSCSI session or portal left; MSiSCSI in its previous state. |
| F7 | *Publish* into the other VM with the SQL login. | As F5 on the other VM; the log says the login cannot open the database itself (dbcreator) and names the `ALTER AUTHORIZATION`. |
| F8 | While published: kill the agent process (`Stop-Process -Force`), then start the service. | The agent removes mounts and iSCSI from its journal at start; the server notices the restart and removes the database with the session's account; session Warning "… restarted meanwhile"; nothing left (F6). |
| F9 | While published: reboot the VM. | As F8, after the boot (the server waits until SQL Server accepts connections). |
| F10 | *Restore* `shop` as a new database into the other VM (original folders, instance defaults, own folders). | Files under the new name, checked with SHA-256, attached ONLINE; `DBCC CHECKDB` clean; row counts and checksums as in the backup. A file that exists already stops the restore, and the files written are deleted. |
| F11 | *Restore → Overwrite* `shop` in the original VM (sysadmin). | Offline, swap, online in seconds; same owner, users, Service Broker; `CHECKDB` clean; no `*.bdfu-*` files left (unless kept). |
| F12 | Overwrite again and kill the agent while it runs (e.g. right after "overwriting the database"). | The session fails with "has its own files again … nothing was overwritten", the database is online with its own files, the `*.bdfu-new` copies are deleted. |
| F13 | *Export .bak* of `shop` into the other VM with the download option (sysadmin). | A copy-only `.bak` with checksums (compressed except on Express) in the instance's backup folder, `RESTORE VERIFYONLY` passed; the session page offers the download for 24 hours, and its SHA-256 equals the file in the VM. With the dbcreator login the export is refused before anything happens. |
| F14 | Repeat F5, F10 and F11 with the .NET Framework 4.8 agent on Windows Server 2012 R2. | As there. |

### G. SQL Server log backups and point-in-time restore

The VMs, accounts and job of section F. In the job's guest processing: *SQL Server backups* on, log backups every 15 minutes (G9: 5 minutes), the Windows account. A marker table in `shop` (`CREATE TABLE dbo.marker (n int, at datetime2 DEFAULT sysdatetime())`), filled with one row per step, gives the times to restore to. Check in the guest with `msdb.dbo.backupset` (`type`, `first_lsn`, `last_lsn`, `name`, `description`) and the agent folder `sql-backups\queue.json`.

| # | Steps | Expected |
|---|---|---|
| G1 | Turn the backups on and wait for the next quarter hour. | One day session "SQL Server backups – <job> – <date>" with a task per VM; a log backup per FULL database (name `backdafuckup log`); the staging folder and the queue are empty afterwards. *SQL Server* lists the databases with the chain *intact*. |
| G2 | Let it run for an hour. | The day session is reopened by each run, the summary names the last one ("last run …"), times in the job's time zone. |
| G3 | Insert marker 1, wait for a log backup, insert marker 2, note the time, insert marker 3, wait for the next log backup. *Restore as a new database* at the noted time. | `<name>` ONLINE with markers 1 and 2, not 3; `DBCC CHECKDB` clean; no staging folder left. |
| G4 | The same with the slider: drag it, use the jump buttons, *latest*. | The time shown while dragging, the plan below it in local time; *latest* restores markers 1–3. |
| G5 | Restore into the other VM (G3 with target `bdfu-lab-sql2`). | As G3 on the other VM. |
| G6 | *Overwrite the original* at the noted time (sysadmin). | Restored as `shop_bdfu_pit` beside it, then the last log backup in single-user mode, swap, online, `MULTI_USER`; markers 1 and 2. The next run sends the queued last log backup, the chain shows a new base (a full) after the next run. |
| G7 | *Publish* at the noted time. | `shop_published` ONLINE with markers 1 and 2 on the mounted volumes; after *Unpublish* nothing is left (F6). |
| G8 | Run `BACKUP LOG shop TO DISK = N'C:\Temp\foreign.trn'` in the guest. | The next run warns with the time and the file and takes a full at once; the chain is *intact* again from that full. |
| G9 | Set the interval to 5 minutes; switch `small` to SIMPLE and back to FULL. | Runs at :00, :05, …; `small` *SIMPLE*, then a full at once after the switch back, with a warning. |
| G10 | Stop the backup server's service for 30 minutes, start it again. | No runs meanwhile; the next run warns about the RPO; the chain stays *intact* (no gap). |
| G11 | Kill the agent process during a log backup (e.g. with 5-minute logs, at the full minute), start the service. | The interrupted backup is resolved through `msdb` (sent, or the partial file deleted); no gap, nothing reported that was not backed up. |
| G12 | Stop the agent service until a run is due. | The run warns that the agent in `<host> (<ip>)` is offline; the next run with the agent catches up. |
| G13 | An image backup of the job while log backups run (SQL Server snapshot). | The suspend waits for a running log backup and vice versa; both succeed. |
| G14 | On a VM with SQL Server 2019 (or a VSS-mode job): turn on the backups. | A native full first (no snapshot base), then log backups; G3 with the native full as the base (downloaded into the staging folder). *Publish* is offered only with a restore point as the base. The full is downloadable as `.bak` on the database's page. |

## Results

| Check | Date | Server / agent version | Result | Notes |
|---|---|---|---|---|
| B4 | 2026-11-02 | 0.2.0 | passed | `\\corp.example\dfs\Shared`: DC → DFS01/-2 → `\\fs01.corp.example\Shared`. The short name `\\corp\dfs` does not resolve on Linux (expected). |
| B1 (part) | 2026-11-02 | 0.2.0 | passed | One file to fs01, original time kept. |
| C1 (part) | 2026-11-02 | 0.2.0 (net10) | passed | One file into `C:\TEMP` on app01 with owner and permissions; SHA-256 equal. |
| F3–F6, F10 (same VM), F11 | 2026-11-04 | 0.3.0 (`ecec970`, net10) | passed | bdfu-lab-sql1; found and fixed: writer files all "other" (`08745ec`), `.\name` accounts (`9b0f875`), an explicit ACE after overwrite (`ecec970`). |
| F13 (without download) | 2026-11-05 | 0.3.0 (`98708b8`) | passed | 5.6 MB `.bak` on bdfu-lab-sql1, copy-only, checksums, not compressed (Express). Name and file paths inside the `.bak` are the staging ones; dialog and log say so since `5de3e8e`. |
| F1 | 2026-11-05 | 0.3.0 (`5de3e8e`) | passed | Both VMs; warning for `shop`, `shop_restored`; `last_log_backup_lsn` set. |
| F5/F7 (other VM, SQL login), F8, F9 | 2026-11-05 | 0.3.0 (`df1b4cb`) | passed | Agent crash: logout from the dead target 68 s, then the server removed the database. VM reboot: SQL Server Express refused connections for about 2 minutes after the boot. |
| F10 (other VM, SQL login) | 2026-11-05 | 0.3.0 (`df1b4cb`) | passed, fixed | The dbcreator login could not open the database it attached (dbo kept `sa`); since `a6c453b` the log says why, and export needs sysadmin. |
| G1, G2, G8 | 2026-11-05 | 0.3.0 (`1cd0eb8`, `75ac9cc`) | passed, fixed | bdfu-lab-sql1/-sql2, hourly then 15-minute logs. The day session was sorted by its creation and summarised in UTC; since `75ac9cc` it moves to the top with each run and uses the job's time zone. A manual `BACKUP LOG` in sql1 → warning with time and path, a full at once, chain intact. |
| G3 | 2026-11-05 | 0.3.0 (`5d042fa`) | passed | `shop_restored_pit` on bdfu-lab-sql1 at 22:10:14 local time: markers 1 and 2, not 3; ONLINE, `CHECKDB` clean, staging empty. |
| G4 | 2026-11-06 | 0.3.0 (`4fd36b3`) | passed | *Latest* to `shop_latest` on bdfu-lab-sql1 (13:29): the full of 05.11. 21:30 and 61 log backups to 13:15; markers 1–3; ONLINE. |
| G5 | 2026-11-06 | 0.3.0 (`4fd36b3`) | passed, fixed | To bdfu-lab-sql2: the first try failed at the start with "L:\ is not a local fixed volume" (sql2 has no L:; the dialog offered the backup's folders); since `764d36c` the dialog checks the target's drives after *Connect* and preselects the instance's default folders for another VM. `shop_pi1` at 22:10:14 (the full of 21:30 and 3 log backups): markers 1 and 2, not 3. A second try at 21:10 (chosen by mistake) had no markers, as expected. |
| G7 | 2026-11-06 | 0.3.0 (`11fc09e`) | passed | Through the API (token): *latest* of `shop` (sql1) published as `shop_published` on bdfu-lab-sql2: the restore point of 14:25 and 5 log backups applied in the overlay, ONLINE, markers 1–3, files below the publish folder (publish id = session id); unpublish: no database, iSCSI session, disk, folder or journal entry left, MSiSCSI stopped again. At 22:10:14 *Publish* is disabled correctly (the base is the native full after G8's break); since `764d36c` the tooltip names the times that can be published. |
| G6 | 2026-11-06 | 0.3.0 (`11fc09e`) | failed, fixed, re-test open | Through the API: *Overwrite the original* `shop` at 22:10:14. Restored as `[shop_bdfu_pit]` into `*.bdfu-new` (the full of 21:30 and 3 log backups), detached, the last log backup in single-user mode, offline, then the swap failed: "Access to the path is denied." (without the path). The rollback worked: own files back, ONLINE, MULTI_USER, restored files deleted, markers 1–3 untouched; the queued last log backup was sent with the next run, the chain stayed *intact*. Cause, checked in the VM: SQL Server's detach leaves the files to the detaching Windows login only (`BDFU-LAB-SQL1\Administrator:(F)`), and Local System may not rename them. Fixed: the agent takes the restored files over for the Administrators group before it touches the database; a failed grant names the file. |
| G13 | 2026-11-06 | 0.3.0 (`11fc09e`) | passed, fixed | Through the API, the image backup started 25 s before the log run (1.2 GB of log in `shop_restored` on sql1 first). sql2: suspended at 15:59:38, the log job (15:59:58) waited and backed up after the snapshot was recorded (16:00:08). sql1: the log job (15:59:58, 1.2 GB until 16:00:31) ran first; the suspend (16:00:11) logged "waiting for a log backup to finish before suspending" and suspended at 16:01:15. Both backups and runs succeeded, all chains without gaps. The session said "suspended the writes … in 63.7 s"; it now names the wait apart ("waited 62.0 s for a running SQL Server log or full backup, then suspended … in 1.7 s"). Two earlier tries did not overlap (script errors on this side). |
| G9 (15-minute interval) | 2026-11-06 | 0.3.0 (`67c2e82`) | passed, fixed | bdfu-lab-sql1 `small`: SIMPLE → FULL at 09:39, a full at once at 09:45 with the warning "no log chain yet"; FULL → SIMPLE, chip *SIMPLE* at 10:00 but without a warning (fixed: the run now warns and says how far a point in time still reaches); FULL again at 10:04, a full at once at 10:15, log backups from 10:30. The 5-minute interval was not tried. |
| G11 | 2026-11-06 | 0.3.0 (`67c2e82`) | passed, fixed | bdfu-lab-sql2: about 1.1 GB of log in `shop_restored`, the agent process killed during its `BACKUP LOG` (09:45). SQL Server aborted the backup (not in `msdb`, no partial file); the finished `shop` backup of the same run stayed queued; the service restarted itself. The 10:00 run resolved the interrupted entry ("partial file was deleted"), sent the queued file first and backed up `shop_restored` from the previous last LSN (1.2 GB, 39 MB new in the repository); LSNs contiguous. The warning repeated itself and did not name the IP (fixed). |
| G12 | 2026-11-06 | 0.3.0 (`67c2e82`) | passed | Agent service on bdfu-lab-sql2 stopped 10:13–10:16: the 10:15 run warned "The agent in BDFU-LAB-SQL2 (10.26.17.21) is offline …"; the 10:30 run caught up, LSNs contiguous, chain *intact*. |
| G10 | 2026-11-06 | 0.3.0 (`61f887e`) | passed, fixed | Service stopped 10:46–11:16: no backups in either VM meanwhile, chains intact. Found: the run due at the start went at once, before the agents had reconnected, and reported both agents offline (fixed: up to 2 minutes' grace for the agents after a start); the 11:30 run caught up without an RPO warning, since the check compared with the log backup the run had just stored (fixed: it compares with the backup before). |
