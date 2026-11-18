# References for Backup from Storage Snapshots

## Similar implementations in this repository

### XCP-ng backup session
- **Location:** `src/Backdafuckup.Xen/XenBackupSession.cs` (snapshot gate, freeze and thaw, `list_changed_blocks`, `CommitAsync` with `VDI.data_destroy`), `XenSnapshotGate`.
- **Relevance:** the XCP-ng path inserts the array snapshot between `VM.snapshot` and the release of the snapshot. CBT stays as it is.

### Backup engine
- **Location:** `src/Backdafuckup.Server/Engine/BackupSessionRunner.cs` (`VmBackupTask.RunCoreAsync`, `BackupDisksAsync`, the `MaxParallelVms` semaphore), `src/Backdafuckup.Server/Engine/EngineServices.cs` (`StartupRecovery.CleanupArtifactsAsync`).
- **Relevance:** where the hook goes, the two phase semaphores, and the crash cleanup order.

### Unused extension points
- **Location:** `src/Backdafuckup.Core/DataMover/Abstractions.cs` (`IDiskTransport`, `ISnapshotLifecycleHook`).
- **Relevance:** they were made for this feature in the foundation spec and are reshaped here.

### VHD and LVM code
- **Location:**
  - `src/Backdafuckup.Xen/VhdStreamReader.cs` and `VhdImageWriter.cs`, written from the Microsoft VHD specification;
  - `src/Backdafuckup.FileLevel` (LTRData.DiscUtils.Lvm as a test oracle).
- **Relevance:** the VHD chain reader shares their footer and header parsing; the LVM parser is new.

### Proxy agent and NBD tunnels
- **Location:** spec 1, `../2026-11-18-1713-vsphere-provider/`.
- **Relevance:** SAN reads use the same agent, device handling and NBD-over-WSS data path.

## Earlier analyses and specs

- `reports/XCP-ng snapshot speed and SAN backup.md`, "Verdict on question 1".
- `research_notes/XCP-ng snapshot speed and SAN backup/san_read_from_array_snapshots.md`. Never run `vgimportclone` on snapshot devices; parse LVM in user space.
- `agent-os/specs/2026-11-04-2026-consistent-snapshots-slow-storage/plan.md`, "Approach E". It is superseded: this spec keeps the XAPI snapshot for CBT.
- `agent-os/specs/2026-11-01-2134-xcpng-backup-foundation/`. Storage snapshots were left out of scope there; only the extension points were defined.

## External references

- **HPE Alletra Storage MP B10000 WSAPI v3:**
  - OpenAPI 3.1 document from developer.hpe.com (`ws-open-api.json`, v2.5.0, stored as `research/HPE-Alletra/ws-open-api-2.5.0.json`, the only file used from `research/`);
  - each array serves its own version under `/docs`.
- **Microsoft Virtual Hard Disk Image Format Specification** (VHD, dynamic and differencing disks).
- **libvslvm documentation of the LVM2 format** (format description only).
- **XCP-ng / XenServer `sm`** (LGPL): read for the LVHD layout (LV names, journal LVs, `vhd-parent`). No code copied.
- **vmfs6-tools 0.2.1:** Debian's copyright file (GPL-2+) and `libvmfs/vmfs_volume.c` ("version 2 … or any later version"). Ported only after a per-file licence audit. Older VMFS3/5 reader: glandium/vmfs-tools (GPL-2.0, not ported).
- **VMware vSphere Web Services API reference:**
  - `HostStorageSystem` (`RescanHba`, `RescanVmfs`, `UnmountVmfsVolume`, `DetachScsiLun`, `DeleteScsiLunState`, `ResolveMultipleUnresolvedVmfsVolumes`);
  - `HostDatastoreSystem` (`QueryUnresolvedVmfsVolumes`, `ResignatureUnresolvedVmfsVolume_Task`);
  - `RenameDatastore`, `SearchDatastore_Task`.
- **open-iscsi** (`iscsiadm`) and the Linux sysfs SCSI interfaces on the proxy.
- **Veeam Help Center**, "Backup from Storage Snapshots" (public pages): workflow ideas only.
