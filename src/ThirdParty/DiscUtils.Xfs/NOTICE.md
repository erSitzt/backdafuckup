# DiscUtils.Xfs (vendored)

- **Upstream:** https://github.com/LTRData/DiscUtils, `Library/DiscUtils.Xfs`
- **Commit:** `05cdb89f2e1e01c94ad349f3af3c0611d1e0470d` (2026-10-16). The provider is unchanged since 1.0.89; it builds against NuGet `LTRData.DiscUtils.Core` 1.0.90.
- **License:** MIT, see `LICENSE.txt`. Our changes are under the same license.
- The assembly name stays `DiscUtils.Xfs`, because `DiscUtils.Core` grants that name access to its internals.

## Changes by backdafuckup

All changes are marked with `backdafuckup:` comments. They are to be offered upstream.

1. **nrext64:** the extent counters of inodes with `XFS_DIFLAG2_NREXT64` are read from their new places: the 64-bit data counter at 0x18 and the 32-bit attribute counter at 0x4C. Without this, every file and directory on file systems made by xfsprogs ≥ 6.6 (default) read as empty, with no error.
2. **bigtime:** timestamps of inodes with `XFS_DIFLAG2_BIGTIME` are decoded (nanoseconds since 1901-12-13). Legacy seconds are read as signed.
3. **Unwritten extents:** the flag is the single top bit of the extent record; upstream read two bits. Unwritten (preallocated) extents now read as zeros instead of old disk content.
4. **Features:**
   - Unknown incompatible features are refused with their names, instead of reading on.
   - A file system flagged `needsrepair` is refused.
   - Readable: ftype, sparse inodes, meta_uuid, bigtime, nrext64, exchange-range, parent pointers.
   - Refused: metadir, zoned, unknown.
5. **Corruption guards:** an inode whose extent counter exceeds its data fork fails instead of allocating a huge array.
6. **Directories:**
   - Directory blocks larger than file system blocks (`dirblklog > 0`) are read correctly.
   - Colliding sanitized names no longer make the whole directory unreadable (`Directory.RawEntries`).
7. **Raw access API** (`RawAccess.cs`, `RawUnixEntry.cs`) for file-level restore:
   - lookup by name components that never follows symbolic links (lstat semantics);
   - raw entries with mode, uid, gid, times and link count;
   - opening regular files and `readlink`;
   - `VolumeUuid` in `blkid`/fstab format.
8. **`Formats.cs`:** a hand-written equivalent of upstream's build-time generated registration.

## Known limits

- The log is not replayed (crash-consistent images may miss the newest metadata).
- CRCs are not verified.
- Realtime volumes are not supported.
