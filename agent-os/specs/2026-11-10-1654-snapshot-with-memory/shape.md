# Snapshot with Memory — Shaping Notes

## Scope

A VM in a backup job can be backed up with a **snapshot with memory** (XAPI `VM.checkpoint`): the VM is paused while its RAM is written and its disks are captured, so the disks match each other, and the RAM image goes into the restore point. A restore can boot the VM cold from the disks, or **resume** it with its memory where it was at the backup.

**In:**
- **Backup:**
  - the per-VM switch in the job;
  - the checkpoint, falling back to a normal snapshot when the VM is not running;
  - the memory image backed up like a disk, but not as a disk;
  - the checkpoint's own metadata;
  - cleanup of the suspend VDI;
  - consistency and a RAM chip;
  - metrics.
- **Restore:**
  - resume with memory in the same pool, at the original location or as a copy without network connections;
  - a cold boot as before.
- **Lab spikes:**
  - pause, record and cleanup (S1);
  - resume (S2);
  - VSS across the pause (S3);
  - a copy without network (S4);
  - NBD for the suspend VDI (S5).

**Not in:**
- resume in another pool (CPU compatibility);
- memory for halted VMs (nothing to capture);
- file-level restore or publishing from the memory image;
- excluding `[NOBAK]` disks from a checkpoint (XAPI cannot).

## Why

The research report (`reports/XCP-ng snapshot speed and SAN backup.md`) showed:
- `VM.snapshot` captures disks one after another, 7–12 s each on the Alletra LVM storage;
- VSS holds writes for at most 10 s.

Multi-disk VMs therefore end up not even crash-consistent across their disks; since `3204885` such restore points are marked. The user pointed out that no job option offers a snapshot with RAM (2026-11-10).

## Decisions

### Confirmed by the user (2026-11-10)
- **Memory image:** backed up, so a restore can resume the VM.
- **Where it is switched on:** per VM in the job (the job editor's VM list).
- **VSS:** spike first, then decide. Until then the checkpoint replaces VSS for those VMs. **Decided after S3 (2026-11-10):** they stay exclusive; the VSS hold runs out during the pause and SQL Server's writer aborts (`spike.md`).
- **Resume:** in the same pool; copies too, but without network connections.

### Technical
- **Public facts only:** XAPI documentation, and Xen Orchestra's open-source code (AGPL), read only and not taken over:
  - `VM.checkpoint(vm, name)` has no `ignore_vdis`;
  - a halted VM gives `VM_BAD_POWER_STATE`;
  - checkpoints are marked `is_a_template`;
  - XO restores a suspended VM with `VM.create` (`power_state Suspended`, `suspend_VDI`, `last_booted_record`, `last_boot_CPU_flags`);
  - the suspend VDI is read like a disk.
- **The memory image is not a disk:** it lives in `RestorePointManifest.Memory`, not in `Disks`. Restore, file-level restore, publishing and per-disk logic never see it, while GC and verify keep and check its chunks.
- **Two metadata blobs:** `vm-metadata` (the live VM, for cold restores, as before) and `vm-metadata-memory` (the checkpoint, for resume).
- **Consistency:** `"crash"` without a warning reason, and the chip RAM. A cold boot is a power failure of the whole VM at one moment; a resume continues the running state.
- **Spike switch:** `Engine:CheckpointWithVss` (default false) is only for S3.
- **No resume for a domain controller:** XAPI keeps the checkpoint's VM generation id (it is a template), so a resumed DC would not notice the restore (USN rollback). A restore point with memory has no guest details, so the VM's agent (domain role 4 or 5) and its restore points with guest processing tell (`DomainControllerEvidence`). A cold restore imports the live VM's metadata and gets a new generation id, as before.

### Risks
- **Pause length:** RAM write plus every disk, e.g. 16 GB and 4 disks ≈ a minute or more (not measured yet). Users and network peers notice it.
- **Space:** the suspend VDI needs RAM-sized space on the storage during the backup, and the backup grows by the compressed RAM image on every run (no CBT for memory).
- **Resume:** needs compatible CPU flags. A copy that resumes with the original's addresses would conflict, hence copies resume without VIFs.
- **Undocumented corners** are settled in the lab before the restore part is built:
  - whether `import_metadata` restores a Suspended VM from a checkpoint export;
  - whether VIFs can be removed from a suspended VM.

## Context

- **Visuals:** none.
- **References:** see `references.md`.
- **Product alignment:** roadmap Phase 1 "Application awareness" and the report's levers (`VM.checkpoint` for VMs that need cross-disk consistency).
