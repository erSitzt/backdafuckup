# References for Backup to Tape

## Code in this repository

### Repository
- **`src/Backdafuckup.Storage/LocalDirectoryRepository.cs`:**
  - GC mark (`CollectGarbageAsync`) and `VerifyAsync`: the chunk walk to extract;
  - restore point layout; pins.
- **`LocalDirectoryRepository.Io.cs`:** `StoreChunkAsync` (next to the new `PutEncodedChunkAsync`), `CommitAsync` (repository id and chunk size checks).
- **`LocalDirectoryRepository.Sql.cs`:** SQL backup objects and their indexes.
- **`src/Backdafuckup.Core/Repository/`:**
  - `IBackupRepository.cs` (`IRestorePointReader`, `IChunkReader`, `IRepositoryWriter`, `IRestorePointPin`);
  - `Manifest.cs`, `FileTree.cs` (`FileTreeWalker.Visit`), `SqlBackups.cs`.

### Engine and catalog
- **`src/Backdafuckup.Server/Engine/EngineServices.cs`:**
  - `SessionRunnerService` (`HasCapacity`, dispatch, the `LastResult` hook);
  - `JobScheduler.TickAsync`, `StartupRecovery`.
- **`SessionQueue.cs`:** enqueue patterns, `SessionTracker`.
- **`BackupSessionRunner.cs`:** `ApplyRetentionAsync`, `ApplyManifest`.
- **`RestoreSessionRunner.cs`:** `RestorePointAsync` (to split for the direct restore), maintenance sessions.
- **`RestoreSessionRunner.Cluster.cs`, `ClusterRuns.cs`:** whole-cluster runs found by session.
- **`EngineInfrastructure.cs`:** `RepositoryManager`, `SessionEventWriter`, `ILiveNotifier`.
- **`src/Backdafuckup.Server/Sql/SqlBackupService.cs`:** SQL retention (to extract into `SqlRetentionWindow`), `SqlBackupCatalog`, `SqlCatalogBackfill`.
- **`src/Backdafuckup.Server/Data/Entities.cs`, `AppDbContext.cs`, `DataServices.cs` (`CredentialProtector`).**
- **`src/Backdafuckup.Server/Api/InfrastructureEndpoints.cs`:** repository import and catalog (the model for the tape import), the delete guard.
- **`src/Backdafuckup.Xen/XenRestoreSession.cs`:** sequential `import_raw_vdi`.

### Console
- **Navigation:** `src/Backdafuckup.Web/Layout/NavMenu.razor`, `Layout/MainLayout.razor` (banner).
- **Pages:** `Pages/JobEditor.razor` (kind toggle), `Pages/Jobs.razor`, `Pages/Sessions.razor`.
- **Restore point views and restore wizards:** under `Pages/` and `Components/`.
- **Session kind display:** `Services/AuthState.cs`, `Components/ObjectKindUi.cs`.

### Telemetry and monitoring
- `src/Backdafuckup.Server/Telemetry/BackupMetrics.cs`, `StateMetrics.cs`.
- `deploy/monitoring/backdafuckup.rules.yml`, `prometheusrule.yaml`, `grafana-dashboard.json`.

## External references (public documentation, behaviour only)

- **Proxmox Backup Server** (AGPL-3.0), tape backup:
  - media pools with allocation and retention policies;
  - media sets, the media catalog on tape;
  - chunk archives and snapshot archives;
  - restore that needs the set's tapes.
- **Bareos and Bacula** (AGPL-3.0): volume labels, catalog rebuild by scanning tapes (`bscan`), end-of-medium handling.
- **Veeam** public user guide, only for UI ideas: media pools, GFS pools, the tape infrastructure tree, backup-to-tape jobs. No code, formats or binaries.
- **Cryptography:**
  - NIST SP 800-38D (AES-GCM);
  - RFC 9106 (Argon2), with Konscious.Security.Cryptography (MIT) as the implementation;
  - RFC 5869 (HKDF).
- **T10 SSC** (data encryption pages for hardware encryption) and SPC (MAM attributes), as in the device-layer spec.

Nothing is copied from purchased products.
