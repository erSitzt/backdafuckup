# References for Active Directory Backup and Object-Level Restore

## Similar implementations in this codebase

### Guest metadata in a restore point (SQL)
- **Location:** `src/Backdafuckup.Server/Sql/StoredGuestMetadata.cs` (`StoredGuestMetadata`, `StoredSqlDatabase`, blob `guest-metadata`); written in `src/Backdafuckup.Server/Engine/BackupSessionRunner.cs:253-299`; count field and backfill in `SqlCatalogBackfill.cs`.
- **Relevance:** the `ActiveDirectory` section (`StoredAdDatabase`) follows the same pattern.
- **Key patterns:**
  - versioned blob with an upgrade on read;
  - per-application consistency from the writer state;
  - a manifest count copied to the catalog;
  - backfill for older points.

### VSS in the agent and the server
- **Location:** `native/bdfu-vss/bdfu-vss.cpp` (`DescribeWriters`, `SetBackupState`, `BackupComplete`), `src/Backdafuckup.Agent/NativeVss.cs` (`Prepare`, `ToWriter`), `src/Backdafuckup.Server/Agents/AgentQuiescer.cs` (`SendAsync`, `CopyOnly`).
- **Relevance:**
  - the NTDS writer is already added generically, but its files are dropped in `ToWriter`;
  - the backup type comes from the server's `CopyOnly`.

### SQL browser on a restore point
- **Location:** `src/Backdafuckup.Server/Sql/SqlCatalog.cs` (`DescribeAsync`, `ReadInstances`: SYSTEM/SOFTWARE hives via DiscUtils.Registry, `Select\Current`), `src/Backdafuckup.Web/Pages/SqlBrowser.razor`.
- **Relevance:** DC detection from the SYSTEM hive (`Services\NTDS\Parameters`, `Netlogon\Parameters\SysVol`); the browser page pattern.

### File-level reader and transfer into the guest
- **Location:**
  - `src/Backdafuckup.Server/FileRestore/FileRestoreMounts.cs` (`OpenDisksAsync`, `OpenNewAsync`);
  - `src/Backdafuckup.FileLevel/GuestImage.cs`, `IGuestFileSystem.cs` (`OpenRead`, `GetSecurityDescriptor`);
  - `src/Backdafuckup.Server/FileRestore/AgentTargetPaths.cs` (`AgentFilePlan.Plan`), the restore-files transfer used by `SqlRestoreSessionRunner.cs:174-222`.
- **Relevance:** ntds.dit and its logs, and the GPT folders of SYSVOL, read from the restore point and written into the target DC with SHA-256 checks; SYSVOL ACLs from the backup.

### SQL job framework and session runners
- **Location:**
  - server: `src/Backdafuckup.Server/Sql/SqlJobs.cs`, `SqlRestoreSessionRunner.cs`, `SqlPublishSessionRunner.cs`, `SqlRestoreRecovery.cs`, `SqlRestoreCleanup.cs`, `SqlPublishLeftovers.cs`;
  - agent: `src/Backdafuckup.Agent.Core/SqlJobs.cs` (`SqlJobRunner`), `AgentRunner.cs` (`StartSqlJob`, capabilities, journals), `src/Backdafuckup.Agent/WindowsLogon.cs`;
  - tests: `tests/Backdafuckup.Server.Tests/ScriptedSql.cs`.
- **Relevance:** `AdJobs`/`AdJobRunner` with request, progress, result and log lines; restart detection (run id); journals cleaned up at agent start; accounts via `WindowsLogon`; a scripted environment for API tests with the real agent runner.

### Console patterns
- **Location:** `src/Backdafuckup.Web/Pages/Backups.razor` (Restore menu entry, chips), `SqlBrowser.razor`, `SqlDatabase.razor`, `src/Backdafuckup.Web/Services/SqlTargetChecks.cs`.
- **Relevance:** the restore point entry, the browser layout, the shared checks before a start.

## External references (public documentation)

- **Microsoft:**
  - AD DS Database Mounting Tool (dsamain) step-by-step guide: https://technet.microsoft.com/en-us/library/cc753609
  - Restoring deleted Active Directory objects (tombstone reanimation, Recycle Bin): Microsoft Learn, "Active Directory Recycle Bin" and [MS-ADTS] 3.1.1.5.3.7 (undelete operation).
  - VM-GenerationID and virtualization-safe DCs: Microsoft Learn, "Virtualized domain controller architecture".
- **Other products, for comparison only (no code):**
  - Veeam: https://helpcenter.veeam.com/docs/backup/explorers/vead_considerations.html?ver=110
  - Quest RMAD: https://support.quest.com/technical-documents/recovery-manager-for-ad/10.1/user-guide/5
