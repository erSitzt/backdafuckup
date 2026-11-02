# File-Level Restore — Shaping Notes

## Scope

Restore single files and folders from a restore point without restoring the whole VM. The restore point's disks are read directly from the repository, and partitions and file systems are parsed in managed code; no kernel mount is needed.

**Targets:**
- **Browser download:** a single file, or a ZIP for several files and folders.
- **Back into the VM:**
  - Windows VMs through the guest agent.
  - Linux VMs over SSH/SFTP with stored credentials and sudo.
- **A network share (SMB).**

**File systems:** NTFS on MBR/GPT; ext2/3/4 and XFS, also on LVM (linear volumes).

### Out of scope (later)
- Recursive search over a whole volume.
- LVM striped and thin volumes.
- Btrfs, ReFS, BitLocker, LUKS, Storage Spaces, and data in Windows deduplication.
- NFS targets.
- Restoring alternate data streams (ADS), sparse flags and hard links as links.
- ACLs on network shares.
- SQL and AD item restore: their own specs follow.

## Decisions

### Confirmed by the user (2026-11-02)
- **Priority:** FLR is a knock-out feature and comes right after the guest agent (order: agent → FLR → SQL → AD). It moves from roadmap phase 2 into the MVP.
- **Targets and file systems:**
  - Targets: browser download, back into the VM, and a network share.
  - File systems: NTFS, plus ext4/XFS with LVM.
- **UI:** no visuals; build it like Veeam's file-level restore browser. Volumes and folders as a tree on the left, files on the right, multi-select, and the actions on top.
- **Linux "back into the VM":** over SSH/SFTP with stored credentials, **with sudo support**. A root login works directly.
- **NTFS permissions on a Windows restore:** restored, as an option that is on by default.

### Technical decisions (from research and review)
- **Parser library:** LTRData.DiscUtils (MIT).
  - NuGet for Core, Streams, Ntfs, Lvm and Registry.
  - The ext and XFS providers are vendored as source and patched:
    - XFS nrext64 (otherwise files read silently as empty on current XFS), bigtime, and unwritten extents;
    - ext4 unwritten extents;
    - rejecting unknown XFS features.
  - The patches are offered upstream.
- **Anything unknown fails loudly** with a reason ("not supported: …"), never as silent zeros.
- **No sync-over-async:** DiscUtils is synchronous. The chunk store gets a synchronous read path and a global chunk cache limited in bytes. File system work runs on worker threads, not request threads.
- **ZIP:** `ZipArchive` on a worker thread into a pipe, so Kestrel never sees synchronous writes.
- **Drive letters:** from `SYSTEM\MountedDevices` in the backup itself, so they also work without an agent. Linux mount points come from `/etc/fstab`.
- **Sessions:**
  - `FileBrowse` (one per open browser) is not queued; its log is the audit trail of who opened and downloaded what.
  - `FileRestore` is queued for the VM and share targets.
- **Permissions default:** on for the original location. For another VM or an alternate folder it is off by default, because local SIDs and inherited ACEs do not fit there; it can be switched on.
- **Allowed targets by source:** NTFS → Windows VM, share or download. ext4/XFS → Linux VM, share or download.
- **Agent safety:**
  - Only agents that report the capability `restore-files-v1` get file commands.
  - An unknown command must never abort a running backup (bug in the current agent).
- **SSH safety:**
  - Files are staged under numbered names and the manifest is base64-encoded. One static script runs with sudo.
  - **No file name ever ends up in a shell string.**
  - Host keys are trusted on first use and pinned.

## Context

- **Visuals:** none.
- **References:** see `references.md`.
  - Existing code: `RestorePointDiskImage`, `LocalDirectoryRepository.Io`, `RepositoryManager`, `IBackupRepository.Pin`, `SessionRunnerService`, `AgentRunner`, `AgentRegistry`, `Backdafuckup.Smb`, `CredentialProtector`, `Backups.razor`.
- **Product alignment:** the roadmap had "File-level restore" in phase 2. The user moved it before notifications because it is a knock-out criterion.

## Standards applied

There are no formal standards yet; see `standards.md`.
