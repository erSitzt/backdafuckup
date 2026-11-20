# Windows Backup Proxy — Plan

## Context

**Why.** The first live storage-snapshot backup on XCP-ng (2026-11-20) failed at the iSCSI login. The Linux proxy VM had no interface in the array's iSCSI networks, which are not routed, and a new address there would be a risk to production iSCSI.

Physical Windows servers that already reach the arrays over iSCSI, with host objects there, can read the snapshots instead. So the user decided (2026-11-20): **build the Windows proxy first.** This was planned as "later" in spec 3 (`agent-os/specs/2026-11-18-1713-storage-snapshot-backup/plan.md:116-118`).

**Decisions (2026-11-20)**

| Topic | Decision |
|---|---|
| First server | **a physical Windows server that already reaches the arrays over iSCSI** (production). |
| Host object | Present our snapshots to the **existing host object `win-proxy-1`** (an IQN can be in only one host object). We only add and remove VLUNs of our `bdfu-` snapshots; the host object stays untouched. |
| iSCSI | **Existing sessions only.** The agent rescans; it never logs in or out and never changes iSCSI or MPIO settings. A missing session is reported. |
| SAN policy | The user asked how Veeam does it. Veeam's Help Center: it sets the Windows SAN policy to **Offline Shared** on its proxies and keeps the SAN LUNs offline. We do the same: if the policy is unsafe, the agent sets Offline Shared and reports it. It also keeps each of our disks offline and read-only. Opt-out like on Linux (`"prepareSan": false`). |

**Outcome.** A self-contained `backdafuckup-proxy.exe` that:
- runs as a Windows service;
- installs with a one-liner (or a downloaded `.ps1`) and updates itself;
- finds a presented snapshot LUN by its NAA WWN, keeps it offline and read-only, and serves it over the existing NBD tunnel.

The server's array path (LVHD reader, verify mode, cleanup) stays as it is.

**OpenVmfs (from the OpenVmfs repository and its documentation):**
- 0.3.0 is built locally (`main` = `ecab1a1`): device map, no build paths, licence list.
- The repo on GitHub is private. It is not on nuget.org.
- Making it public and publishing it is open. Not part of this plan.

## Design

**Platform awareness (server and protocol)**
- **Hello and data model:** the hello gets `Platform` (`linux-x64` | `win-x64`, from `RuntimeInformation.RuntimeIdentifier`). Old agents without it count as Linux. It is stored in `ProxyEntity.Platform` (migration) and shown in `ProxyDto` and `ProxyInfo`.
- **Package** (`ProxyPackage.cs`): one folder `proxy/` with `backdafuckup-proxy` + `.service` (Linux) and `backdafuckup-proxy.exe` (Windows), plus `version.txt`. `Program(platform)` and `ProgramPath` pick by platform.
  - The update offer (`ProxyAgents.cs:211-241`) compares against the agent's own platform, so a Windows agent never gets the ELF file.
  - `ProxySelfUpdate.RunningHash` accepts `backdafuckup-proxy.exe`.
  - `scripts/build-proxy.sh` publishes `linux-x64` and `win-x64` (template: `scripts/build-tape-recover.sh`).
- **Capabilities:** the Windows agent sends `nbd-v1` and `san-v1`, never `hotadd-v1`. In `VSphereHotAdd.cs:118`, replace the `"hotadd-v1"` literal with `ProxyAgentProtocol.CapabilityHotAdd`. `ProxyDisk.OpenReaderAsync` names SAN reads `san` instead of `hotadd`.
- **Host-object dialog** (`AlletraSession.DescribeHostCreation`): the persona follows the proxy's platform. Linux gets 2 Generic-ALUA, `-os Linux`. Windows gets 15 WindowsServer, `-os "Windows Server"`. For win-proxy-1 nothing is created: proxy access finds `win-proxy-1` by the Microsoft IQN.
- **After unpresenting**, `ArraySnapshotService` teardown sends the existing `rescan` to the proxy, so Windows drops the vanished LUN. This is harmless on Linux.

**Agent structure** (`src/Backdafuckup.ProxyAgent/Program.cs`): split the composition into `LinuxHost` (today's code) and `WindowsHost`. Drop `[SupportedOSPlatform("linux")]` and add platform guards (warnings are errors, CA1416). `ProxyAgentClient` and `NbdServer` stay shared.

| | Linux | Windows |
|---|---|---|
| Config, key, log | `/etc/backdafuckup-proxy/agent.json` | `%ProgramData%\backdafuckup-proxy\agent.json`, folder ACL SYSTEM + Administrators (as `AgentInstaller.cs:159-178`); file log `proxy.log` with 5 MiB rotation (pattern `Agent.Core/AgentFiles.cs:119+`) |
| Service | systemd | `ServiceBase` (as `src/Backdafuckup.Agent/Program.cs:142-200`), `run` and `run --console` |
| Update restart | `Environment.Exit(0)` and `Restart=always` | rename the running exe to `.old`, exit code 3, SCM restart through `sc failure … restart` + `failureflag 1` (as the guest agent) |
| Install | bash one-liner | `backdafuckup-proxy.exe install --server --proxy --token --pins` copies itself to `%ProgramFiles%\backdafuckup Proxy\`, enrolls (spends the token), creates the service (`sc create … start= delayed-auto obj= LocalSystem`, failure actions), starts it and waits 15 s that it keeps running. Run again, it replaces. `uninstall` stops and removes the service and files. |
| Host facts | DMI | WMI `Win32_ComputerSystemProduct.UUID`, `Win32_BIOS.SerialNumber` (as the guest agent's `WindowsHostInfo.cs`) |

**Windows storage** (`src/Backdafuckup.Proxy/Windows/`; P/Invoke behind a narrow interface so Linux CI can fake it)
- **`IWindowsDisks`:** enumerate `\\.\PhysicalDriveN`, get identity and size, set attributes, open raw read-only. `Win32Disks` implements it with `LibraryImport` `CreateFileW`/`DeviceIoControl` (pattern `Tape/Scsi/WindowsScsi.cs:266-274`):
  - `IOCTL_STORAGE_QUERY_PROPERTY` / `StorageDeviceIdProperty`: VPD 0x83 NAA id. Not `Get-Disk.SerialNumber`, which is VPD 0x80 and not the WWN. With MPIO, PhysicalDrive is the multipath disk, one per LUN.
  - `IOCTL_DISK_GET_LENGTH_INFO`: size.
  - `StorageAccessAlignmentProperty`: sector size.
  - `IOCTL_DISK_SET_DISK_ATTRIBUTES`: offline + read-only, not persisted.
- **`StorageDeviceIdParser`:** pure parsing of the descriptor bytes (tested with byte arrays, like `WindowsScsiLayout`). It normalizes with `DeviceSerials.Normalize`, so it matches `Wwns.Normalize(snapshot.Wwn)`.
- **`WindowsBlockDevices : IBlockDevices`:**
  - **List:** the disks with NAA id and size.
  - **Rescan:** `Update-HostStorageCache` through `CommandRunner`.
  - **Open:** writable is refused. Otherwise the disk is set offline + read-only first, then opened with `GENERIC_READ`, `FILE_FLAG_NO_BUFFERING`.
  - **Delete:** close only; the server's rescan after unpresenting drops it.
- **`AlignedDiskDevice : IOpenDevice`:** reads through a sector-aligned bounce buffer (`NativeMemory.AlignedAlloc`). It widens unaligned offsets and lengths to sector boundaries, so NBD requests of any size work (today's backup path is already chunk-aligned).

**Windows iSCSI and SAN check** (`CommandRunner` + PowerShell with `-EncodedCommand`, as `Agent/WindowsPublishHost.cs:278-337`)
- **`WindowsIscsiInitiator : IIscsiInitiator`:**
  - **Initiator name:** `Get-InitiatorPort`, or the WMI fallback.
  - **Login:** checks `Get-IscsiSession` for a connected session to the target and logs in to nothing. If there is none: "no iSCSI session to <target> on <host>; connect it as for the array's other LUNs (with MPIO); the proxy does not log in itself".
  - **Probe:** the TCP probe moves from `IscsiadmInitiator` to a shared `IscsiPortals.ProbeAsync`.
  - **Rescan:** `Update-HostStorageCache`.
- **`WindowsSanCheck`:** reports all problems at once.
  - SAN policy (`Get-StorageSetting`) must be OfflineShared or OfflineAll.
  - The MSiSCSI service must be running.
  - The initiator name must be known.
  - MPIO is listed as information only.
- **`WindowsSanPreparation`:** only when the policy is unsafe, `Set-StorageSetting -NewDiskPolicy OfflineShared` (as Veeam does), logged and reported. Nothing else is changed.

**Installer and console**
- **One-liner** (elevated PowerShell), the token in a header as on Linux:
  ```
  curl.exe -sSL --pinnedpubkey 'sha256//…' --insecure -H 'Authorization: Bearer <token>' -o $env:TEMP\backdafuckup-proxy.exe <url>…/install/backdafuckup-proxy.exe; (Get-FileHash …).Hash -eq '<sha>' -or $(throw …); & $env:TEMP\backdafuckup-proxy.exe install --server … --proxy … --token … --pins …
  ```
  If the spike shows that Windows' curl (Schannel) does not enforce the pin with `--insecure`, the script pins in PowerShell instead: an `Add-Type` callback with the key-hash code from `Agent.Core/ServerCertificatePin.cs`, which works on .NET Framework.
  - **Downloadable** `install-backdafuckup-proxy.ps1` with the same steps.
  - **Refused requests** (spent token) get a PowerShell refusal script.
- **`Proxies.razor`:** the Windows toggle shows the one-liner, the script, manual steps and Windows SAN notes, instead of "planned".
  - The table shows the platform.
  - The SAN refusal row names `proxy.log` on Windows instead of journalctl.
- **`ProxyInstallDto`:** Windows fields.
- **`docs/admin-guide.md`:** "Backup proxies → Windows". Requirements: Windows Server 2016+, an existing iSCSI session, MPIO; SAN policy as Veeam sets it. Also install, update, uninstall, and what the agent changes (only the SAN policy if unsafe, our disks' attributes).

**Not in this plan:** FC host objects and FC proxies (the proxies in view use iSCSI), restores through a Windows proxy (writable opens are refused), HotAdd on Windows.

## Tasks (each with its tests, the full suite green, a commit; deploy as work progresses)

0. **Spec folder** `agent-os/specs/2026-11-20-HHMM-windows-proxy/` (plan, shape with the decisions above, standards, references). Spec 3's plan points to it.
1. **Spike** on our lab VM `the lab Windows VM` (no production), via guest operations:
   - curl.exe `--pinnedpubkey` with `--insecure` on Schannel (a wrong pin must fail);
   - `StorageDeviceIdProperty` on a VM disk;
   - unbuffered raw reads;
   - `Update-HostStorageCache`;
   - service install, update and restart with exit code 3.
   Write up in `spike.md`.
2. **Server and protocol:** `Platform` (hello, entity + migration, DTO, `ProxyInfo`), package per platform, update offer by platform, `.exe` routes, `RunningHash`, HotAdd constant, `san` transport name, persona per platform, rescan after unpresenting.
3. **Agent structure:** `LinuxHost`/`WindowsHost`, config and ACL, file log, `ServiceBase`, exit code 3, `install`/`uninstall`, shared portal probe.
4. **Windows storage:** `IWindowsDisks` + `Win32Disks`, `StorageDeviceIdParser`, `WindowsBlockDevices`, `AlignedDiskDevice`.
5. **Windows iSCSI and SAN:** `WindowsIscsiInitiator`, `WindowsSanCheck`, `WindowsSanPreparation`.
6. **Installer, console, docs:** both RIDs in `build-proxy.sh` (and `update-windows.sh`), PowerShell one-liner and script, refusal script, `Proxies.razor`, admin guide, runbook rows W1–W6.
7. **Live** (asking before each step on production):
   - **W1** `the lab Windows VM`: install, hello, update, uninstall. No SAN.
   - **W2** win-proxy-1, when the server is not busy with other work: install with the one-liner. Hello: `san-v1`, policy Offline Shared, session present. Proxy access shows host object `win-proxy-1` and 4/4 portals reached.
   - **W3** a backup of a lab VM on an LVHD SR with *Backup from storage snapshots*. Expected log: VM snapshot → array snapshot → presented to `win-proxy-1` → disk found by WWN, offline and read-only → LVHD read, verify mode → VM snapshot released.
   - **W4** a second run (incremental, unchanged-region check).
   - **W5** after that: no `bdfu-` VLUNs or snapshots on the array, no leftover disk on p1 after the rescan, Veeam sessions unchanged.
   - **W6** `uninstall` leaves nothing.

## Tests

- **Linux CI with fakes:**
  - `IWindowsDisks` fake: disks with NAA descriptors, sizes and sector sizes; attribute calls recorded.
  - PowerShell through `FakeCommands`: `Get-IscsiSession`, `Get-StorageSetting` and `Get-InitiatorPort` output as JSON.
  - Parser byte tests.
  - `AlignedDiskDevice` against a memory source: unaligned NBD requests return exactly what a plain read does.
- **What is covered:**
  - writable is refused;
  - no login attempt without a session; the message;
  - SAN policy: OnlineAll is set to OfflineShared, and with opt-out only reported;
  - `install` arguments for sc (`CommandRunner` fake);
  - the update offer per platform (server test: a Windows hello gets the `.exe` path, a Linux one the ELF);
  - the persona per platform in the host dialog;
  - the rescan after unpresenting.
- **The existing end-to-end proxy test** (`ApiIntegrationTests.Proxies.cs:109-183`) runs once more with platform `win-x64` and `FileBlockDevices`.
- **Full suite:** `dotnet test --solution backdafuckup.slnx -c Release` before every commit.

## Verification

- **Each task:** the suite is green; the console is checked with a screenshot (Playwright script in the scratchpad).
- **Live, W1–W6** as in task 7, recorded in `docs/lab-runbook.md`. On win-proxy-1 every step that changes something (install, a SAN policy change if needed, uninstall) is asked for first, when the server is not busy with other work. Afterwards, read-only on both arrays: no `bdfu-` objects left.

## Risks

- **A production server that does other work:**
  - Our agent only reads; it does not log in to iSCSI and changes no iSCSI or MPIO setting.
  - With the policy Offline Shared nothing is mounted.
  - Our read load runs on its storage links, so tests run when it is not busy with other work.
- **Veeam seeing our LUNs:** it identifies datastores by NAA id, and our snapshots have their own WWN. For LVHD (XCP-ng) there is nothing for Veeam to see.
- **Schannel pinning in curl.exe:** unknown until the spike; the fallback is pinning in PowerShell.
- **Disk numbering on Windows changes after rescans:** we always go by the NAA id, never by the number.
