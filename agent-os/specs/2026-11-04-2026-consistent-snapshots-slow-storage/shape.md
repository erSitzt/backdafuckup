# Application-Consistent Snapshots on Slow Storage — Shaping Notes

## Problem

Windows holds the writes of a VSS snapshot for at most 10 s (`VSS_E_HOLD_WRITES_TIMEOUT`, fixed). Our agent's VSS provider holds them while the server takes the XAPI `VM.snapshot`, so the whole snapshot must fit into those 10 s.

Measured on backup01, 2026-11-02 to 2026-11-04 (session log):

| VM | Disks | Storage | VM snapshot | Result |
|---|---|---|---|---|
| app01 | 1 | – | 7.3–8.8 s | application-consistent (held 7.1–7.8 s) |
| bdfu-lab-sql1/sql2, both at once | 4 each | Alletra (shared LVM) | 38–103 s | crash-consistent, writers `VSS_E_WRITERERROR_TIMEOUT` |
| bdfu-lab-sql1/sql2, both at once, no guest processing | 4 each | Alletra | ~60–80 s | crash-consistent by choice |

XAPI snapshots the disks one after another, and parallel snapshots on one LVM storage wait for each other. In the second guest run sql1 finished 18 s after sql2, which points to about 5–9 s per disk. A SQL Server with C:, data and log disks will not fit into 10 s on this storage, even alone.

## How others do it (public sources only)

- **Xen Orchestra** (AGPL source, `@xen-orchestra/backups`, `@xen-orchestra/xapi/vm.mjs`):
  - quiesced snapshots are gone;
  - per-VM tags choose a plain snapshot, `xo-memory-backup` (`VM.checkpoint`, consistent only when resumed with its RAM) or `xo-offline-backup` (shut down, snapshot, start);
  - `[NOBAK]` in a disk name leaves it out (`VM.snapshot` with `ignore_vdis`);
  - the tag `xo:notify-on-snapshot` calls an HTTP hook in the guest (port 1727, `/sync` before the snapshot, `/post-sync` after).
- **Veeam XCP-ng plug-in** (VBR 13.1): no application-aware processing, crash-consistent only. Veeam points to its agents inside the guests (community review, R&D forum, April 2026 field report).
- **Hyper-V:** the guest creates a VSS shadow copy, then the host snapshots the VHDX. On the host, "auto-recovery" reverts the snapshot volume to the shadow copy. Approach B below follows the same idea.

## Input: the user's report (2026-11-04)

The report is titled "MSSQL-Sicherung auf XCP-ng – Zielarchitektur & Spezifikation für die Eigenentwicklung".

**Adopted:**
- **Two layers for SQL:** native SQL backups with the log chain (point-in-time restore), plus the VM image for the server itself.
- **T-SQL snapshot backup (SQL Server 2022+)** as the freeze for the image. It is documented by Microsoft, has no 10 s limit, and gives a FULL (or COPY_ONLY) backup in `msdb` with a `.bkm` file.
- **Free-space check of the storage** before every snapshot. The report found Alletra at 1–4 % free on 2026-11-03, when Veeam skipped 22 VMs.
- **Our own snapshots carry tags**, so deletions by others can be told apart. We already have this: `backdafuckup.session` and `backdafuckup.restorepoint` in `other_config`.
- **Only one tool owns the log chain:** foreign `BACKUP LOG` entries (e.g. `TO DISK = 'NUL'`) are reported as a broken chain. Our VSS backups are copy-only already.
- **Encrypted SQL connections** and a least-privilege account in the agent's SQL layer.

**Not adopted:**
- Every finding and design idea the report takes from decompiled Veeam components, for example the "freeze writer" in `VeeamVssSupport.dll` and the plug-in's worker code.
  - Rule: no code, design or details from a purchased product's binaries (user rule; Veeam's EULA forbids reverse engineering; clean-room for an AGPL product).
  - What we build here comes from Microsoft Learn, the XAPI and Xen Orchestra sources, and our own measurements.
- **Separate repository service with immutability, mTLS and client-side AES:** this belongs to the backup-target roadmap (offsite/immutable copy), not to snapshot consistency.

**Open question in the report: who deleted Veeam's CBT snapshots on 2026-11-02/03?**
- backdafuckup has run on backup01 since 2026-11-02, so we checked our code.
- It deletes only:
  - snapshots and disks recorded in its own artifact ledger;
  - disks tagged `backdafuckup.session`;
  - the change-tracking disks of its own restore points.
- It never calls `VDI.disable_cbt`.
- Since 2026-11-04, releasing change-tracking data also checks the tag and the disk type, and refuses anything else with a warning.
- The answer lies in the XAPI audit log on the pool master. It shows the session, user and source IP of each `VDI.destroy`.

## Decisions

- **Step 1 is done (2026-11-04)** (see `status.md`):
  - one snapshot at a time per storage;
  - `[NOBAK]` disks left out and restored empty;
  - the storage free-space check;
  - the snapshot time with disk count and free space in every log line.
- **Step 2: T-SQL snapshot backup (approach A) for SQL Server 2022+.**
  - It is the smallest change that makes SQL consistent on any storage.
  - It also gives the base for log backups and point-in-time restore.
- **Step 3: in-guest shadow copy with read-time revert (approach B).** It covers all VSS applications (AD, Exchange, file servers, SQL before 2022).
- **Approach C (memory checkpoint, offline snapshot) and approach E (array snapshot) are options, not next steps.**
- **Native SQL backups with PITR (layer 1 of the report) get their own spec.** It is the SQL restore spec's phase 2, and the roadmap's "SQL transaction log backup & point-in-time recovery".
