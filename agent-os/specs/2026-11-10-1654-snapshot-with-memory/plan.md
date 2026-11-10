# Plan: Snapshot with memory (VM checkpoint) and resume on restore

Spec folder: `agent-os/specs/2026-11-10-1654-snapshot-with-memory/`

## Context

**The problem.** XAPI `VM.snapshot` captures a VM's disks one after another, seconds apart (research report `reports/XCP-ng snapshot speed and SAN backup.md`).
- Since `3204885`, multi-disk restore points that were not application-consistent are marked as not consistent across their disks.
- VSS can only cover a snapshot that finishes within 10 s, which is about one disk on the Alletra LVM storage.
- There is no job option today that gives consistent disks for such VMs.

**The lever: `VM.checkpoint`** (public XAPI; Xen Orchestra offers it as `xo-memory-backup`).
- XAPI suspends the VM (its RAM goes into a suspend VDI), snapshots all disks, then resumes the VM.
- The disks therefore match each other.
- With the RAM image, a restore can resume the VM in its exact running state.

**User decisions (2026-11-10):**
- The memory image is backed up, and a restore can resume the VM.
- The option is switched on **per VM in the job**.
- VSS: **spike first, then decide**. Until then the checkpoint replaces VSS for those VMs.
- Resume is allowed in the **same pool, also for copies**; copies resume with their networks disconnected.

**Today's code** (exploration):
- Backup:
  - `XenBackupSession.CreateAsync` always calls `VM.snapshot` (local `SnapshotAsync`).
  - It never reads `suspend_VDI` or `power_state` of the snapshot.
  - It exports metadata of the live VM (`ExportMetadataAsync`, `export_snapshots=false`).
  - `CommitAsync`, `RollbackAsync` and `DestroySnapshotVmAsync` only handle Disk VBDs, so a suspend VDI would be left behind.
- Restore:
  - The restored VM always comes back Halted from `import_metadata`; the only power action is `PowerOn` → `VM.start`.
  - `CreateVdiAsync` hard-codes `type = "user"`.
- Fake pool: no checkpoint, suspend VDI, Suspended state or resume.

**Facts from Xen Orchestra's open source** (read only, nothing taken over; `@xen-orchestra/xapi/vm.mjs`, `@xen-orchestra/backups/_incrementalVm.mjs`):
- `VM.checkpoint(vm, name)` takes no `ignore_vdis`, so `[NOBAK]` disks are captured too.
- A halted VM gives `VM_BAD_POWER_STATE`; XO falls back to `VM.snapshot`.
- Checkpoints are marked `is_a_template`.
- XO restores a suspended VM by creating it with `power_state Suspended`, `suspend_VDI`, `last_booted_record` and `last_boot_CPU_flags`. The suspend VDI is read like a disk (NBD).

## Decisions

### Where it is switched on
- `BackupJobObject.WithMemory` (bool), shown as a switch per VM in the job editor's VM list, with an explanation:
  - pause of RAM write plus all disks;
  - RAM-sized space on the storage and in the backup;
  - no `[NOBAK]` exclusion from the snapshot.
- Contract: `JobObjectDto.WithMemory`.
- Migration `AddSnapshotWithMemory`: the job object column plus the restore point column below.

### Backup
- `VmBackupRequest.WithMemory`.
- `XenBackupSession`:
  - For a running VM, call **`VM.checkpoint`** instead of `VM.snapshot`, with the same gate, ledger task record, timeout and 250 ms polling.
  - On `VM_BAD_POWER_STATE` (the VM is off), take a normal snapshot and log "not running: snapshot without memory".
  - **VSS:** no guest freeze for a VM with memory. The guest's preparation runs only to collect facts if it can do that without a freeze; otherwise the VM is backed up without guest processing and the log says why.
  - The config option `Engine:CheckpointWithVss` (default false) is for the spike only.
- **Free space:** the check also covers the suspend SR (the VM's `suspend_SR`, else the pool default) for the VM's `memory_static_max`, and the gate also takes that SR.
- **Snapshot record:**
  - Read `suspend_VDI`.
  - Tag it (`XenTags.Session`/`RestorePoint`) and record it in the ledger as a new `XenArtifactKinds.SuspendVdi`.
  - Set `is_a_template=false` if the spike shows it matters.
- **`[NOBAK]` disks in a checkpoint:** they are in the snapshot anyway. They stay excluded (not read; created empty on restore), and their snapshot VDIs are destroyed at commit and rollback.
- **Memory image:**
  - Exposed as `IVmBackupSession.Memory` (`SnapshotDisk?`, key `memory`, `Locator["role"]="memory"`), **not** in `Disks`.
  - Restore, FLR, publishing and per-disk logic therefore never treat it as a disk.
  - Read with the same reader path (`OpenReaderAsync`; `_disks` holds its state) as a full read every time, with `FullReadReasons.MemoryImage`.
  - Its chunks are deduplicated and compressed like disks; zero pages cost nothing.
- **Metadata:** additionally export the **checkpoint snapshot's** metadata (`export_metadata` with the snapshot uuid) as blob `vm-metadata-memory`, for resume. The existing `vm-metadata` of the live VM stays for cold restores.
- **Commit:**
  1. `VM.destroy` of the snapshot;
  2. data_destroy of the disks as now;
  3. `VDI.destroy` of the suspend VDI and the `[NOBAK]` snapshot VDIs.
  
  `RollbackAsync`, `DestroySnapshotVmAsync(destroyDisks)` and the orphan sweep also remove the suspend VDI.
- **Consistency:**
  - A restore point with memory is `"crash"` **without** a warning reason: a cold boot is a power failure of the whole VM at one moment.
  - `AcrossDisks` skips it, since the disks match.
  - New flag `MemoryBytes` (manifest `Memory` entry, entity `RestorePointEntity.MemoryBytes`, `RestorePointDto`/`BackupVmPointDto`).
  - The console shows a chip **RAM** instead of "crash".
- **Repository:** `RestorePointManifest.Memory` (`ManifestDisk?`).
  - GC (`CollectGarbageAsync`) and verify (`VerifyAsync`) in `LocalDirectoryRepository` include its index.
  - `ReadDiskIndexAsync` finds key `memory`.
- **Metrics:**
  - `SnapshotFacts.Method` (`snapshot`/`checkpoint`). The label `kind` on `bdfu_snapshot_duration_seconds` makes the checkpoint duration the VM's pause.
  - The memory image's bytes are counted under category `memory` in `bdfu_backup_disk_bytes_total`.

### Restore
- **`RestoreRequestDto.ResumeWithMemory`.** Validation in the runner and the wizard:
  - the point has memory;
  - the target pool is the point's pool;
  - else an error that names the pool.
- **Runner and `XenRestoreSession`:**
  - Create the memory VDI with `type = "suspend"` (`CreateVdiAsync` gets a type) on the SR of the first disk, or the chosen SR, and upload the image like a disk.
  - Import `vm-metadata-memory` with the VDI map including the suspend VDI. The result is a **Suspended** VM.
  - If `import_metadata` does not give a Suspended VM (spike S2), create it like XO with `VM.create` with `power_state Suspended`, `suspend_VDI`, `last_booted_record` and `last_boot_CPU_flags` from the checkpoint record stored in the manifest.
- **Copies (AlternateLocation):**
  - Their VIFs are removed before resume, so the copy never appears with the original's addresses. The log and the wizard say so.
  - If XAPI refuses VIF changes on a suspended VM (spike S4), copies with memory are refused, with a message.
- **Power:**
  - "Power on" becomes `VM.resume` / `VM.resume_on(host)`.
  - Without it, the VM stays Suspended; it can be resumed in XO.
  - A cold restore (default) is unchanged: disks only, Halted, `VM.start` on request.
- **Wizard:** for points with memory, a choice between "Boot from the disks (cold start)" and "Resume with memory (the VM continues where it was at the backup)".
  - Disabled for another pool, with the reason.
  - For a copy, the hint "starts without network connections".
  - The summary shows it.

## Tasks

1. **Spec documentation:** `plan.md`, `shape.md` (decisions, XO facts, risks), `standards.md`, `references.md`.
2. **Backup with memory:** job object option, migration, contracts, job editor, backup path in `XenBackupSession`, manifest `Memory`, entity flag, repository GC/verify, metrics, chip.
   - The fake pool gets `VM.checkpoint` (a Suspended snapshot with a suspend VDI of random data), `suspend_VDI` and `VM_BAD_POWER_STATE` for halted VMs.
   - Tests:
     - a backup with memory stores the image and destroys the suspend VDI;
     - `[NOBAK]` with a checkpoint;
     - a halted VM falls back to a snapshot;
     - GC keeps the image's chunks.
3. **Lab spike, part A** (deploy first; a lab VM with memory in a job; report in `spike.md`):
   - **S1:** checkpoint on bdfu-lab-sql2 (3 disks plus the config drive). Record:
     - the pause (task time and the guest's view);
     - the snapshot record (`power_state`, `is_a_template`, `suspend_VDI` and its size and type);
     - that `VM.destroy` leaves the suspend VDI.
   - **S3:** with `Engine:CheckpointWithVss=true`, does the VSS hold survive the pause?
   - **S5:** the suspend VDI over NBD versus the HTTP export.
4. **Restore with resume:** request flag, validation, the memory VDI (`type suspend`), metadata import or `VM.create` fallback, VIF removal for copies, resume, wizard.
   - The fake pool imports Suspended VMs and has `VM.resume`.
   - Tests:
     - resume to the original location (replace);
     - a copy without VIFs;
     - refusal across pools;
     - a cold restore of a memory point is unchanged.
5. **Lab spike, part B:**
   - **S2:** resume of the restored sql2 copy (uptime and a test file continue).
   - **S4:** a copy without network.
   - Runbook rows.
6. **VSS decision from S3:** either combine VSS and checkpoint, or keep them exclusive. Record it in shape and the admin guide.
7. **Docs:** admin guide (job VM option, cost, consistency, restore with resume, metrics), roadmap, runbook.

## Verification

- **Tests:** `dotnet test --project tests/Backdafuckup.Xen.Tests` and `tests/Backdafuckup.Server.Tests` for the new tests, then the full suite. CI on GitHub must be green.
- **Lab** (backup01, lab VMs only; deploy outside running sessions and quarter hours):
  - S1–S5 as above;
  - pause and size in the metrics (`bdfu_snapshot_duration_seconds{kind="checkpoint"}`);
  - no suspend VDI left in the pool after the backup (`bdfu_storage_vdis`);
  - a restored and resumed copy runs with its pre-backup state and has no VIF;
  - a cold restore boots.
- **Leftovers:** VMs and copies made by the tests stay for the user to delete; nothing in the pool is deleted by hand.
