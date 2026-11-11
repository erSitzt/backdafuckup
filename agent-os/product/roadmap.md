# Product Roadmap

## Phase 1: MVP

- **Web UI:** easy-to-use web console on par with Veeam, so admins can switch easily. It covers the dashboard, backup job setup, browsing restore points, guided restore wizards, and job status/history. Backups & Restore stays usable with hundreds of VMs: one row per VM grouped by job, filters in the address, VMs that missed a scheduled run or have no restore point marked
- **VM backup (XCP-ng / XenServer):** VM snapshot via XAPI, disks read over NBD into our own chunk repository (compressed, deduplicated). Full backups plus **CBT-based incrementals** (forever-incremental: every restore point is complete on its own). Built on a hypervisor-agnostic core so other platforms can be added later
  - Snapshot with memory, per VM in the job (spec `2026-11-10-1654-snapshot-with-memory`): `VM.checkpoint` pauses the VM, writes its RAM to a memory image and captures all its disks at one moment, so they match without VSS. The memory image goes into the restore point; a restore boots the VM cold, or resumes it where it was (only in the pool it was backed up in, a copy without network connections, never a domain controller). The pause is the price: about 2 minutes for 8 GiB RAM on shared storage in the lab, and VSS cannot hold writes that long, so such VMs get no guest processing
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
  - Prometheus metrics at `/metrics`, read with a metrics-only API token (spec `2026-11-10-1151-prometheus-metrics`). They cover:
    - VM snapshots (duration, wait, disk-to-disk gap, guest hold);
    - disks by transport, fallbacks and NBD connects;
    - sessions, and per VM the last backup, stale and no restore point;
    - pool storage (space, disks by kind, leftover snapshots);
    - repositories, agents, SQL log backups.
    
    Alert rules and a Grafana dashboard are in `deploy/monitoring/`. Mail and webhook notifications are still to come.
- **Application awareness:** application-consistent backups of Windows guests (VSS). Needs our own in-guest agent (VSS requester), because XAPI's quiesced snapshots were removed in XenServer / XCP-ng 8.1
  - On storage where the XAPI snapshot takes longer than VSS's 10 s (shared LVM, several disks): T-SQL snapshot backup for SQL Server 2022+, then an in-guest shadow copy that is reverted when the backup is read (spec `2026-11-04-2026-consistent-snapshots-slow-storage`)
- **Active Directory objects:** application-aware AD backup with object-level restore (spec `2026-11-06-2308-ad-object-restore`)
  - Backup: domain controllers are recognised and get a VSS full backup (AD records it); the directory's facts and consistency go into the restore point; a VM restore of a DC gets a new VM generation id (non-authoritative restore by Windows)
  - Explore: the restore point's `ntds.dit` is copied to a live DC of the domain and served there by Windows' AD Database Mounting Tool (dsamain); browse, search and compare every object with the live directory (unchanged, changed, deleted, recycled, gone); the copy is always deleted (close, idle time, restarts)
  - Restore over LDAP from a preview with typed confirmation: deleted objects reanimated with and without the Recycle Bin (tombstone attributes and memberships written back), gone ones re-created with a new SID, changed attributes and moves put back, new passwords set or accounts left disabled; nothing live deleted
  - Group policies: files in SYSVOL swapped in with their permissions, container, DACL, versions raised for clients and DFS-R, links added back
- **MS SQL Server:** application-aware backup and restore of SQL Server databases (spec `2026-11-03-0026-sql-server-restore`)
  - Backup: VSS, or the T-SQL snapshot backup for SQL Server 2022+ as a full (default) or copy-only backup, with the `.bkm` and LSNs in the restore point
  - SQL browser with export of the files (ZIP) and as `.bak`, publish without copying (loopback iSCSI with a write overlay), restore as a new database (same or another VM or instance), overwrite of the original with rollback
  - Recovery from agent crashes, VM reboots and server restarts
  - Log backups and point-in-time restore (spec `2026-11-05-1932-sql-log-backup-pitr`, moved up from phase 2 on 2026-11-05): log backups every 5/15/30/60 minutes or longer, run by the server through the agent and stored in the repository; native fulls (SQL Server before 2022, VSS mode, or on schedule) and daily system database fulls; a log-chain watch (other tools' backups, SIMPLE switches, forks, RPO) with a full at once on a break; one session per job and day; a SQL Server page per database with a timeline and a slider to the second; restore as a new database, overwrite with a last log backup, and publish at a point in time; `.bak` download of the fulls
- **File-level restore:** restore single files and folders from guest disks without restoring the whole VM. It moved here from phase 2 as a knock-out criterion (2026-11-02).
  - Browser download, back into the VM (Windows through the guest agent, Linux over SSH/SFTP with sudo), or to an SMB share.
  - File systems: NTFS, ext2/3/4, XFS, LVM (linear).
- **Kubernetes workloads** (spec `2026-11-11-1343-kubernetes-workload-backup`, user request 2026-11-11): the RKE2 agent nodes leave the VM backup (VM snapshots of the nodes disturbed their vSphere CSI volumes), so namespaces are backed up at the Kubernetes level:
  - their objects, and the data of PVCs on a CSI driver with snapshots (a short-lived VolumeSnapshot, copied at block level by a mover pod into the repository), with Velero-compatible hooks;
  - restore of a namespace (all or selected objects), a single PVC and files in the same cluster;
  - the server talks to the cluster API directly, or through an in-cluster agent that dials out.

  Built and checked on the test cluster on 2026-11-11 (lab runbook K1–K8): connection, mover image, backup with hooks, restore of a namespace, apps and PVCs (also replacing a PVC in place), the object explorer, and files back into a live PVC, over the direct API path. Open in phase 1: the in-cluster agent, a label selector for jobs, and metrics of their own.

  Later: another cluster and whole-cluster DR, then NFS volumes at file level, changed block tracking (CSI SnapshotMetadata) and group snapshots; then Kubernetes clusters on XCP-ng, whose PVs could be read through XAPI with CBT.

## Phase 2: Post-Launch

- **Users, roles and audit (user request 2026-11-06):** more than one administrator; roles (e.g. administrator, operator who runs jobs and restores, read-only) checked on every endpoint, also for API tokens (a token acts with its user's roles; scopes per token later); an audit log of who did what (sign-ins, changes to jobs, credentials and repositories, restores, publishes, downloads, token use), kept in the catalog and shown in the console. API tokens (2026-11-06) are the first step: they already act as their user and mark each request as a token request.
- **Active Directory, follow-ups:** WMI filters and site links of group policies; security descriptors of objects; AD-integrated DNS zones and the configuration partition; authoritative restore through the product (`ntdsutil` in DSRM on an isolated copy); passwords from the backup (would need the backup's boot key; not planned unless asked); compare and restore across domains
- **Tape out:** move or copy backups to tape on an HPE MSL tape library
- **Offsite / immutable copy:** backup copies to a secondary site, S3 or immutable storage
- **SQL Server, follow-ups:** differential backups; automatic restore tests (restore to a point in time into a test instance and `DBCC CHECKDB` on a schedule); mail/webhook alerts for chain breaks and RPO; availability groups and failover cluster instances; streaming fulls without staging space in the VM (VDI); resumable uploads; repository pack files for the many small log backup objects
- **Publish for file-level restore:** mount a restore point's volumes into a VM over the same loopback iSCSI as the SQL publish
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
- **File-level restore, follow-ups:**
  - search for files across a restore point (recursive)
  - versions of a file: the same path in all restore points of the VM (size, modification time, changed or not by
    comparing the file's chunk ids, so no data is read), with download and restore of a chosen version. Veeam's
    backup browser offers something similar (user request 2026-11-02).
  - another VM as restore target in the console (the API already accepts one)
  - symbolic links, junctions and alternate data streams into Windows VMs
  - striped and thin LVM volumes, ReFS

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
