# Plan: Windows guest agent — application-consistent backups (VSS) and push installation

Spec folder: `agent-os/specs/2026-11-02-1430-windows-guest-agent/`

## Context

The basic backup works: full backups and restores of XCP-ng VMs, with an HTTP export fallback. The user calls the following technical features **knock-out criteria** that come before everything else (e-mail etc.): file-level restore (FLR), SQL Server restore and AD object restore. The order is decided: **agent → FLR → SQL → AD**, each as its own spec.

XAPI's quiesced snapshot was removed in XenServer / XCP-ng 8.1, so every backup is only crash-consistent today. Application-consistent backups need our own agent in the Windows guest. During `VM.snapshot` it freezes I/O through VSS and then releases it. The agent is also the basis for later specs: FLR "restore into the VM" and the SQL restore into an instance.

**Revision 2026-11-02 (after approval): the patterns of the user's MIT project `erSitzt/inventory` (`~/GIT/inventory`) are adopted**, because there they are proven in production:
- **Transport:** HTTPS long poll instead of SignalR. `POST /api/agent/enroll` with the enrollment secret returns a 256-bit agent key; the server stores only its SHA-256. After that every request carries the header `X-Agent-Key`, and `POST /api/agent/poll` is held for up to 50 s and returns the next command at once. Results go back as `POST …/result`. This needs no SignalR dependency (also on net48), and commands like "thaw" arrive with no delay because a poll is always open.
- **Push without a temporary setup service:** a fixed service name (`BackdafuckupAgent`, LocalSystem, recovery restart) and a fixed folder (`%ProgramFiles%\backdafuckup Agent`, ACL SYSTEM + administrators). The push copies the files plus `agent.json` (server URL, SPKI pin, enrollment secret), then creates and starts the service. A fixed name and path can be allowed in endpoint protection.
- **Self-update** by poll: the agent compares the hash of its files with the server's; it downloads into a staging folder, swaps, and exits with code 3, and the service recovery restarts it.
- **Setup package** (ZIP with `Install-Agent.cmd`) for GPO and software distribution.
- **Code signing (now in scope):** a self-signed release certificate (`scripts/new-signing-cert.sh`), signed on Linux with `osslsigncode`. The `.cer` file is committed; the docs explain distribution to "Trusted Publishers" and Defender allow indicators (as in `docs/defender.md` of the inventory project).
- **Difference from inventory:** there net48 is preferred; here, by user decision, the **.NET 10 build is preferred** and 4.8 is the fallback.

**User decisions (2026-11-02)**
- **Deployment:** push installation through Windows admin credentials is part of this spec. There is also a self-installing package with a registration token for GPO or software distribution.
- **Connection:** the agent connects **outbound** over HTTPS to the backup server (port 9555). No inbound rule is needed in the VM.
- **Freeze fails or times out:** the VM is still backed up crash-consistently and the session ends with a warning that names the reason. Per job this can be switched to "fail the VM".
- **VSS backup type: copy-only** (`VSS_BT_COPY`). Veeam runs in parallel on the VMs, and its SQL chains and log handling stay untouched. Log truncation comes later with T-log backups and PITR.
- **Platforms:** Windows only; the Linux agent comes later. **Two agent builds:**
  - **.NET 10**, the preferred build: self-contained, Server 2016+ / Windows 10+.
  - **.NET Framework 4.8**, the fallback, e.g. for Server 2012 R2.
  - The push installation tries .NET 10 first and falls back to 4.8.
- **Later specs (already decided):**
  - FLR: browser download, back into the VM and to a network share; NTFS (GPT/MBR) plus ext4/XFS with LVM.
  - SQL: restore into an instance and export as `.bak`; PITR later.

## Research results (sources go into `references.md`)

- **Freeze pattern (qemu-ga, XenServer `win-xenvss`):**
  - Our requester calls `DoSnapshotSet`.
  - Our own VSS **software provider** in `CommitSnapshots` (inside the flush-and-hold window) sets the event "frozen" and waits for "thaw" or a timeout. It always returns S_OK.
  - The reported shadow copy is a no-op; the real snapshot is the VM snapshot.
  - Named events in `Global\`, accessible only to SYSTEM and Administrators.
- **Timing:** VSS releases flush-and-hold after **10 s** at the latest. After that comes `VSS_E_HOLD_WRITES_TIMEOUT`, and the backup is only crash-consistent. Writer freeze timeout is 60 s. **`VM.snapshot` of all disks must fit into ~10 s.** That must be measured on the team's pool.
- **Build:**
  - mingw-w64 **≥ 10** contains `vss.h`, `vsprov.h`, `vsadmin.h`, so no VSS SDK is needed. Cross-compile on Linux in Docker, as QEMU does in its CI.
  - Reference implementations: qemu-ga `qga/vss-win32` (GPL-2.0-or-later, reading only), `xenserver/win-xenvss` and `win-xenguestagent` (BSD-2), Microsoft Windows-classic-samples (MIT). We write our own code.
- **Requester in C#:** there is no maintained pure C# library. AlphaVSS (C++/CLI) is barely maintained. So the requester also goes into the **native DLL**, and both agent builds use it via P/Invoke.
- **VM mapping:**
  - The BIOS serial number (`Win32_BIOS.SerialNumber`) equals the XAPI VM UUID.
  - The SMBIOS UUID appears under Windows with the first three groups byte-swapped. Check both byte orders.
- **VM-Generation-ID:** XCP-ng supports `VM.generation_id`, and a non-live import generates a fresh one. This matters for the later AD spec.
- **Copy-only:** `VSS_BT_COPY` does not change the SQL differential base and shows as `backupset.is_copy_only=1`. The SQL writer never truncates logs.
- **Push from Linux:**
  - SMBLibrary (LGPL-3.0) provides an SMB2/3 client with NTLM (Kerberos via Kerberos.NET), files and RPC over named pipes.
  - We add the `svcctl` (MS-SCMR) calls ourselves.

## Scope

**In:**
- **Agent:** the Windows agent service (two builds), the native VSS DLL (provider + requester + registration), and freeze/thaw exactly around `VM.snapshot`.
- **Guest metadata** in the restore point: OS, volumes, VSS writers/components, SQL instances/databases with file paths.
- **Registration and push:** registration with pinned server certificate; push install, update and uninstall via SMB/ADMIN$ plus svcctl.
- **Console:** a "Guest agents" page, Windows credentials, a "Guest processing" section in the job, and the consistency display.
- **Quality:** tests, docs and a lab runbook.

**Out:**
- FLR, SQL and AD restore.
- T-log backup, truncate and PITR.
- Linux agent and pre/post scripts.
- An MSI package; the self-installing EXE is enough for GPO and software distribution.

## Architecture

**New projects / components**
- `src/Backdafuckup.Agent.Protocol` (netstandard2.0): DTOs and commands of the agent API (enroll, poll, result) for agent and server.
- `src/Backdafuckup.Agent` (multi-target `net10.0-windows`, self-contained single-file win-x64, plus `net48`):
  - Windows service with a long-poll loop over `HttpClient` (works the same on net48) and server certificate pinning (SPKI from `agent.json`).
  - One EXE with the commands `run | install --server --pin --token | uninstall`. When the SCM starts it as a temporary setup service, it runs the installation (PsExec model).
  - Writes logs to the Windows event log and a file.
- `native/bdfu-vss/` (C++, mingw-w64 in Docker, `build.sh`):
  - `bdfu-vss.dll` with the COM software provider, the requester and the registration (COM + `IVssAdmin::RegisterProvider`).
  - C exports: `bdfu_vss_prepare(json*)`, `bdfu_vss_freeze(timeoutMs)`, `bdfu_vss_thaw(json*)`, `bdfu_vss_abort()`, `bdfu_vss_register(path)`, `bdfu_vss_unregister()`.
  - The requester settings follow the qemu-ga and VMware pattern (`VSS_BT_COPY`). The context (`VSS_CTX_BACKUP` vs. qemu's `APP_ROLLBACK|…`) is settled in the spike: the SQL writer must take part.
- `src/Backdafuckup.Smb`: push client on SMBLibrary: ADMIN$ copy, plus our own `svcctl` NDR client (OpenSCManagerW, CreateServiceW, StartServiceW, QueryServiceStatus, DeleteService, CloseServiceHandle).

**Core (hypervisor-neutral, `src/Backdafuckup.Core`)**
- `IGuestQuiescer` → `FreezeAsync(target, log, ct)` returns an `IGuestFreeze?`. `ThawAsync()` returns a `GuestFreezeResult` (writer states, durations, error).
- `VmBackupRequest` (`Hypervisor/Model.cs`) gets an optional quiescer.
- `XenBackupSession.CreateAsync` (`src/Backdafuckup.Xen/XenBackupSession.cs`, `VM.snapshot` at ~line 110):
  - CBT enabling and record reads stay **before** the freeze.
  - Only `RunTaskAsync("VM.snapshot")` runs inside the window; the thaw follows right after.
- Manifest (`Repository/Manifest.cs`): `Consistency` (ApplicationConsistent / CrashConsistent + reason) and the blob `guest-metadata` (via `IRepositoryWriter.WriteBlobAsync`, as with `vm-metadata`).

**Server (`src/Backdafuckup.Server`)**
- **Agent API `/api/agent/*`** (long poll, header `X-Agent-Key`, exempt from cookie auth and `CsrfHeaderMiddleware`):
  - Commands: `GetInfo`, `PrepareBackup`, `Freeze(timeout)`, `Thaw`, `Abort`, `Update`.
  - The agent can only freeze and thaw; there are no file or command calls in this spec.
- **Registration:** `POST /api/agent/register` exchanges a one-time token for an agent ID plus secret. The secret is stored as a hash on the server and DPAPI-protected (LocalMachine) on the agent.
  - The token contains the server URL, the SPKI pin of the server certificate and the one-time secret, with an expiry.
- **Mapping:** BIOS serial or SMBIOS UUID (both byte orders) maps the agent to the inventory VM. Fallback: hostname/IP from `VM_guest_metrics`.
- **`AgentDeployer`:** a session of kind `AgentDeployment` with log lines naming IPs and ports.
  1. Target IP from `VM_guest_metrics.networks` (needs guest tools) or entered by hand.
  2. SMB 445 → ADMIN$\Temp\bdfu-setup-<id>.
  3. svcctl creates and starts a temporary service ("install …").
  4. Wait for the agent connection, clean up.
  5. Fallback from the .NET 10 to the 4.8 build if the first will not start.
  6. Update and uninstall use the same path.
- **Data (migration):**
  - New tables `agents` (VM, version, build, OS, last contact, secret hash) and `agent_registration_tokens`.
  - Windows credentials use the existing `credentials` / `CredentialProtector`.
  - Job fields: `GuestProcessing`, `GuestFailurePolicy` (CrashConsistentWithWarning | FailVm), `GuestCredentialId` (auto-install when the agent is missing).
- **Bundling:** the agent binaries (both builds + `bdfu-vss.dll`) come from the build into the server package and are served for push and download.

**Backup flow per VM (guest processing on), in `BackupSessionRunner`**
1. Agent online? If not and credentials are set: push install, then wait for the agent (timeout). Otherwise apply the policy.
2. `PrepareBackup`: writer metadata and SQL databases go to the log and to `guest-metadata`.
3. `XenBackupSession.CreateAsync`: CBT etc., then `Freeze`. The agent reports "frozen" once our provider holds in `CommitSnapshots`.
4. `VM.snapshot`, then `Thaw` right away. The log shows the freeze, snapshot and thaw durations and the writer states.
5. On error or timeout the snapshot counts as crash-consistent: a warning or a failed VM, depending on the policy. The snapshot stays usable.
6. Writer results and consistency go into the manifest and session metrics.

**Console (`src/Backdafuckup.Web`)**
- `/agents`: the VMs with agent status (not installed / installing / online / offline / outdated, plus the build), push install/update/uninstall, and a download of the package plus the token command line.
- Settings → Windows credentials.
- Job editor: a "Guest processing" section (on/off, policy, credentials).
- Session log, session page and restore points show "application-consistent" or "crash-consistent (reason)".

## Tasks

1. **Save the spec documentation** (`plan.md`, `shape.md` with the decisions above, `standards.md` (no standards yet, conventions from the foundation), `references.md` with the research sources, `visuals/`).
2. **Spike:**
   - Docker build of `bdfu-vss.dll` with mingw-w64 ≥ 10.
   - Measure `VM.snapshot` duration on the pool (read-only: the lab spike measures in the user's test lab).
   - Check the SMBIOS/BIOS serial on a Windows VM.
   - Settle the VSS context so the SQL writer takes part.
3. **Protocol + Core:** `IGuestQuiescer`, manifest consistency, guest metadata, with tests.
4. **Server:** agent registry, registration and tokens, hub auth, quiescer implementation, migration. Tests with an in-process fake agent (HTTP client against the test server).
5. **Backup integration:** freeze/thaw in `XenBackupSession`, policy in `BackupSessionRunner`, log lines and metrics.
   - Engine tests (fake pool + fake agent): success, timeout, failing writer, agent offline, policy FailVm.
6. **Agent service (both builds):** hosting, registration, reconnect, pinning, info (OS, volumes, SQL), P/Invoke layer. Logic behind interfaces, so it is testable on Linux.
7. **`bdfu-vss.dll` (C++):** provider, requester, registration, Docker build script and build integration.
8. **Push:** `Backdafuckup.Smb` (SMB + svcctl NDR with test vectors) and `AgentDeployer` incl. the 4.8 fallback, update and uninstall.
9. **UI:** agents page, credentials, job section, consistency display.
10. **Docs:**
    - Admin guide: ports 445 and 9555, UAC remote restrictions for local admins (`LocalAccountTokenFilterPolicy`), SMB signing and NTLM notes, EDR notes on PsExec-like installation, the 10 s window.
    - Lab runbook.

## Verification

- **Linux (automated):**
  - Unit tests (NDR marshaling, tokens, pinning, protocol) and engine tests with the fake agent for every policy case.
  - The full suite stays green; lab tests stay opt-in (`BDFU_LAB_RUN=1`).
  - The Docker build of `bdfu-vss.dll` runs in the build.
- **Lab (Windows Server 2022 + SQL Express, plus a Server 2012 R2 VM for the 4.8 build, in the team's test lab):**
  1. Push install with domain admin and with local admin.
  2. The agent shows online and is mapped to the right VM.
  3. Backup with guest processing: the log shows freeze, snapshot and thaw durations and `SqlServerWriter` OK.
  4. `msdb.dbo.backupset` shows `is_snapshot=1` and `is_copy_only=1`; Veeam's chain is untouched.
  5. An artificial snapshot delay over 10 s gives a crash-consistent backup with a warning.
  6. Uninstall leaves no service, files or VSS provider behind (`vssadmin list providers`).

## Risks

1. **10 s window vs. `VM.snapshot`** (shared LVM on Alletra, multi-disk VMs). Measure; inside the window only the snapshot runs. Exceeding it gives crash-consistent + warning.
2. **SMB push from Linux:**
   - Signing and encryption requirements (Server 2025 defaults), NTLM restrictions, Kerberos.
   - UAC for local admins.
   - EDR alerts on service installation via ADMIN$.
   - Fallback: the package with token via GPO or software distribution.
3. **VSS provider coexistence** with other providers and requesters (Veeam), and COM+ registration details.
4. **.NET 4.8 build:** dependencies must stay net48/netstandard2.0-compatible (long poll over `HttpClient` needs no extra packages). Server 2012 R2 needs .NET 4.8 installed (clear error if missing).
5. **Unsigned binaries** (SmartScreen/AppLocker); code signing later.
6. **Testability:** VSS and push can only be truly tested on Windows; the lab runbook is mandatory before the spec is done.

---

## Implementation status (2026-11-02)

Tasks 1–10 are implemented. The automated suite has 120 tests: 117 pass, and the 3 opt-in lab tests are skipped. Everything that needs Windows is still open and listed in the lab runbook below.

**What exists**
- **Protocol and agent core:**
  - `Backdafuckup.Agent.Protocol` (netstandard2.0).
  - `Backdafuckup.Agent.Core` (netstandard2.0): long poll, command state machine, self-update with checksum and loop guard, SPKI pin with its own DER reader (net48 has no `ExportSubjectPublicKeyInfo`), file hash.
- **Agent:** `Backdafuckup.Agent` (net10.0-windows self-contained single file, and net48): service, `install`/`uninstall`/`info`, WMI host info, P/Invoke to `bdfu-vss.dll`. Both builds compile on Linux.
- **`bdfu-vss.dll`** (`native/bdfu-vss`, mingw-w64 11 in Docker):
  - Requester: `VSS_BT_COPY`, context `APP_ROLLBACK|TRANSPORTABLE|NO_AUTORECOVERY|TXF_RECOVERY`, selectable components added, `SetBackupSucceeded` + `BackupComplete` + `DeleteSnapshots`.
  - Software provider: holds the writes in `CommitSnapshots`. `IsVolumeSupported` returns true only while our "frozen" event exists.
  - Registration: COM+ application (Local System) via `ImportComponent` plus `IVssAdmin::RegisterProvider`.
  - The DLL depends only on system DLLs.
- **Server:**
  - Agent API (enroll, poll, result, files) with `X-Agent-Key`, exempt from CSRF.
  - `AgentRegistry`: no updates during an operation.
  - `AgentQuiescer` and `GuestProcessingService` with the failure policy.
  - Manifest consistency and `guest-metadata`.
  - Push installation (`AgentDeployer`, session kind `AgentDeployment`).
  - Windows credentials and console pages.
- **SMB:** `Backdafuckup.Smb` uses SMBLibrary plus our own MS-SCMR client (NDR byte tests).
  - Checked against Samba 4.23 in Docker: NTLMv2 logon, error messages, folder and multi-chunk file writes.
  - Samba's svcctl was not available in that container, so the SCMR calls are only tested by byte vectors.
- **Packaging and signing:** `scripts/build-agent.sh` builds `artifacts/agent/{net10,net48}`. With `SIGNING_CERT_PFX` it signs with osslsigncode; the signing was verified end to end with a test certificate. `scripts/new-signing-cert.sh` creates the release certificate.
- **Docs:** admin guide section "Guest agent", `docs/defender.md`, THIRD-PARTY-NOTICES.

**Deviations from the plan**
- **Transport:** HTTPS long poll instead of SignalR (pattern of erSitzt/inventory).
- **Push installation:** creates the final service directly instead of a temporary setup service.
- **Agent key on disk:** protected by the folder ACL (SYSTEM + administrators) instead of DPAPI, as in inventory.
- **COM+:** `ImportComponent` of the self-registered class instead of `InstallComponent` with a type library. Ubuntu's mingw packages ship no `stdole2.tlb`.
- **Recovery actions:** the agent sets its own service recovery (`sc failure`, `failureflag`) at every service start, so a push installation (which only creates the service) gets it too.

## Lab runbook (Windows; mandatory before release)

Prepare a Windows Server 2022 VM with SQL Server Express, and a Server 2012 R2 VM with .NET Framework 4.8 (for the net48 build), both in the test lab.

1. **Push installation:**
   - *Guest Agents*: set the server address, add Windows credentials (domain admin; second run with `.\Administrator`), install the agent.
   - Expected: session Succeeded, agent online, VM mapped, VSS provider "registered".
   - Also check: `vssadmin list providers` shows "backdafuckup VSS provider", and the folder ACL allows SYSTEM and Administrators only.
2. **Backup with guest processing:**
   - The log shows "VSS prepared … SQL database(s)", then "application-consistent snapshot" with freeze, hold and snapshot times.
   - The restore point is marked "application".
   - `msdb.dbo.backupset` has a row with `is_snapshot = 1, is_copy_only = 1`.
   - In the Windows event log (VSS, SQLWRITER), the writers are Stable.
3. **Hold timeout:** with an artificially slow snapshot (e.g. many disks), or by holding thaw longer than 10 s, the result is a crash-consistent backup plus a warning with `VSS_E_HOLD_WRITES_TIMEOUT`. The VM stays responsive.
4. **Coexistence:** take a Veeam (or `diskshadow`) backup on the same VM while our agent is idle. It must use the system provider, and `vssadmin list shadows` must look normal.
5. **net48 build:** on Server 2012 R2 the push installation falls back to net48 (log line) and the backup works.
6. **Self-update:** build a new agent version, restart the server. The agent updates itself and keeps working, also after a push installation (`sc qfailure BackdafuckupAgent` shows the restart actions).
7. **Uninstall:** `backdafuckup-agent.exe uninstall` removes the service, provider and folder.
