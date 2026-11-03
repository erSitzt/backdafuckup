# References for SQL Server Restore and Publish

## Similar implementations in this codebase

### File-level restore (FLR)
- **Location:** `src/Backdafuckup.FileLevel`, `src/Backdafuckup.Server/FileRestore`
- **Relevance:** reading NTFS volumes and files from a restore point, drive letters from `MountedDevices`, the restore session pattern, the ZIP export, one-time download links.
- **Key patterns:** `FileRestoreMounts` (pin, leases, audit), `FileRestoreSessionRunner` (sessions, `RestoreProgress`), `GuestZipExport`, `FileRestoreDownloads`.

### Agent transfer (restore into a Windows VM)
- **Location:** `src/Backdafuckup.Server/FileRestore/AgentTransfers.cs`, `AgentTargetPaths.cs`, `src/Backdafuckup.Agent.Core/FileRestoreTransfer.cs`, `RestoreFileSystem.cs`, `src/Backdafuckup.Agent/WindowsRestoreFileSystem.cs`
- **Relevance:** streaming files into the VM with SHA-256, path guards, capability gating, the pull-style channel the SQL jobs copy.

### Guest agent and VSS
- **Location:** `native/bdfu-vss/bdfu-vss.cpp`, `src/Backdafuckup.Agent/NativeVss.cs`, `src/Backdafuckup.Server/Agents`
- **Relevance:** SQL writer metadata at backup time, where the VSS documents get saved, agent registry and commands.

### Credentials
- **Location:** `src/Backdafuckup.Server/Api/LinuxCredentialEndpoints.cs`, `Data/Entities.cs` (`CredentialKind`, protected JSON secrets)
- **Relevance:** the template for `CredentialKind.SqlServer`.

## External sources

### SQL Server
- SQL Server VSS writer backup guide (components, restore through the writer, autorecovery of snapshot files): https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/sql-server-vss-writer-backup-guide
- SQL Writer service (supported scenarios, NT Service\SQLWriter): https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/sql-writer-service
- VSS restore processing (saved Backup Components Document and writer metadata): https://learn.microsoft.com/en-us/windows/win32/vss/overview-of-processing-a-restore-under-vss
- Attach a database (permissions, file access): https://learn.microsoft.com/en-us/sql/relational-databases/databases/attach-a-database
- CREATE DATABASE … FOR ATTACH: https://learn.microsoft.com/en-us/sql/t-sql/statements/create-database-transact-sql
- Upgrade by detach and attach (versions): https://learn.microsoft.com/en-us/sql/relational-databases/databases/upgrade-a-database-using-detach-and-attach-transact-sql
- Move a TDE-protected database: https://learn.microsoft.com/en-us/sql/relational-databases/security/encryption/move-a-tde-protected-database-to-another-sql-server
- BACKUP / RESTORE permissions: https://learn.microsoft.com/en-us/sql/t-sql/statements/backup-transact-sql, https://learn.microsoft.com/en-us/sql/t-sql/statements/restore-statements-transact-sql
- Service accounts and provisioning (NT AUTHORITY\SYSTEM is not sysadmin since 2012): https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/configure-windows-service-accounts-and-permissions
- Editions and limits (Express 10 GB / 50 GB on 2025): https://learn.microsoft.com/en-us/sql/sql-server/editions-and-components-of-sql-server-2025
- T-SQL snapshot backup (SQL 2022+, an alternative for phase 2): https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/create-a-transact-sql-snapshot-backup
- SQL VDI samples (MIT): https://github.com/microsoft/sql-server-samples/tree/master/samples/features/sqlvdi

### Veeam (behavior to match, public docs only)
- How publishing works: https://helpcenter.veeam.com/archive/backup/120/explorers/vesql_how_publishing_works.html
- Mount operations (iSCSI, C:\VeeamFLR): https://helpcenter.veeam.com/docs/vbr/userguide/vesql_mount_operations.html
- How restore works: https://helpcenter.veeam.com/docs/vbr/explorers/vesql_how_restore_works.html
- Export to .bak (staging server): https://helpcenter.veeam.com/docs/backup/explorers/vesql_exporting_bak.html
- System requirements (staging same or later version, Express 10 GB): https://helpcenter.veeam.com/archive/backup/120/explorers/vesql_systemreqs.html
- Permissions: https://helpcenter.veeam.com/archive/backup/120/explorers/vesql_permissions.html
- Ports (iSCSI 3260–3270): https://helpcenter.veeam.com/backup/howtosql/how2_sql_prep_step1_check_ports.html

### iSCSI and Windows disks
- Tal Aloni ISCSI target (LGPL-3.0-or-later): https://github.com/TalAloni/iSCSIConsole, https://www.nuget.org/packages/ISCSI
- DiskAccessLibrary (`Disk` base class): https://github.com/TalAloni/DiskAccessLibrary, https://www.nuget.org/packages/DiskAccessLibrary
- Loopback use reported with the Windows initiator: https://github.com/TalAloni/iSCSIConsole/issues/36
- No CHAP in the library: https://github.com/TalAloni/iSCSIConsole/issues/22
- Loopback initiator (Microsoft's own target): https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2012-r2-and-2012/dn305893(v=ws.11)
- iscsidsc API (`LoginIScsiTargetW`): https://learn.microsoft.com/en-us/windows/win32/api/iscsidsc/nf-iscsidsc-loginiscsitargetw
- SAN policy (OfflineShared): https://learn.microsoft.com/en-us/windows/win32/api/vds/ne-vds-vds_san_policy
- Disk signature collisions: https://learn.microsoft.com/en-us/archive/blogs/markrussinovich/fixing-disk-signature-collisions
- Mount a partition into a folder: https://learn.microsoft.com/en-us/powershell/module/storage/add-partitionaccesspath

### Libraries
- Microsoft.Data.SqlClient (MIT): https://www.nuget.org/packages/Microsoft.Data.SqlClient
- Testcontainers.MsSql (MIT): https://www.nuget.org/packages/Testcontainers.MsSql
- mssql container image (Developer edition: development and test only): https://hub.docker.com/r/microsoft/mssql-server
