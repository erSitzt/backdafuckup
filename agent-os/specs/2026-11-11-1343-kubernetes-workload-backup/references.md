# References for Kubernetes Workload Backup

## Code in this repository

### Provider abstraction (to be generalized)
- **`src/Backdafuckup.Core/Hypervisor/IHypervisorProvider.cs`:**
  - `IHypervisorProvider`, `IHypervisorSession` (inventory, `BeginBackupAsync`, `BeginRestoreAsync`, artifact cleanup);
  - `IVmBackupSession` (disks, `OpenReaderAsync`, change tokens, metadata blob);
  - `IVmRestoreSession`.
- **`src/Backdafuckup.Core/Hypervisor/Model.cs`:** `HypervisorKind` (only `XenServer`), `VmDescriptor`, `VmRestorePlan`, `InventoryKind`.
- **Xen-only guards:**
  - `src/Backdafuckup.Server/Engine/EngineServices.cs` registers a provider only if none is registered;
  - `ConnectionManager` in `EngineInfrastructure.cs` (`BuildSettings` with Xen options, `NetworkCredential`);
  - `InfrastructureEndpoints` hard-codes `HypervisorKind.XenServer`.
- **`src/Backdafuckup.Core/Operations/Operations.cs`:** `IArtifactLedger` / `ProviderArtifact`, the crash-safety ledger for VolumeSnapshots, PVCs and pods.

### Repository
- **`src/Backdafuckup.Core/Repository/Manifest.cs`:** `RestorePointManifest` (`Vm` required today, `Disks`, the blobs `Metadata`/`Guest`/…); a new property for `k8s-resources`.
- **`src/Backdafuckup.Storage/LocalDirectoryRepository*.cs`:**
  - fixed chunks with SHA-256 ids, zstd, zero chunks not stored;
  - `WriteBlobAsync`;
  - GC (mark-and-sweep over `AllImages`); `VerifyAsync` (blobs not verified yet).
- **`src/Backdafuckup.Core/Repository/SqlBackups.cs`:** `WriteFileAsync`, a stream of any length into chunks. The pattern for a later file store (NFS, Phase 3).

### Data movement
- **`src/Backdafuckup.Core/DataMover/Abstractions.cs`:**
  - `ISequentialDiskReader` (strictly increasing offsets; gaps are zeros), the backup source of a mover push;
  - `IDiskWriter`, `IDiskImageSource` / `RestorePointDiskImage` (`DataExtents`) for the restore pull.
- **`src/Backdafuckup.DataMover/DiskBackupPipeline.cs`:** hashing, dedup, base index, `DiskBackupCounters`.
- **`src/Backdafuckup.Server/Agents/AgentEndpoints.cs`:** `PUT api/agent/sql-jobs/{id}/backups/{fileId}` with `SqlBackupReceiver`, the model for a mover push endpoint with one-time tokens.

### Agent
- **`src/Backdafuckup.Agent.Protocol/AgentProtocol.cs`, `src/Backdafuckup.Agent.Core` (netstandard2.0):**
  - enroll, `X-Agent-Key`, a long poll of 50 s, `ServerPins`;
  - side channels for long work.

  The packaged agent is Windows-only (VSS, SMB push installation, self-update by file swap).

### Restore, browse, explore
- **`src/Backdafuckup.FileLevel/GuestImage.cs`:**
  - DiscUtils with MBR/GPT, LVM, NTFS, FAT, ext2/3/4, XFS (vendored in `src/ThirdParty`);
  - an unpartitioned file system becomes a whole-disk volume (needs a fixture).
- **`src/Backdafuckup.Server/FileRestore/`:**
  - `FileRestoreMounts`, `BrowseMount`, `FileRestorePipeline`;
  - `IFileRestoreTarget` (a new target writes into a PVC).
- **Explorer pattern:** `src/Backdafuckup.Web/Pages/AdExplorer.razor` and `SqlBrowser.razor`. A mount or explore session, a paged API, a preview, then a queued restore.
- **Jobs, sessions, restores:**
  - `BackupJob` and `BackupJobObject` (`NativeId`, no kind yet) in `src/Backdafuckup.Server/Data/Entities.cs`;
  - `BackupSessionRunner`, `RestoreSessionRunner`, `SessionQueue`, `StartupRecovery`.
- **UI:**
  - `Pools.razor`/`AddPoolWizard.razor`, `Inventory.razor`, `JobEditor.razor`;
  - `Backups.razor` + `BackupVms.cs`, `RestoreWizard.razor`;
  - `Components/WizardSteps.razor`, `AppIcons.cs`.

## External sources (public)

### Velero (Apache-2.0; behaviour reference, no code taken over)
- How it works: https://velero.io/docs/main/how-velero-works/
- Restore order, metadata, existing resources: https://velero.io/docs/main/restore-reference/
- Backup archive format: https://velero.io/docs/main/output-file-format/
- CSI snapshot data movement (incl. the `velero-block` mover of 1.19): https://velero.io/docs/main/csi-snapshot-data-movement/ and the design https://raw.githubusercontent.com/velero-io/velero/main/design/Implemented/volume-snapshot-data-movement/volume-snapshot-data-movement.md
- File system backup: https://velero.io/docs/main/file-system-backup/
- Backup hooks / restore hooks: https://velero.io/docs/main/backup-hooks/ , https://velero.io/docs/main/restore-hooks/
- Resource filtering and restore resource modifiers: https://velero.io/docs/main/resource-filtering/ , https://velero.io/docs/main/restore-resource-modifiers/
- Migration / DR: https://velero.io/docs/main/migration-case/

### Kanister (Apache-2.0) and Kasten (concepts from public docs only)
- Kanister architecture and functions: https://docs.kanister.io/architecture.html , https://docs.kanister.io/functions.html
- Kasten storage integration (CSI vs vSphere modes, block-mode export): https://docs.kasten.io/latest/install/storage
- Kasten transforms, DR: https://docs.kasten.io/latest/api/transformsets.html , https://docs.kasten.io/latest/operating/dr

### Kubernetes CSI
- Volume snapshots: https://kubernetes.io/docs/concepts/storage/volume-snapshots/
- Prevent volume mode conversion (`allow-volume-mode-change`, GA 1.30): https://kubernetes-csi.github.io/docs/prevent-volume-mode-conversion.html
- Volume populators and data sources: https://kubernetes.io/docs/concepts/storage/volume-populators-and-data-sources/
- VolumeGroupSnapshot (GA in 1.36): https://kubernetes.io/blog/2026/05/08/kubernetes-v1-36-volume-group-snapshot-ga/
- Changed block tracking, KEP-3314 / SnapshotMetadata (beta): https://kubernetes-csi.github.io/docs/changed-block-tracking.html , https://kubernetes-csi.github.io/docs/external-snapshot-metadata.html , https://github.com/kubernetes/enhancements/issues/3314

### vSphere CSI driver
- Snapshot and restore (Broadcom docs): https://techdocs.broadcom.com/us/en/vmware-cis/vsphere/container-storage-plugin/3-0/getting-started-with-vmware-vsphere-container-storage-plug-in-3-0/using-vsphere-container-storage-plug-in/volume-snapshot-and-restore.html
- Compatibility matrix: https://techdocs.broadcom.com/us/en/vmware-cis/vsphere/container-storage-plugin/3-0/getting-started-with-vmware-vsphere-container-storage-plug-in-3-0/vsphere-container-storage-plug-in-concepts/compatibility-matrix-for-vsphere-container-storage-plug-in.html
- **Driver code at v3.3.1:**
  - snapshot validation and the same-datastore restore: `pkg/csi/service/vanilla/controller.go`, `controller_helper.go`, `pkg/csi/service/common/vsphereutil.go`;
  - limits: `pkg/common/config/config.go`.

  https://github.com/kubernetes-sigs/vsphere-csi-driver/tree/v3.3.1
- No VolumeGroupSnapshot or CBT for vanilla clusters: https://github.com/kubernetes-sigs/vsphere-csi-driver/blob/master/pkg/csi/service/server.go , https://github.com/kubernetes-sigs/vsphere-csi-driver/blob/v3.8.0/pkg/csi/service/wcp/controller_cbt.go
- **Snapshot KBs:**
  - 318825 (best practices, 2–3 snapshots);
  - 441624 (FCD snapshot deletion stuck);
  - 377620 (stale snapshot counts).

  https://knowledge.broadcom.com/external/article/318825
- Snapshot performance: https://www.vmware.com/docs/vsphere-vm-snapshots-perf
- RKE2 bundled chart versions: https://github.com/rancher/rke2/blob/release-1.32/charts/chart_versions.yaml ; Rancher chart: https://github.com/rancher/vsphere-charts/tree/main/charts/rancher-vsphere-csi
