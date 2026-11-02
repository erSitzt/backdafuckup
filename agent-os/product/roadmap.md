# Product Roadmap

## Phase 1: MVP

- **Web UI:** easy-to-use web console on par with Veeam, so admins can switch easily. It covers the dashboard, backup job setup, browsing restore points, guided restore wizards, and job status/history
- **VM backup (XCP-ng / XenServer):** VM snapshot via XAPI, disks read over NBD into our own chunk repository (compressed, deduplicated). Full backups plus **CBT-based incrementals** (forever-incremental: every restore point is complete on its own). Built on a hypervisor-agnostic core so other platforms can be added later
- **Backup from storage snapshots (XCP-ng + HPE Alletra Storage MP B10000):** keeps the live cluster almost unstressed during backup:
  1. A short VM snapshot, held only seconds, gives application consistency
  2. An array snapshot is taken, and the VM snapshot is removed immediately
  3. The backup proxy reads from the array snapshot over the SAN
  4. Changed blocks come from XAPI CBT

  Arrays connect through a storage plugin interface.
- **Backup targets:** a directory on a local volume first (e.g. an iSCSI LUN from the HPE MSA mounted on the backup server); then SMB and NFS shares and S3-compatible object storage
- **Scheduling & retention:** scheduled backup jobs with retention and rotation policies
- **Restore:** restore a VM to its original or an alternate host / SR
- **Status & alerting:** job logs, success/failure reporting and notifications
- **Application awareness:** application-consistent backups of Windows guests (VSS). Needs our own in-guest agent (VSS requester), because XAPI's quiesced snapshots were removed in XenServer / XCP-ng 8.1
- **Active Directory objects:** application-aware AD backup with object-level restore
- **MS SQL Server:** application-aware backup and restore of SQL Server databases
- **File-level restore:** restore single files and folders from guest disks without restoring the whole VM. It moved here from phase 2 as a knock-out criterion (2026-11-02).
  - Browser download, back into the VM (Windows through the guest agent, Linux over SSH/SFTP with sudo), or to an SMB share.
  - File systems: NTFS, ext2/3/4, XFS, LVM (linear).

## Phase 2: Post-Launch

- **Tape out:** move or copy backups to tape on an HPE MSL tape library
- **Offsite / immutable copy:** backup copies to a secondary site, S3 or immutable storage
- **SQL transaction log backup & point-in-time recovery:** SQL Server log backups and point-in-time restore, run directly from the backup app
- **VMware vSphere support:** back up and restore vSphere VMs using only public vSphere APIs, with no VDDK and no Broadcom partner program
  - Snapshots and CBT through the vim25 Web Services API
  - Disk data read and written through a HotAdd proxy VM (`ReconfigVM_Task`), with full, incremental and block-level restore
  - ExportSnapshot / ImportVApp as a full-backup / full-restore fallback
  - Requires a paid vSphere licence on the customer side, since free ESXi blocks backup APIs
- **Hyper-V support:** back up and restore Hyper-V VMs
- **Storage snapshots for vSphere and Hyper-V:**
  - vSphere: the snapshot LUN is mounted on an ESXi host as a resignatured datastore and read through the HotAdd proxy
  - Hyper-V: the array's VSS hardware provider plus transportable shadow copies
- **More storage array plugins:** vendors to be decided; each must have a publicly documented snapshot API

## Backlog (technical)

- **Repository pack files (before the SMB/NFS/S3 targets and tape out):**
  - **Problem:** today every chunk is its own file, about 500 KB compressed on average. A 10 TB repository holds about 20 million files.
    - Inodes are not the problem.
    - Operations over all files are slow: GC, verify, copying a repository offsite or to tape, SMB/NFS targets, S3 request costs.
  - **Plan:**
    - Bundle chunks into pack files (e.g. 64 MiB) with an index, as restic, Kopia and Borg do. Deduplication and forever-incremental stay.
    - The GC repacks (compaction).
    - Quick interim option: 4 MiB chunks (4× fewer files; coarser dedup and CBT).
  - **Comparison:** Proxmox Backup Server uses one file per chunk as we do. Veeam writes large container files per job/VM (`.vbk`/`.vib`); on S3 it writes one object per block.
