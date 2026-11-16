# References for Repository Pack Files

## Code in this repository

### Storage
- **`src/Backdafuckup.Storage/LocalDirectoryRepository.cs`:**
  - layout, `CreateAsync`, `OpenAsync` (format check at open), `Recover`, `PurgeTemp`;
  - GC (`CollectGarbageAsync`: mark, 10-minute safety window, late marks, `_protected`);
  - `VerifyAsync`.
- **`LocalDirectoryRepository.Io.cs`:**
  - `StoreChunkAsync` (stripe locks, `File.Exists` dedup);
  - `ChunkWriterBase`, `ChunkReader`, commit.
- **`LocalDirectoryRepository.Sql.cs`:** SQL backup objects and their indexes.
- **Formats and helpers:**
  - `ChunkCodec.cs`: `BDCK` header, `Encode`, `Decode`, `ReadHeader`;
  - `ChunkIndexFormat.cs`: `BDIX`;
  - `AtomicFile.cs` (`WriteAsync`, `SyncDirectory`), `FileSystemSync.cs`.

### Core
- **`src/Backdafuckup.Core/Repository/IBackupRepository.cs`:**
  - `IChunkReader`, `IRepositoryWriter`, `GcResult`, `VerifyResult`;
  - the chunk exceptions.
- **`Manifest.cs`:** `RepositoryDescriptor`, `CurrentFormatVersion`.
- **`FileTree.cs`:** `FileTreeWalker.Visit`, used by the GC mark.

### Server
- **`src/Backdafuckup.Server/Engine/EngineInfrastructure.cs`:** `RepositoryManager`, `EngineOptions`.
- **`RestoreSessionRunner.cs`:** maintenance sessions.
- **`src/Backdafuckup.Server/Api/InfrastructureEndpoints.cs`:** repository create, import, rescan, gc and delete.
- **`src/Backdafuckup.Web/Pages/Repositories.razor`.**
- **`src/Backdafuckup.Server/Telemetry/StateMetrics.cs`.**

### Tests
- `tests/Backdafuckup.Server.Tests/LocalDirectoryRepositoryTests.cs`: GC racing backups; corrupt and missing chunks.
- `FileTreeTests.cs`, `SqlBackupObjectTests.cs`: the `CountChunkFiles` helpers.

## External references (public designs, behaviour only)
- **restic** (BSD-2-Clause): pack files with a trailing header, index files, `prune` repacking.
- **Kopia** (Apache-2.0): pack blobs, index blobs merged in levels, compaction.
- **Borg** (BSD-3-Clause): segment files and compaction.
- **Proxmox Backup Server** (AGPL-3.0): one file per chunk, like format 1. It is the comparison for why packs pay off on tape and network storage.
- **Bloom filters:** sizing at about 10 bits per entry for about 1 % false positives.

The designs above are read for ideas only. No code is copied.
