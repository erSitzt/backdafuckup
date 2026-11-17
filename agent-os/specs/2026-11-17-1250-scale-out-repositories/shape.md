# Scale-out Repositories — Shaping Notes

## Scope

**Extents.** Several directory repositories (extents, on different volumes) form one scale-out repository that jobs target.

**Placement.**
- A backup chain is one job's restore points of one VM, Kubernetes namespace or `@cluster` object, together with that VM's SQL Server backups.
- Each chain is placed on one extent.
- New chains go to the extent with the most room.

**Operations**
- Chains can be moved between extents.
- An extent can be sealed, put into maintenance, evacuated, added and removed.
- Extents are balanced by hand (with a preview) or automatically in a time window.

## Decisions

### By the user (2026-11-17)
- **Placement unit:** job + object, with its SQL Server backups.
- **When a chain's extent is sealed, in maintenance or offline,** its next backup starts a new full on another extent. For maintenance and offline this is a setting; off means the VM is skipped with a warning.
- **Existing repositories are converted:** *Add extent* on a standalone repository makes it extent 1 of a new scale-out repository. Its jobs then target the scale-out; its chains stay where they are.
- **Balancing:**
  - placement of new chains;
  - a manual *Rebalance* that shows its plan first;
  - an automatic rebalance in a window, when an extent is low on space or the extents are too unequal.

### In the design (2026-11-17, checked against the code)
- **Extents stay ordinary directory repositories.** Restore points and SQL Server backups keep the extent's id, so every read path stays as it is.
  - The scale-out repository is a catalog grouping.
  - Jobs target it, and a placement service picks the extent for a chain's new data.
- **Chains cannot span extents.**
  - Dedup is per chunk store.
  - An incremental clones its base's disk index (`DiskBackupPipeline.cs:105-116`), and Kubernetes file trees reuse the previous tree.
- **Moves keep restore point and SQL Server backup ids.**
  - Tape's "already copied" check, session tasks, `sql_backup_sets`, cluster runs, queued sessions and links all key on them.
  - A move copies (only missing chunks; records stored unchanged and hash-checked), switches the catalog row with compare-and-set, then deletes the source copy.
- **Readers use a double-checked open:** pin, then re-read the row.
- **Retention and delete use compare-and-set on the row** (Rule B), so a move and a retention never both win.
- **A per-chain lease** orders the final switch against a running image backup of that chain. SQL Server log backups never take it.
- **A journal (`repository_move_items`)** makes interrupted moves resumable and lets a cleanup service delete copies that are no longer referenced.
- **Defaults chosen in the design:**
  - an evacuating extent keeps its chains' incrementals until each is switched;
  - SQL Server runs write to another extent for one run when theirs is briefly unavailable;
  - one move session at a time;
  - extents on the same volume are refused (a setting allows them for tests and the lab).
- **Not in v1:**
  - detaching an extent with its data;
  - dissolving a scale-out;
  - SMB, NFS and S3 extents (they come with those target kinds).

## Context

- **Visuals:** none.
- **References:** see `references.md`.
- **Product alignment:** roadmap "Backup targets" (directories first, then SMB/NFS/S3). The scale-out layer is independent of the extent kind.
