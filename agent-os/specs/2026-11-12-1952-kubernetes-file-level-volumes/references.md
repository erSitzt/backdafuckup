# References for File-level Backup of Kubernetes Volumes

## Code in this repository

### Backup path today
- **Data path decision:** `src/Backdafuckup.Kubernetes/KubernetesInventory.cs` (`DataPathOf`, `DataPaths`).
- **Namespace backup and session contract:** `KubernetesNamespaceBackup.cs`; `IVmBackupSession` in `src/Backdafuckup.Core/Hypervisor/IHypervisorProvider.cs`.
- **Snapshot export and ledger:** `VolumeSnapshots.cs` (`VolumeSnapshotFlow.DeleteAsync` knows no PersistentVolume yet), `VolumeExport.cs`.
- **Mover pods:** `MoverPods.cs` (`Build`, `BuildFiles`). Mover program: `src/Backdafuckup.Mover` (`MoverRunner`, `VolumeFiles` writes files only).
- **Channels:** `src/Backdafuckup.Server/Movers/MoverChannels.cs` (`BackupChannel`, `FilesChannel`) and `MoverEndpoints.cs` (`PUT /data` accepts only `BackupChannel`).
- **Engine:** `src/Backdafuckup.Server/Engine/BackupSessionRunner.cs` (`BackupDiskAsync`, `ApplyManifest`, consistency); `src/Backdafuckup.DataMover/DiskBackupPipeline.cs`.

### Repository
- **`src/Backdafuckup.Core/Repository/Manifest.cs`:** the manifest, `KubernetesVolumeRecord`, `CurrentFormatVersion`.
- **Storage:**
  - `src/Backdafuckup.Storage/LocalDirectoryRepository*.cs`: GC mark over `AllImages` and SQL files, verify;
  - `LocalDirectoryRepository.Sql.cs` `WriteFileAsync`: a stream into chunks.
- **Formats:** `ChunkIndex`, `ChunkIndexFormat`, `ChunkCodec` (chunks of any length up to the chunk size).

### Read side
- **Interfaces:**
  - `src/Backdafuckup.FileLevel/IGuestFileSystem.cs`, `GuestEntry.cs`;
  - `GuestImage.cs`, today the only provider, hard-coded at the call sites in `FileRestoreMounts.cs`, `BrowseMount.cs`, `FileRestoreDownloads.cs`, `FileRestoreSessionRunner.cs`, `VolumeFileSender.cs`, `FileRestoreEndpoints.cs`, `AgentTransfers.cs`.
- **Restore paths:**
  - `FileRestorePipeline`;
  - `MoverFilesTarget` and the `BDFUFIL1` stream (`src/Backdafuckup.Core/DataMover/MoverFiles.cs`);
  - `VolumeFilesImport` (files into a new PVC), `PvcFileImport` (files back into a live PVC).

### Hooks and permissions
- **Hooks:** `KubernetesHooks.cs` (Velero annotations; `ClientPodExecutor` has no stdin yet).
- **Permissions:** `KubernetesSetup.cs` (the roles, the work namespace at Pod Security baseline).

## External references (public documentation, behaviour only)
- **Kubernetes:**
  - persistent volumes (static PVs, `claimRef` pre-binding, NFS, hostPath, local);
  - Pod Security Standards (baseline allows `DAC_OVERRIDE`, `SETUID`, `SETGID`);
  - mount propagation;
  - the kubelet's authorization (`nodes/stats`) and the Summary API.
- **Linux:** `statx(2)`, `getdents64(2)`, `openat(2)`, `setfsuid(2)`, `lseek(2)` with `SEEK_HOLE`.
- **Velero** (Apache-2.0): file system backup with its node agent, and the annotations `backup.velero.io/backup-volumes` and `…-excludes`. Read for behaviour only, nothing copied.
- **Prometheus metrics:** cAdvisor `container_fs_usage_bytes`, `kubelet_volume_stats_used_bytes`, and the ephemeral-storage exporter's metrics, used as optional, configurable sources.
