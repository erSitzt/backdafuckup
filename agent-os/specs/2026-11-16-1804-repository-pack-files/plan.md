# Plan: repository pack files

Part 0 of the tape work. It comes before `../2026-11-16-1804-tape-device-layer` and `../2026-11-16-1804-backup-to-tape`, and before the SMB/NFS/S3 targets and offsite copies (roadmap backlog "Repository pack files").

## Context

**What prompted this.**
- Every chunk is its own file today: `chunks/<hex[0..4]>/<sha256>`, about 500 KB compressed on average, so about 20 M files per 10 TB.
- Everything that touches all chunks goes file by file: GC, verify, copies, and soon tape reads. Reading one file per chunk on NTFS will not keep an LTO-9 drive streaming at 300+ MB/s.

**Found while exploring.**
- **Dedup check:** one `File.Exists` per put, under a stripe lock (`StoreChunkAsync`, `LocalDirectoryRepository.Io.cs:208-276`).
- **Writes and reads:** on Windows every chunk file is flushed on its own (`Io.cs:251`); every read opens and closes a file (`Io.cs:66-81`).
- **GC** marks into a `HashSet<ChunkId>` (about 1 GB at 20 M chunks), then lists every chunk file.
- **Older servers** already refuse a `repo.json` whose `FormatVersion` is not 1 (`LocalDirectoryRepository.cs:124`), before `Recover` or GC run. Raising the version keeps them away from pack data.
- **Error texts** name only the chunk id (`IBackupRepository.cs:164-174`).

**User decisions (2026-11-16):**
- Pack files come before tape.
- Existing repositories switch with a button per repository. They keep working unchanged until then, and new repositories start with pack files.

## Format (repository format 2)

**`repo.json`**
- `formatVersion: 2` and `packs: { targetSize }`.
- `targetSize`: 64 MiB by default, 16–512 MiB allowed, hard maximum 1 GiB.
- Manifests, `BDIX` indexes, chunk ids and `BDCK` records do not change.

**Directories**
- `packs/<hh>/<32hex>.pack`: sealed and immutable. The name is a random 128-bit id; `hh` is its first two hex characters.
- `incoming/`: packs being written. Emptied only when the repository opens, never by the 1 h temp purge.
- `index/L<level>-<seq>.bdpi`: immutable, sorted index segments.
- `chunks/`: loose chunks. Still read and collected, never written in format 2.

**Pack `BDPK` v1**
- **Header** (32 B): magic, version, flags (room for encryption later), repository id.
- **Records:** the 32-byte chunk id, then the unchanged `BDCK` record from `ChunkCodec.Encode`.
  - The id in front lets a salvage scan read a pack whose trailer is damaged.
  - Each record verifies itself.
- **Trailer:** one 44-byte entry per record: id, offset of the `BDCK` header, stored length, raw length.
- **Footer** (64 B): `BDPF`, entry count, trailer offset and length, SHA-256 of the trailer.
- Every pack describes itself, so the index can always be rebuilt from the trailers.

**Index segment `BDPI` v1** (two levels)
- **Head:**
  - header and pack table;
  - fan-out table of 2^k cumulative counts (about 32 entries per bucket);
  - for the base only, a Bloom filter at 10 bits per entry (about 1 % false positives);
  - SHA-256 of the head.
- **Entries:** sorted by id, 48 B each (id, pack ordinal, offset, stored length, raw length). Their SHA-256 sits in the footer and is checked on merge and by the repository check.
- **RAM:** only the base's head stays in memory, about 32 MiB at 20 M chunks.
- **Lookups:** one `RandomAccess.Read` of a single bucket (about 1.5 KB, usually from the page cache). There is no mmap, so this also works on SMB later.
  - A new chunk is decided by the Bloom filter alone.
  - A dedup hit costs one small read, which replaces today's `stat`.
- **L0 deltas:** one per commit, held in memory as sorted arrays. More than 16 of them are merged into one; during GC they are merged into the base once L0 exceeds 1/8 of it.
  - A merge streams its inputs, drops dead packs and duplicate ids, swaps in an immutable snapshot, then deletes the inputs.
- **The pack registry is authoritative.** It is built from `packs/` at open, and entries of missing packs are ignored. Deleting a pack never waits for an index rewrite.

## Write path

- **One open pack per writer.** Each writer (`ChunkWriterBase`, `Io.cs:157`) owns one preallocated pack in `incoming/`. Pipeline workers reserve offsets atomically and write in parallel. Compression stays outside all locks.
- **Put,** under the chunk's existing stripe lock:
  1. Add the id to `_protected`.
  2. Look it up: pending (open packs) → recent → L0 → base, then loose files while `chunks/` still has any. Packs marked *retiring* are skipped.
  3. A hit in another writer's open pack makes that pack a dependency of this writer.
  4. After the record is written, register it as pending.

  Two concurrent puts of the same new chunk store it twice. That is harmless; the next repack drops the copy.
- **Seal,** at the target size, at commit or at dispose:
  1. Write the trailer and footer.
  2. `Flush(flushToDisk: true)`: fsync on Linux, FlushFileBuffers on Windows.
  3. Move the file into `packs/hh/`, then `AtomicFile.SyncDirectory` (`AtomicFile.cs:41`).
  4. Move its entries from pending to recent.
- **Commit** (`Io.cs:326`, `Sql.cs:290`):
  1. Seal the writer's own pack and every dependency. A dependency that cannot be sealed fails the commit, naming that pack's path.
  2. Write the L0 delta with `AtomicFile.WriteAsync`.
  3. `FileSystemSync.SyncFileSystem`, then the manifest.
- **Writers that never commit still seal**, because other writers may depend on their pack.
- **Recovery at open** (`Recover`, `LocalDirectoryRepository.cs:482`):
  - delete `incoming/`;
  - load the segment heads, rebuilding from the trailers on any mismatch;
  - read the trailers of packs no segment covers, and write a delta for them;
  - report packs with a bad footer by path, and never delete them;
  - return an open report that `RepositoryManager` logs.

## Read path

- **`ChunkReader`** (`Io.cs:10-93`):
  - looks the chunk up in the same order, including loose files;
  - reads with `pread` from a cache of up to 256 pack handles, opened with `FileShare.Read | FileShare.Delete` so Windows can delete repacked packs;
  - decodes with `ChunkCodec.Decode`, unchanged.

  If a pack vanishes mid-lookup, it looks up once more. The synchronous `Read` used by file-level restore keeps working.
- **New `IChunkLocator`** in Core, for the tape job, verify and packing:
  - `PlanRead(ids)` returns a `ChunkReadPlan`: distinct non-zero ids grouped by pack and sorted by offset, loose chunks last, with the stored and raw length per chunk and totals for capacity planning.
  - `ReadStoredAsync(plan, maxIo = 16 MiB, packsInFlight = 2)` merges neighbouring chunks into large reads and yields the stored `BDCK` records without recompressing them.

## GC and repack (`CollectGarbageAsync`)

- **Mark** as today, but into an open-addressing table of 64-bit id prefixes: about 320 MB at 20 M chunks instead of about 1 GB. A collision can only keep a dead chunk, never lose a live one.
- **Loose sweep** stays as it is while `chunks/` has files.
- **Candidate packs** are older than the 10-minute safety window and at least one of:
  - completely dead (deleted);
  - at least 20 % dead (`RepackUnusedPercent`);
  - smaller than ¼ of the target size (5-minute SQL log backups make tiny packs).

  Selection stops at `RepackMaxBytesPerRun`.
- **Protocol:**
  1. Mark the selected packs *retiring*. Dedup lookups skip them; reads still work.
  2. For each record, under its stripe lock: keep it if it is marked, protected or late-marked and no other live pack has a copy. Kept records go into GC's own pack.
  3. Seal that pack, write a delta, delete the old packs, then merge the index.
- **Why this is safe:**
  - A writer that deduplicated before the retiring flag put the id in `_protected` under the same lock, so the record is copied.
  - A writer that looks up after the flag misses and stores a new copy.
  - Late marks cover writers that commit during GC.
  - Deleting a restore point never touches chunks.
- **`GcResult`** gains packs deleted and repacked, bytes repacked, and unused bytes left.

## Verify

- **Quick:** every id has an index entry, its pack exists, and offset + length fit. Then the 16-byte headers are read in pack order (`ChunkCodec.ReadHeader`) and the lengths compared.
- **Full:** `ReadStoredAsync` plus Decode, in pack order.
- **Repository check:** footers, trailers against the index, every record.
- **Error texts:** the chunk exceptions get an optional location, for example "Chunk … in pack '<root>/packs/3f/….pack' at offset … is corrupt: … (repository '<root>')".

## Migration

- **After the update:** format 1 repositories run unchanged on the loose write path. New repositories are created in format 2.
- **"Convert to pack files"** (console button and `POST /api/repositories/{id}/packs`) queues a maintenance session:
  1. Create the directories, rewrite `repo.json` as format 2 (`AtomicFile.WriteAsync` with overwrite, then `SyncDirectory`) and switch the write mode. Active writers continue; their loose chunks stay readable.
  2. Run GC.
  3. Pack loose chunks:
     - walk the committed manifests newest first (disk indexes, trees, SQL indexes), so a restore point's chunks sit together, which helps tape reads;
     - delete loose files only after their pack is sealed and its delta written;
     - unreferenced loose chunks are left to GC.
- **Pausing:** the session stops after `PackLooseMaxDuration` (default 4 h). Whatever is left in `chunks/` is the remaining work, so it resumes by design. A crash leaves a harmless duplicate.
- **Later maintenance sessions** keep packing until `chunks/` is empty; then the loose lookups switch off.
- **Import** (`InfrastructureEndpoints.cs:347-386`): a format 1 directory stays format 1. A format 2 directory opens, and its index is rebuilt from the trailers if it is missing or damaged. Older servers answer "Repository format version 2 is not supported" (a 400 from the endpoint).
- **No downgrade tool.** A converted repository needs a server that knows format 2.

## Tasks

1. **Spec docs** (this folder) and the roadmap.
2. **Pack format.** `Storage/PackFormat.cs`, `PackWriter.cs`, `PackReader.cs`; `RepositoryDescriptor.Packs` (`Manifest.cs:369`).
   - Tests: round trip; rebuild from the trailer; salvage scan; truncated or garbled footer.
3. **Index.** `Storage/PackIndex.cs` (segments, fan-out, Bloom, pending/recent, merge, snapshot swap), `PackRegistry.cs`.
   - Tests: 2 M random ids against a dictionary; duplicates; dead packs; a crash during merge; a RAM bound.
4. **Write path and recovery.** `LocalDirectoryRepository.Io.cs` (put, dependencies, commits) and `.cs` (`CreateAsync` in format 2, `OpenAsync` for 1 and 2, `Recover`).
   - Tests: crash states built on disk, then reopened (a file in `incoming/`, a pack without a delta, a delta without a pack); dedup across concurrent writers; a commit that depends on another writer's open pack.
5. **Read path and locator.** `ChunkReader`; `IChunkLocator` and `ChunkReadPlan` in Core.
   - Tests: a restore while its pack is repacked (Windows sharing); plan order and totals; loose and packed chunks mixed.
6. **GC and repack.**
   - Tests: port `Gc_running_concurrently_with_backups_never_deletes_referenced_chunks` (`LocalDirectoryRepositoryTests.cs:170`) to small packs; a delete racing a deduplicating commit; consolidating tiny packs; the 10-minute window with `FakeTimeProvider`.
7. **Verify and error texts.**
   - Tests: port `Detects_corrupt_and_missing_chunks` to a flipped pack byte and a deleted pack; messages contain the root and the pack path.
8. **Migration and server.**
   - `UpgradeToPacksAsync`, `PackLooseChunksAsync`.
   - Maintenance session phases and log lines naming paths (`RestoreSessionRunner.cs:526`, `SessionQueue.EnqueueMaintenanceAsync`).
   - `EngineOptions` repack settings; the open report in `RepositoryManager.OpenAsync`.
   - Repositories API and `Repositories.razor`: format, loose chunks left, *Convert to pack files*.
   - Tests: a format 1 repository with restore points, SQL backups and trees → convert → verify everything; interrupt and resume; importing format 1 and format 2 directories.
9. **Metrics and docs.**
   - Gauges from open repositories (next to `StateMetrics.cs:64-68`): `bdfu_repository_packs`, `bdfu_repository_loose_chunks`, `bdfu_repository_unused_bytes`, `bdfu_repository_index_entries`, `bdfu_repository_index_memory_bytes`.
   - Counters: `bdfu_repository_repacked_bytes_total`, `bdfu_repository_packed_loose_chunks_total`.
   - Admin guide: a section "Repository format and pack files" and metrics rows.
   - A public stored-chunk count replaces the `CountChunkFiles` test helpers.

## Verification

- **Each task:** the tests listed, the full suite (`dotnet test --solution backdafuckup.slnx`), CI green on Ubuntu and Windows.
- **Lab benchmark:**
  - a 2 TB backup and its restore, packed against loose;
  - `ReadStoredAsync` at 400 MB/s or more from the repository disk;
  - index RAM with 20 M synthetic entries.
- **Production-like server:** first convert a copy of one repository, then the real ones outside backup windows.
