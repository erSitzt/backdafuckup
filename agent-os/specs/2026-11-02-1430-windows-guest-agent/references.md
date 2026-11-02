# References for the Windows Guest Agent

These references are documentation and open-source implementations, studied to understand behaviour. We write our own code; GPL code is for reading only.

## Freeze / VSS

### qemu guest agent, Windows VSS (qga/vss-win32)
- **Location:**
  - https://gitlab.com/qemu-project/qemu/-/tree/master/qga/vss-win32 (provider.cpp, requester.cpp, install.cpp, vss-handles.h)
- **License:** GPL-2.0-or-later. Read for understanding only.
- **Key patterns:**
  - **Events:** `Global\QGAVSSEvent-frozen`, `-thaw` and `-timeout`, accessible to SYSTEM and Administrators only.
  - **Requester:** InitializeForBackup → SetBackupState → SetContext → GatherWriterMetadata → AddComponents → StartSnapshotSet → AddToSnapshotSet → PrepareForBackup → DoSnapshotSet.
  - **Provider:** `CommitSnapshots` sets the frozen event and waits up to 60 s for thaw, then always returns S_OK.
  - **Timeouts:** `VSS_E_HOLD_WRITES_TIMEOUT` means "fsfreeze is limited up to 10 seconds".
  - **Build:** cross-compiled with mingw-w64 (Fedora `mingw64-gcc-c++`). Without the old VSS SDK it uses `vsadmin.h`/`vsprov.h` from mingw-w64 ≥ 10.

### XenServer win-xenvss / win-xenguestagent
- **Location:**
  - https://github.com/xenserver/win-xenvss
  - https://github.com/xenserver/win-xenguestagent
- **License:** BSD-2-Clause.
- **Key patterns:**
  - It is a hardware provider.
  - `CommitSnapshots` signalled dom0 through xenstore and waited for "snapshots-created".
  - The dom0 side was removed in XenServer 8.1, so this is a historic reference.

### Microsoft documentation
- **CommitSnapshots, 10 s flush-and-hold window:** https://learn.microsoft.com/en-us/windows/win32/api/vsprov/nf-vsprov-ivssprovidercreatesnapshotset-commitsnapshots
- **DoSnapshotSet errors** (`VSS_E_FLUSH_WRITES_TIMEOUT`, `VSS_E_HOLD_WRITES_TIMEOUT`): https://learn.microsoft.com/en-us/windows/win32/api/vsbackup/nf-vsbackup-ivssbackupcomponents-dosnapshotset
- **Writer freeze timeout (60 s default):** https://learn.microsoft.com/en-us/windows/win32/api/vswriter/nf-vswriter-cvsswriter-initialize
- **VSS_BACKUP_TYPE (copy vs. full):** https://learn.microsoft.com/en-us/windows/win32/api/vss/ne-vss-vss_backup_type
- **`CreateVssBackupComponents`, exported as `CreateVssBackupComponentsInternal`:** https://learn.microsoft.com/en-us/windows/win32/api/vsbackup/nf-vsbackup-createvssbackupcomponents
- **SQL Server VSS writer backup guide (copy-only, differential base):** https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/sql-server-vss-writer-backup-guide
- **Copy-only backups, `backupset.is_copy_only`:** https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/copy-only-backups-sql-server
- **Samples:** https://github.com/microsoft/Windows-classic-samples (MIT): VolumeShadowCopyServiceProvider, VShadow.

### mingw-w64 headers
- **Location:**
  - https://raw.githubusercontent.com/mingw-w64/mingw-w64/master/mingw-w64-headers/include/vsprov.h
  - https://raw.githubusercontent.com/mingw-w64/mingw-w64/master/mingw-w64-headers/include/vsadmin.h
- **Relevance:** no VSS SDK download is needed (mingw-w64 ≥ 10).

## SQL Server alternatives (not chosen, for the SQL spec)
- **T-SQL snapshot backup** (SQL Server 2022+): https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/create-a-transact-sql-snapshot-backup
- **SQL VDI headers and samples** (MIT): https://github.com/microsoft/sql-server-samples/tree/master/samples/features/sqlvdi

## XCP-ng identity
- **SMBIOS from hvmloader:** the type 1 UUID is the domain handle, and the serial number is the UUID string. https://raw.githubusercontent.com/xen-project/xen/master/tools/firmware/hvmloader/smbios.c
- **Byte-swapped SMBIOS UUID in guests:** https://xcp-ng.org/forum/topic/11078/vm-uuid-via-dmidecode-does-not-match-vm-id-in-xen-orchestra
- **`VM.generation_id`** (for the AD spec): https://github.com/xapi-project/xen-api/blob/master/ocaml/idl/datamodel_vm.ml

## Push installation
- **SMBLibrary** (LGPL-3.0, SMB 1/2/3 client and server, NTLM; Kerberos via Kerberos.NET): https://github.com/TalAloni/SMBLibrary
- **MS-SCMR** (Service Control Manager Remote Protocol, svcctl): https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-scmr/
- **MS-RPCE / NDR:** https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rpce/
- **MS-SMB2:** https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-smb2/

## erSitzt/inventory (the user's own project, MIT, `~/GIT/inventory`)
- `README.md`, section *Agent*: enrollment (`/api/agent/enroll`, `X-Agent-Key`, SHA-256 of the key on the server), long poll, self-update, setup ZIP with `Install-Agent.cmd`.
- `src/Inventory.Runner/` (AgentLoop, AgentService, AgentInstaller, Uninstaller): the agent service, multi-target `net48;net10.0-windows`.
- `src/Inventory.Remoting/Smb/SmbServiceTransport.cs`: ADMIN$ plus SCM. It is Windows-native (P/Invoke); backdafuckup needs a managed implementation for Linux.
- `scripts/new-signing-cert.sh`, `installer/sign.ps1`, `installer/signing/`: the self-signed release certificate.
- `docs/defender.md`: Defender for Endpoint (alerts, allow indicators, alert tuning, false-positive reports).

## Existing code in this repository
- `src/Backdafuckup.Xen/XenBackupSession.cs`: `CreateAsync` takes the snapshot, so freeze and thaw go around `VM.snapshot`.
- `src/Backdafuckup.Server/Engine/BackupSessionRunner.cs`: the per-VM backup flow, session log and metrics.
- `src/Backdafuckup.Server/Api/OperationEndpoints.cs`: `LiveHub`, the pattern for hubs.
- `src/Backdafuckup.Server/Program.cs`: authentication, `CsrfHeaderMiddleware`, hub mapping.
- `src/Backdafuckup.Xen/Tls/CertificatePinning.cs`: SPKI pinning, reused for the agent's server pin.
