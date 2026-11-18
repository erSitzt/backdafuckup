# References for Instant VM Recovery (vSphere)

## Similar implementations in this repository

### SQL Server publish (copy-on-write over restore point blocks)
- **Location:**
  - `src/Backdafuckup.Agent.Core/Publishing.cs` (`PublishTarget`, `CowDisk`, `PublishBlockSource`, `OverlayBudget`);
  - `src/Backdafuckup.Server/Publishing/DiskPublishes.cs`;
  - `src/Backdafuckup.Server/Sql/SqlPublishSessionRunner.cs`.
- **Relevance:** the closest existing pattern. It covers the overlay over restore-point blocks, an overlay budget, a long-lived session with a watch loop, and cleanup after restarts.
- **Key patterns:** first-write copy at block granularity, read-ahead, `WatchAsync`/`EndReason`.
- **What differs here:** the map is persisted and the session survives restarts.

### Restore point readers
- **Location:**
  - `src/Backdafuckup.DataMover/RestorePointDiskImage.cs` (`SequentialImageStream`);
  - `src/Backdafuckup.FileLevel/ChunkCache.cs`;
  - `src/Backdafuckup.Server/FileRestore/FileRestoreMounts.cs` (`RestorePointDiskStream`).
- **Relevance:** random and sequential reads over a chunk index with a cache. They are the base image of the NFS files.

### Pins and crash recovery
- **Location:**
  - `src/Backdafuckup.Server/Engine/RestorePointAccess.cs`;
  - `src/Backdafuckup.Server/Engine/EngineServices.cs` (`StartupRecovery`, `CleanupArtifactsAsync`, `HasCapacity`);
  - `IArtifactLedger` in `src/Backdafuckup.Core/Operations/Operations.cs`.
- **Relevance:** pinning restore points, the ledger before external calls, and skipping and resuming long-lived sessions.

### Integration tests with real clients
- **Location:** `tests/Backdafuckup.Nbd.Tests/NbdkitInteropTests.cs`.
- **Relevance:** the Testcontainers pattern for running a reference client (here: the Linux kernel NFS client, cthon04, fio) against our server.

## External references (public)

- **RFC 1813:** NFS Version 3 Protocol Specification (including MOUNT v3).
- **RFC 5531:** RPC: Remote Procedure Call Protocol Specification Version 2. **RFC 4506:** XDR.
- **RFC 1833:** Binding Protocols for ONC RPC Version 2 (portmapper).
- **vSphere Web Services API reference:** `HostDatastoreSystem.CreateNasDatastore`, `RemoveDatastore`, `CreateVM_Task`, `RelocateVM_Task`, `PowerOnVM_Task`, `Destroy_Task`.
- **VMware documentation:** "NFS datastore concepts" (NFS 3 locking with `.lck` files, mount limits, `NFS.MaxVolumes`). Public docs only.
- **Connectathon test suite (cthon04), fsx, fio, libnfs:** test tools only, run in containers.
- **Veeam Help Center** (public): the idea of vPower NFS. No design details taken.
