# Spike results (2026-11-02)

**Library:** unmodified LTRData.DiscUtils 1.0.90 (NuGet).
**Test images:** `tests/fixtures/fs-images/` (built by `make-fs-images.sh`; tool versions in `versions.txt`).
**Check:** every path in the manifests is compared for presence, size, SHA-256, link target, mode/uid/gid and mtime.

## Fixtures

| Image | Volume | Result |
|---|---|---|
| `ntfs-gpt` | NTFS on GPT | **go.** All 3,467 entries correct, including the ADS `hello.txt:stream1`, a compressed folder, a sparse file and a hidden file. |
| `ntfs-mbr` | NTFS primary + logical (extended) partition | **go.** |
| `ext4-gpt` | ext4, mke2fs 1.47.2 defaults (64bit, metadata_csum, metadata_csum_seed, orphan_file) | **patch needed:** `sparse.bin` (32 MiB, data at 0 and 16 MiB, holes in between) reads with **wrong content**, and symlinks are resolved instead of reported as links. Content of the other 3,464 entries, modes and owners are correct. |
| `xfs-nrext64` | XFS v5, nrext64 + bigtime (xfsprogs 6.13 protofile) | **no-go without a patch:** the root directory reads as **empty, with no error**. This confirms the research finding. |
| `xfs-gpt` | XFS v5, bigtime, nrext64=0 (kernel 5.15) | **patch needed:** content correct (including fragmented and preallocated files), but **every timestamp is wrong** (bigtime not decoded), and symlinks are resolved. |
| `lvm-disk0` + `lvm-disk1` | VG over two PVs: LV `root` (ext4), LV `data` (XFS, spans both PVs) | **go** for LVM. The XFS mtime issue as above. A whole-disk PV shows up as an extra volume without a file system, so discovery must hide PVs. |

The prealloc files (unwritten extents) read correctly in this run, but the junk blocks may not have been reused. Unit tests on the patched extent code must cover this explicitly.

## Real restore point: app01 (read-only)

Restore point `20261102T155011Z`, application-consistent, Windows Server, one 40 GiB GPT disk with 33,184 of 40,960 chunks used.

- **Volumes:** recovery (NTFS), EFI system (FAT32, not detected because no FAT provider was registered), MSR, C: (NTFS 39.4 GiB), and a 256 KiB partition of type `CADDEBF1-…`.
- **Full walk of C:** 395,100 files and 178,323 directories, **0 errors**, 101 s single-threaded with a 256 MiB LRU cache; 24,627 chunk reads.
- **Single files, cold:** `notepad.exe` (254 KB) in 19 ms; `explorer.exe` (4.4 MB) in 37 ms, about 115 MB/s including decompression and hash checks.
- **`SYSTEM\MountedDevices`:** `\DosDevices\C:` = `DMIO:ID:` + GUID `fe832dd5-…`, which is the partition GUID of C:. Drive letter mapping works without the agent. `D:` is a CD-ROM entry (a device path string), so it is ignored.
- **Guest metadata:** the volume GUID path `\\?\Volume{fe832dd5-…}\` equals the GPT partition GUID (basic disk).

**Finding:** DiscUtils NTFS hides hidden and system files by default (`NtfsOptions`). `ProgramData`, `$Recycle.Bin`, `System Volume Information` and `pagefile.sys` were missing from the listing, so the counts above are without them. The adapter must switch this off and filter only NTFS metafiles (`$MFT`, `$Bitmap`, …) itself.

## Decisions from the spike

- **NTFS, partitions, LVM linear, registry:** NuGet as planned.
- **ext and XFS:** vendored and patched as planned. Needed: XFS nrext64 and bigtime, ext4 holes/sparse, `lstat`-style symlink info, plus the unwritten-extent checks.
- **FAT:** add `LTRData.DiscUtils.Fat` for EFI system partitions and `/boot/efi`. It is cheap and read-only.
- **Performance:** browsing single folders is interactive. A recursive search over a whole volume would take minutes, so it is deferred (a later version could scan the MFT directly).
