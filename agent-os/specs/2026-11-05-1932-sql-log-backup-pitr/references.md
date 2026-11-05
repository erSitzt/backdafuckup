# References for SQL Server Log Backups and Point-in-Time Restore

## Similar implementations in this codebase

### SQL Server snapshot (image full, `.bkm`)
- **Location:** `src/Backdafuckup.Agent.Core/SqlSnapshot.cs`, `src/Backdafuckup.Server/Agents/AgentQuiescer.cs`, `src/Backdafuckup.Server/Sql/StoredGuestMetadata.cs` (`StoredSqlSnapshot`)
- **Relevance:**
  - the chain base for 2022+;
  - backup set facts read from `msdb` (`BackupSetQuery`);
  - log chain warnings (`LogChainWarnings`).
- **Test:** `tests/Backdafuckup.Sql.Tests/SqlSnapshotTests.cs:103` proves the restore path `.bkm` → logs → `STOPAT` in a container.

### SQL restore and publish (phase 1)
- **Location:**
  - server: `src/Backdafuckup.Server/Sql/SqlRestoreSessionRunner.cs`, `SqlPublishSessionRunner.cs`, `SqlRestoreRecovery.cs`, `SqlJobs.cs`;
  - agent: `src/Backdafuckup.Agent.Core/SqlJobs.cs`, `SqlAttach.cs`, `SqlReplace.cs`, `Publishing.cs`.
- **Relevance:**
  - the SQL job channel (request, progress, result, restart detection);
  - prechecks, the overwrite swap with rollback, publish through loopback iSCSI;
  - cleanup after agent and server restarts.

### Export file store and upload
- **Location:** `src/Backdafuckup.Server/Sql/SqlExportFiles.cs`, `Agents/AgentEndpoints.cs` (`PUT api/agent/sql-jobs/{id}/file`)
- **Relevance:**
  - streaming uploads from the agent (`DisableRequestSizeLimit`);
  - one-time download tickets.

### Repository
- **Location:** `src/Backdafuckup.Core/Repository/IBackupRepository.cs`, `Manifest.cs`, `src/Backdafuckup.Storage/LocalDirectoryRepository*.cs`, `ChunkCodec.cs`
- **Relevance:**
  - chunk store, manifests as commit points;
  - pins, GC (mark and sweep), verify, import (`Api/InfrastructureEndpoints.cs` `ImportRestorePointsAsync`).

### Scheduling and sessions
- **Location:** `src/Backdafuckup.Server/Engine/EngineServices.cs` (`JobScheduler`, `SessionRunnerService`, `StartupRecovery`), `ScheduleCalculator.cs`, `SessionQueue.cs`, `EngineInfrastructure.cs` (`SessionEventWriter`)
- **Relevance:**
  - schedules in a time zone;
  - the session lifecycle, keyed log lines and the task roll-up.

### Console
- **Location:** `src/Backdafuckup.Web/Pages/SqlBrowser.razor`, `JobEditor.razor`, `SessionDetail.razor`, `Sessions.razor`, `Backups.razor`
- **Relevance:**
  - the restore and publish dialog to extract;
  - the guest processing panel;
  - the session pages.

## External sources (public)

### SQL Server
- BACKUP (LOG, COMPRESSION, CHECKSUM, NAME/DESCRIPTION, permissions): https://learn.microsoft.com/en-us/sql/t-sql/statements/backup-transact-sql
- RESTORE (STOPAT, NORECOVERY, MOVE), RESTORE FILELISTONLY/HEADERONLY: https://learn.microsoft.com/en-us/sql/t-sql/statements/restore-statements-transact-sql
- Restore to a point in time: https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/restore-a-sql-server-database-to-a-point-in-time-full-recovery-model
- Tail-log backups: https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/tail-log-backups-sql-server
- Log chain and recovery paths (forks): https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/recovery-paths
- `msdb.dbo.backupset` (LSNs, `backup_set_uuid`, `family_guid`, `database_guid`, `time_zone`): https://learn.microsoft.com/en-us/sql/relational-databases/system-tables/backupset-transact-sql
- `sys.database_recovery_status` (`last_log_backup_lsn`, `recovery_fork_guid`): https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-database-recovery-status-transact-sql
- T-SQL snapshot backup (SQL 2022+): https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/create-a-transact-sql-snapshot-backup
- Back up and restore system databases: https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/back-up-and-restore-of-system-databases-sql-server

### User report
- "MSSQL-Sicherung auf XCP-ng – Zielarchitektur & Spezifikation für die Eigenentwicklung" (requirements A1–A13, chain rules, appendix A: T-SQL for the agent, appendix B: catalog model). Its parts derived from decompiled Veeam code or Veeam logs serve only as comparison; only public sources are adopted.
