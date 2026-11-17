# References for Scale-out Repositories

## Code in this repository

### Storage
- **`src/Backdafuckup.Storage/LocalDirectoryRepository.cs`:**
  - open and `.owner.lock`;
  - `Pin`;
  - `DeleteRestorePointAsync` (waits on `_gcMutex`);
  - GC (mark, protected sets, late marks, safety window);
  - `GetStorageStats`.
- **`LocalDirectoryRepository.Transfer.cs`:** `BeginImport` / `BeginSqlImport`, `PutEncodedChunkAsync`, `CommitAsync`, which rewrites the ids. This is the copy path that moves reuse with the same id.
- **`LocalDirectoryRepository.Io.cs`:** `TryDeduplicate`, the stripe locks, `StoreEncodedChunkAsync`.
- **`src/Backdafuckup.Core/Repository/RepositoryTransfer.cs`, `IBackupRepository.cs`:** `IRepositoryTransfer`, `IChunkLocator` (`PlanRead`, `ReadStoredAsync`).
- **`src/Backdafuckup.DataMover/DiskBackupPipeline.cs:105-116`:** an incremental clones its base's index, which is why a chain stays on one extent.

### Server
- **`src/Backdafuckup.Server/Engine/BackupSessionRunner.cs`:**
  - one repository per session (`:49`);
  - `FindBaseAsync` (`:1025`);
  - the restore point row (`:340`);
  - retention (`:217-258`).
- **`src/Backdafuckup.Server/Sql/SqlBackupService.cs`:**
  - the repository per run (`:288`);
  - `SqlBackupReceiver` with `Job.RepositoryId` (`:391`), which must become the extent;
  - SQL retention (`:765-850`).
- **`src/Backdafuckup.Server/Engine/EngineInfrastructure.cs`:** `RepositoryManager`, `EngineOptions`.
- **`src/Backdafuckup.Server/Engine/EngineServices.cs`:** dispatch, `HasCapacity`, `StartupRecovery`.
- **`src/Backdafuckup.Server/Engine/SessionQueue.cs`:** `EnqueueMaintenanceAsync`, `SessionTracker`.
- **`src/Backdafuckup.Server/Engine/TaskProgress.cs`:** throttled, ordered task progress.
- **`src/Backdafuckup.Server/Tape/TapeLocks.cs`:** `FifoLease`, which the chain leases reuse.
- **`src/Backdafuckup.Server/Api/InfrastructureEndpoints.cs`:** repository list, create/import, delete (its guards), rescan.
- **Readers that cache a repository id:**
  - `FileRestore/FileRestoreMounts.cs`
  - `Ad/AdExplores.cs`
  - `Sql/SqlPublishSessionRunner.cs`, `Sql/SqlRestoreSessionRunner.cs`
  - `Tape/TapeBackupRunner.cs`, `Tape/TapeRestoreRunner.cs`
- **Tape restore into a repository:** `Tape/TapeRestoreRunner.cs`. This is the precedent for copying a restore point between chunk stores.

### Console
- `src/Backdafuckup.Web/Pages/Repositories.razor`, `Pages/JobEditor.razor`.
- `Pages/Tape.razor`: tabs, 5 s refresh, confirmation dialogs.

## External references (public documentation, behaviour only)

**Veeam Backup & Replication user guide, "Scale-Out Backup Repository":**
- the data locality placement policy;
- extent seal mode and maintenance mode;
- evacuating backups from an extent;
- rebalancing.

These are ideas only. No code, binaries or file formats of the product are used.

**Proxmox Backup Server documentation:**
- datastore maintenance modes (read-only, offline);
- sync jobs between datastores (one chunk store per datastore, chunks copied only when missing).
