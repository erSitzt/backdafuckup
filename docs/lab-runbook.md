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

## Results

| Check | Date | Server / agent version | Result | Notes |
|---|---|---|---|---|
| B4 | 2026-11-02 | 0.2.0 | passed | `\\corp.example\dfs\Shared`: DC → DFS01/-2 → `\\fs01.corp.example\Shared`. The short name `\\corp\dfs` does not resolve on Linux (expected). |
| B1 (part) | 2026-11-02 | 0.2.0 | passed | One file to fs01, original time kept. |
| C1 (part) | 2026-11-02 | 0.2.0 (net10) | passed | One file into `C:\TEMP` on app01 with owner and permissions; SHA-256 equal. |
