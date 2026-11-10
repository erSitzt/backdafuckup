# References for Snapshot with Memory

## In this codebase

### Backup snapshot
- **Location:** `src/Backdafuckup.Xen/XenBackupSession.cs` (`CreateAsync`, local `SnapshotAsync`, `ExportMetadataAsync`, `CommitAsync`, `RollbackAsync`), `src/Backdafuckup.Xen/XenHypervisorSession.Backup.cs` (`DestroySnapshotVmAsync`, `CleanupArtifactAsync`, `FindTaggedArtifactsAsync`)
- **Relevance:** where `VM.checkpoint` replaces `VM.snapshot`, where the suspend VDI is found, read, recorded and destroyed.

### Server backup task
- **Location:** `src/Backdafuckup.Server/Engine/BackupSessionRunner.cs` (`VmBackupTask.RunCoreAsync`, `BackupDiskAsync`, `CheckConsistency`, `AcrossDisks`, `ApplyManifest`)
- **Relevance:** the memory image goes through `BackupDiskAsync` and into `manifest.Memory`; consistency for memory points.

### Restore
- **Location:** `src/Backdafuckup.Xen/XenRestoreSession.cs` (`CreateVdiAsync`, `CreateVmAsync` with `import_metadata`, `ReconfigureNicsAsync`, `CompleteAsync` power-on), `src/Backdafuckup.Server/Engine/RestoreSessionRunner.cs`, `src/Backdafuckup.Web/Pages/RestoreWizard.razor`
- **Relevance:** the suspend VDI, the Suspended VM, VIF removal for copies, `VM.resume`.

### Job options
- **Location:** `BackupJobObject` in `src/Backdafuckup.Server/Data/Entities.cs`, `JobObjectDto` / `SaveJobDto` in `src/Backdafuckup.Contracts/Api.cs`, `OperationEndpoints.Apply`, `src/Backdafuckup.Web/Pages/JobEditor.razor`
- **Pattern:** the `SqlSnapshotCopyOnly` option (migration `AddSqlSnapshotCopyOnly`) and the restore point flag `DomainController` (migration `AddDomainControllers`).

### Repository
- **Location:** `src/Backdafuckup.Core/Repository/Manifest.cs`, `src/Backdafuckup.Storage/LocalDirectoryRepository.cs` (`CollectGarbageAsync`, `VerifyAsync`), `LocalDirectoryRepository.Io.cs` (`ReadDiskIndexAsync`)
- **Relevance:** the memory image's index must be kept and verified like a disk's.

### Fake pool
- **Location:** `tests/Backdafuckup.Testing/Xapi/FakeXenPool.cs`
- **Relevance:** `VM.checkpoint`, `suspend_VDI`, Suspended import, `VM.resume`.

## Outside

- **XAPI:** `VM.checkpoint`, `VM.resume`/`resume_on`, VM fields `suspend_VDI`, `suspend_SR`, `last_booted_record`, `last_boot_CPU_flags`, VDI type `suspend` (xapi-project.github.io).
- **Xen Orchestra** (AGPL, read only): `@xen-orchestra/xapi/vm.mjs` (`checkpoint`, `create` with `suspend_VDI`), `@xen-orchestra/backups/_incrementalVm.mjs` (suspend VDI read and restored), docs "backup features and settings" (`xo-memory-backup`).
