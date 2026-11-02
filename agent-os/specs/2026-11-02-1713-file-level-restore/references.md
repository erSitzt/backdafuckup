# References for File-Level Restore

## Parser library

### LTRData/DiscUtils (used)
- **Location:**
  - https://github.com/LTRData/DiscUtils
  - NuGet `LTRData.DiscUtils.*` 1.0.90 (2026-10)
- **License:** MIT.
- **Relevance:** managed readers for MBR/GPT/LDM, NTFS, ext2-4, XFS, LVM2 and registry hives.
- **Known gaps** (checked in the source, 2026-11):
  - **XFS:**
    - `nrext64` is not implemented: the extent count is read from the old field, so files read empty.
    - `bigtime` is not decoded.
    - Unwritten extents are ignored.
    - Unknown features are not rejected.
  - **ext4:**
    - Unwritten extents have the wrong length (DiscUtils issue #276).
    - Incompatible features such as inline_data and meta_bg are refused.
  - **LVM:** linear segments only.
  - **NTFS:**
    - Only the WOF reparse tag is handled; dedup stubs (0x80000013) read as sparse zeros.
    - EFS data is returned as ciphertext.
    - `$LogFile` is not replayed.
  - **Since 2026-09** every provider must be registered explicitly (`DiscUtils.Xfs.Formats.Register()`), which lets us replace single providers.

### Original DiscUtils
- **Location:** https://github.com/DiscUtils/DiscUtils (last release 0.16.13, 2021).
- **Status:** unmaintained. Its issue list is still useful: #201 for directories with more than 32k entries, #72 for LZNT1, #81 for volumes over 8 TB.

### dissect (Fox-IT)
- **Location:**
  - https://github.com/fox-it/dissect.xfs
  - https://github.com/fox-it/dissect.volume
- **License:** AGPL-3.0, the same as ours.
- **Relevance:**
  - A Python reference for XFS nrext64 and bigtime, and for LVM striped, thin, snapshot and RAID segments.
  - A possible source to port from for later LVM work.

### Linux kernel and xfsprogs documentation
- **XFS on-disk format:** https://www.kernel.org/doc/html/latest/filesystems/xfs/xfs-online-fsck-design.html and the xfsprogs `libxfs/xfs_format.h` (`XFS_DIFLAG2_NREXT64`, `XFS_SB_FEAT_INCOMPAT_NREXT64`, `XFS_SB_FEAT_INCOMPAT_BIGTIME`).
- **ext4 on-disk format:** https://www.kernel.org/doc/html/latest/filesystems/ext4/ (unwritten extents use the length bit `ee_len > 32768`).

### Windows
- **`SYSTEM\MountedDevices`** values map drive letters to partitions:
  - MBR: 12 bytes, the disk signature plus the partition offset;
  - GPT: `DMIO:ID:` plus the partition GUID.

## ZIP

- **.NET 10 `ZipArchive`:**
  - **Streaming:** Create mode on a non-seekable stream writes data descriptors and ZIP64 without buffering.
  - **Synchronous writes:** entries are written synchronously on close (dotnet/runtime #121624, fixed only in .NET 11).
  - **Our workaround:** the ZIP is built on a worker thread into a pipe.

## SSH

### SSH.NET
- **Location:** https://github.com/sshnet/SSH.NET (NuGet `SSH.NET` 2026.0.0).
- **License:** MIT.
- **Relevance:** SFTP and exec channels, with a PTY for `requiretty`.
- **Version:** use 2026.0.0 or later, because 2025.x is no longer supported.

## Similar implementations (behaviour)

### Veeam file-level restore (behaviour only; no Veeam code or binaries)
- **Browser:** a tree on the left and a file list on the right.
- **Restore modes:**
  - *Overwrite*;
  - *Keep*: writes the file as `RESTORED-<name>` next to the original.
- **Copy to:** a folder or a share.
- **Permissions:** an option to restore them.
- **Session:** the session stays open while the browser is open.

## Existing code in this repository

- `src/Backdafuckup.DataMover/RestorePointDiskImage.cs`: random access to a restore point disk.
- `src/Backdafuckup.Storage/LocalDirectoryRepository.Io.cs`: chunk reading and `ChunkCodec`.
- `src/Backdafuckup.Server/Engine/EngineInfrastructure.cs`: `RepositoryManager`.
- `src/Backdafuckup.Server/Engine/EngineServices.cs`: `SessionRunnerService` and `StartupRecovery`.
- `src/Backdafuckup.Agent.Core/AgentRunner.cs`: the long poll and the self-update download pattern.
- `src/Backdafuckup.Smb/RemoteWindows.cs`: the SMB client on top of SMBLibrary.
- `tests/Backdafuckup.DataMover.Tests/DataMoverRoundTripTests.cs` and `tests/Backdafuckup.Testing/InMemoryDisk.cs`: the backup round-trip helpers.
