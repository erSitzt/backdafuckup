# DiscUtils.Ext (vendored)

- **Upstream:** https://github.com/LTRData/DiscUtils, `Library/DiscUtils.Ext`
- **Commit:** `05cdb89f2e1e01c94ad349f3af3c0611d1e0470d` (2026-10-16). The provider is unchanged since 1.0.89; it builds against NuGet `LTRData.DiscUtils.Core` 1.0.90.
- **License:** MIT, see `LICENSE.txt`. Our changes are under the same license.
- The assembly name stays `DiscUtils.Ext`, because `DiscUtils.Core` grants that name access to its internals.

## Changes by backdafuckup

All changes are marked with `backdafuckup:` comments. They are to be offered upstream.

1. **`ExtentsFileBuffer` rewritten.**
   - Upstream returned the preceding extent for blocks in a hole and computed negative lengths, so sparse files read wrong data.
   - Upstream read unwritten (preallocated) extents as data, because `ee_len > 32768` was not decoded; they now read as zeros.
   - The extent tree depth is bounded (8), and an extent block whose entry count does not fit fails.
   - `GetExtentsInRange` reports the real data ranges.
2. **Directory records:** a record length below 8, beyond the block, or shorter than its name fails with an `IOException`. Before, a zero length looped forever on a corrupt image.
3. **Raw names:** `DirectoryRecord.RawName` keeps the stored name; upstream replaces `\ / * ? :` with `_`. Colliding sanitized names no longer make the whole directory unreadable (`Directory.RawEntries`).
4. **Inode:**
   - A fast symlink is detected by its size (< 60 bytes), as in the kernel, not by `i_blocks == 0`, which fails when the inode has an xattr block.
   - Large inode timestamps (`*_extra`: nanoseconds and epoch bits) are decoded, and 32-bit seconds are read as signed.
5. **Raw access API** (`RawAccess.cs`, `RawUnixEntry.cs`) for file-level restore:
   - lookup by name components that never follows symbolic links (lstat semantics);
   - raw entries with mode, uid, gid, times and link count;
   - opening regular files and `readlink`;
   - `VolumeUuid` in `blkid`/fstab format.
6. **`Formats.cs`:** a hand-written equivalent of upstream's build-time generated registration.

## Known limits

- Files using triple indirect blocks (ext2/3 files larger than about 4 GiB with 4 KiB blocks) are refused (`NotSupportedException`).
- The journal is not replayed.
- Incompatible features that upstream refuses (inline_data, meta_bg, encrypt, casefold, ...) make the volume unreadable with a clear message.
