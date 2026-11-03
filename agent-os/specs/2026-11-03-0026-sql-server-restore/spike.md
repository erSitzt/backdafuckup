# Spike results

## 2a. SQL Server in a container (2026-11-03)

Image `mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04` (16.0.4305.5, built 2026-10-03), Developer edition, run as root with `--memory 2400m` and `MSSQL_MEMORY_LIMIT_MB=2048`. Script: a database `shop` (FULL recovery), 1,000 rows, a CHECKPOINT, then 500 more rows that exist only in the log.

**Crash-consistent copy:** `docker pause`, copy `shop.mdf` + `shop_log.ldf` through a helper container, `docker unpause`.
- `CREATE DATABASE shop_copy ON (FILENAME=N'C:\bdfu\copy\shop.mdf'), (FILENAME=N'C:\bdfu\copy\shop_log.ldf') FOR ATTACH` works.
- The errorlog shows "1 transactions rolled forward", and all 1,500 rows are there. Attach recovers files that were never shut down cleanly.

**Paths:** SQL Server on Linux maps `C:\x\y` to `/x/y`. Tests use `C:\…` paths and a bind mount at that place; other drive letters need the planned path-map hook.

**Overwrite building blocks:**
- `ALTER DATABASE … SET OFFLINE WITH ROLLBACK IMMEDIATE` + `DROP DATABASE` leaves both files on disk.
- Attaching them again gives `ONLINE`, `MULTI_USER`, recovery `FULL`, so the rollback path works.

**Export building blocks:** `BACKUP DATABASE … WITH COPY_ONLY, CHECKSUM, INIT, FORMAT, STATS=50` → "processed 466 pages"; `RESTORE VERIFYONLY … WITH CHECKSUM` → "The backup set on file 1 is valid"; `RESTORE … WITH MOVE` under a new name → 1,500 rows.

**File classification:** page 0 of both the data file *and* the log file has `m_type = 15` (file header page). The page type cannot tell data from log files.
- File types come from the VSS metadata (v2: `GetDatabaseFile` / `GetDatabaseLogFile`), with the file extension as the fallback for v1 restore points.
- Page 0 is used only to find the primary file (file id 1, offset 0x24 of the page header; to be confirmed in the classifier test).

**Practical notes for the tests:**
- Files that SQL Server creates as root are not readable for the test process. Tests copy and clean up through the container (or a helper container), and write their own input files world-readable.
- Memory: the machine had about 3 GB available; the limited container ran without problems.

## 2c. Loopback iSCSI on app01 (2026-11-03)

**Setup:**
- Windows Server 2019 Datacenter (10.0.17763), disk 0 GPT; MSiSCSI stopped and Manual before the test; SAN policy `OnlineAll`.
- Spike tool (scratchpad, not in the repo): `ISCSI` 1.5.6 + `DiskAccessLibrary` 1.6.6, a copy-on-write disk with an in-memory overlay, a 64 MiB GPT/NTFS test image built with DiscUtils, PowerShell for the initiator and disk steps.
- Run over WinRM as a local administrator. This needed `LocalAccountTokenFilterPolicy=1`, because remote UAC filters local admin accounts.

**Go: loopback publish works with the built-in initiator.**
- Login:
  - `New-IscsiTargetPortal 127.0.0.1:<random port>` + `Connect-IscsiTarget -IsPersistent $false`.
  - About 1 s after the portal; about 8 s including starting MSiSCSI.
- **Owner of the initiator's connection: the MSiSCSI service process** (svchost, e.g. PID 4720), *not* PID 4.
  - The check is therefore "the owning PID equals the PID of the running MSiSCSI service" (`Win32_Service.ProcessId`, in code `QueryServiceStatusEx`).
  - A connection from an ordinary process (the tool itself with a fake IQN) was refused.
- **The disk arrives offline** with `OfflineReason = Policy` even under `OnlineAll`. `Set-Disk -IsOffline $false` and `-IsReadOnly $false` bring it online and writable.
- **No drive letter** when the GPT partition has attribute bit 63; `NoDefaultDriveLetter = True`.
  - Without the patch, Windows assigned E: right away.
  - The product therefore patches; for MBR disks (no such attribute), letters are removed after online.
- `Add-PartitionAccessPath` into an empty folder under ProgramData works; files read correctly (32 MiB in about 200 ms through the target, without the HTTPS path; hash equal).
- **Writes go to the overlay only;** the base image hash is unchanged after every run. Bringing the disk online alone writes about 5.7 MB (NTFS metadata, System Volume Information); writing an 8 MiB file adds about 8.4 MB.
- **Signature collision:**
  - With disk 0's GPT disk GUID copied into the test image and served unpatched, the disk arrives with `OfflineReason = Collision`.
  - On online, Windows writes a new disk GUID itself (into the overlay).
  - With our patch (new disk and partition GUIDs, CRCs fixed; `sgdisk -v`: no problems), no collision occurs.
- **Cleanup:**
  - `Remove-PartitionAccessPath`, `Set-Disk -IsOffline $true`, `Disconnect-IscsiTarget`, stop MSiSCSI if it was stopped before.
  - **`Remove-IscsiTargetPortal` fails** (via the pipeline: "portal not found"; with explicit parameters: a type mismatch on the port), so three portal entries were left over. `iscsicli RemoveTargetPortal 127.0.0.1 <port>` removed them.
  - The product uses `RemoveIScsiSendTargetPortalW` and checks afterwards that no portal is left.
- **Not measured yet:** throughput with reads over the agent's HTTPS channel (task 7).
- The VM was left clean: no portals, no sessions, MSiSCSI stopped/Manual, only disk 0, spike files removed.
