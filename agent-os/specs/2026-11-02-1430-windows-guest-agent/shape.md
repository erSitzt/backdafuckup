# Windows Guest Agent — Shaping Notes

## Scope

A Windows agent inside the guest that makes VM backups application-consistent. It freezes I/O through VSS while the backup server takes the XAPI `VM.snapshot`, and releases it right after.

The spec also covers:
- **Installation:** push installation from the console with Windows admin credentials, and a self-installing package with a registration token for GPO or software distribution.
- **Console:** a page for the agents, a "Guest processing" section in backup jobs, and the consistency of every restore point.

The agent is the foundation for the next knock-out features, which follow as separate specs in this order: file-level restore, then SQL Server restore, then AD object restore.

### Out of scope (later specs)
- File-level restore (FLR):
  - Restore by browser download, back into the VM, or to a network share.
  - File systems: NTFS (GPT/MBR) and ext4/XFS with LVM.
- SQL Server restore into an instance and export as `.bak`.
- T-log backups, log truncation, point-in-time restore.
- AD object restore.
- A Linux agent (fsfreeze), pre/post scripts, an MSI package, code signing.

## Decisions

### Confirmed by the user (2026-11-02)
- **Priorities:** the basic technical features are knock-out criteria and come before notifications.
- **Order:** guest agent → FLR → SQL → AD.
- **Deployment:** push installation is part of this spec, not a later step.
- **Connection direction:** the agent connects outbound to the backup server over HTTPS (port 9555). No inbound firewall rule is needed in the VM.
- **When the freeze fails or takes too long:** the default per job is a crash-consistent backup plus a warning that names the reason. The job can be switched to "fail the VM".
- **VSS backup type:** copy-only, so that other backup products on the same VMs (Veeam runs in parallel) keep their SQL backup chains and log handling.
- **Platforms:** Windows first; Linux later.
- **Builds:** "prefer .NET 10, fall back to 4.8, build both agents". The push installation tries the .NET 10 build first.
- **Manual deletes:** nothing in the XCP-ng pool is deleted by hand. The product only removes objects it created and tagged itself.

### Adopted from the user's project erSitzt/inventory (MIT, `~/GIT/inventory`)

The user's hint was: "Code signing und SmartScreen/Defender-Issues hatten wir bei erSitzt/inventory auch, sind aber jetzt eigentlich behoben, einfach dort schauen."

- **Signing:** a self-signed release certificate, with the `.cer` file committed; Defender allow indicators and certificate distribution are documented.
- **Agent:** a long-poll agent (enroll/key/poll), a fixed service name and folder, self-update by poll, and a setup ZIP for GPO.

### Technical decisions (from research)
- **Freeze mechanism:**
  - We write our own VSS software provider. In `CommitSnapshots` it signals "frozen" and blocks until "thaw" or a timeout, as qemu-ga and the old XenServer `win-xenvss` provider do.
  - The requester and provider live in one native DLL (`bdfu-vss.dll`), cross-compiled with mingw-w64 ≥ 10 on Linux.
  - Both agent builds use the DLL via P/Invoke.
- **Time limit:** VSS releases flush-and-hold after 10 s. Only `VM.snapshot` runs inside the freeze window; everything slow (enabling CBT, record reads) happens before it.
- **VM mapping:** the BIOS serial number (= VM UUID); the SMBIOS UUID is compared in both byte orders.
- **Push from Linux:**
  - SMBLibrary (LGPL-3.0) handles SMB2/3 and NTLM.
  - Our own svcctl RPC calls create a temporary setup service (PsExec model).
- **Trust:** the registration token carries the server URL, the SPKI pin of the server certificate and a one-time secret. The agent secret is stored hashed on the server and DPAPI-protected on the agent.
- **Agent permissions:** the agent API only offers info, prepare, freeze, thaw, abort and update. No file or command execution in this spec.

## Context

- **Visuals:** none.
- **References:** see `references.md`. Existing code: `XenBackupSession.CreateAsync` (snapshot), `BackupSessionRunner` (per-VM flow), `CredentialProtector`, `LiveHub`, `CsrfHeaderMiddleware`, `IRepositoryWriter.WriteBlobAsync`.
- **Product alignment:** the roadmap's MVP item "Application awareness" says it needs our own in-guest VSS requester, because quiesce was removed in XenServer / XCP-ng 8.1.

## Standards applied

There are no formal standards yet (`agent-os/standards/index.yml` is empty). The conventions from the foundation spec apply; see `standards.md`.
