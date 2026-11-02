# Implementation status

| Task | State | Notes |
|---|---|---|
| 1. Spec documentation | done | |
| 2. Spike + fixtures | done | See `spike.md`. Test images in `tests/fixtures/fs-images` (`make-fs-images.sh`, Docker `--privileged`). |
| 3. Reading layer | done | Sync `IChunkReader.Read`, `ChunkCache` (global, byte-limited, single-flight), `RestorePointDiskStream`, `FileLevelWorkers` (16 MiB stacks). |
| 4. NTFS + partitions + `IGuestFileSystem` | done | `GuestImage` (MBR/GPT/LVM discovery, stable ids, hidden helper partitions, "not readable" reasons), adapters for NTFS/FAT/ext/XFS, `MountedDevices` and fstab mapping, `GuestWalker`. |
| 7. (moved up) ext/XFS providers vendored and patched | done | `src/ThirdParty/DiscUtils.{Ext,Xfs}`; see their `NOTICE.md`. |
| 5. Browse mounts, API, single-file download, UI | done (slice 1) | `FileRestoreMounts` (sessions of kind FileBrowse as an audit trail, pin, leases, idle timeout, stop via Sessions), `/api/file-restore` (mounts, paged entries, single-file download over a worker thread + pipe), `FileBrowser.razor` (volume/folder tree, list, download), a "Restore files" button in Backups. Repository removal is refused while browsers are open. API test `File_level_restore_browses_a_restore_point_and_downloads_files`. |
| 6. ZIP download | open | |
| 8. SMB target | open | |
| 9. Agent target | open | |
| 10. SSH target | open | |
| 11. Docs | open | |
| 12. Lab provisioning (OpenTofu + XO) | open | Needs XO URL, token, templates, SR and network from the user. |

## Tests

`tests/Backdafuckup.FileLevel.Tests` (25 tests):
- Every fixture volume matches its manifest: content hashes, sizes, types, link targets, mode/uid/gid, mtimes, and the complete listing.
- LVM over two disks, fstab mount points, MBR logical partitions, stable volume ids.
- A round trip through a real repository (backup → `RestorePointDiskStream` → files).
- `ChunkCache` behavior: single-flight, eviction, no caching of failures.
- Hostile images: 36 corrupted variants finish within the time limit.

Full suite on 2026-11-02: 146 total, 143 passed, 3 lab tests skipped.

## Findings from the hostile image tests

A corrupted inode can claim a size of several exabytes, and reading it yields zeros forever. Files larger than 16 × their volume (at least 1 TiB) are therefore reported as unsupported ("implausible size, corrupt metadata?").
