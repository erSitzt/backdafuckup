# Plan: VMware vSphere provider (spec 1 of 3)

Part of a set of three specs from 2026-11-18:
- **1. vSphere provider (this spec).**
- **2. Instant VM recovery** (`../2026-11-18-1713-vsphere-instant-recovery/`).
- **3. Backup from storage snapshots** (`../2026-11-18-1713-storage-snapshot-backup/`).

This spec builds the provider, the Linux proxy agent and the seams the other two plug into.

## Context

**The goal.** The product backs up XCP-ng and Kubernetes today. vSphere VMs are to be backed up with every publicly documented technique:
- our own vim25 client;
- snapshots plus CBT;
- HotAdd through a proxy VM;
- VMware Tools quiescing;
- guest operations;
- restore and quick rollback.

**What we must not use.** The integration policy rules out VDDK, NFC, TAP and VAIO (`agent-os/product/tech-stack.md`, `reports/VMware vSphere integration access.md`). Veeam's public Help Center may serve only as a source of ideas.

## Decisions

| Topic | Decision |
|---|---|
| vim25 client | Hand-written SOAP over `HttpClient`, not generated from the WSDL (svcutil output is about 100 MB and relies heavily on reflection). The WSDL is only a reference. |
| VM identity | `NativeId` = `config.instanceUuid`: vCenter keeps it unique and `FindByUuid(instanceUuid: true)` finds the VM exactly. The BIOS UUID (`config.uuid`) can be duplicated, which would break the unique index `(ConnectionId, Kind, NativeId)`. |
| Agent matching | New indexed column `InventoryEntry.GuestUuid`: the BIOS UUID for vSphere, `NativeId` for Xen. `AgentService.VmIdCandidates` also parses the VMware serial format `VMware-42 1a …-…`. |
| Platform | `PlatformId` = vCenter `about.instanceUuid`. vCenter only in v1; a standalone ESXi (`apiType` HostAgent) is refused with a message. |
| Data path | The proxy serves the hot-added device as **NBD inside a WebSocket tunnel that it opens to the server**, with a one-time token per stream. The server reads with the existing `NbdConnectionPool`. This reuses `IDiskReader` and therefore reads only dirty chunks, gives several connections with reconnect, and supports writes, flush and `WRITE_ZEROES` for restore and rollback. Spec 3 reuses it for SAN LUNs. |
| Alternatives rejected | Extending `MoverStream` with an extent list: one stream per disk, no reconnect, and the zero-fill pitfall of the sequential path. Random reads over the control socket: that would mean writing our own NBD anyway. |
| Snapshot with memory | Not offered for vSphere in v1. It cannot be quiesced, and `.vmsn`/`.vmem` would have to go over `/folder`. |
| Quick rollback | A safety snapshot is on by default. CBT is reset afterwards, so the next backup reads in full (cheap through dedup). |
| `.vmx` | Not used for restore, because its format is not documented. Restore builds a `ConfigSpec` from documented `ConfigInfo` parts. |

## Architecture

### vim25 client (`src/Backdafuckup.VSphere/Vim`)

**Building blocks**
- **`VimTransport`:**
  - `https://<vcenter>/sdk` with the `vmware_soap_session` cookie;
  - SOAPAction `urn:vim25/<version>`, taken from `/sdk/vimServiceVersions.xml`;
  - keepalive through `CurrentTime`;
  - one new login after `NotAuthenticated`;
  - TLS with SPKI pins (`Core/Tls/CertificatePinning`).
- **`VimSoap`:** builds the envelope and turns faults into `VimFaultException(type, properties)`. Messages name the object, the host and the operation.
- **`VimObject`:** typed reading over `XElement` (like `XapiRecord`).
- **`VimWriter`:** writes request data objects in XSD sequence order with `xsi:type`.
- **`PropertyCollector`:** `RetrievePropertiesEx`/`ContinueRetrievePropertiesEx` with `ContainerView` and paging.
- **`VimTasks`:** polls `TaskInfo`. The task moref goes into the ledger.
- **One logged-in session per connection**, with a reference count, so a job does not cause a burst of logins.

**Operations (about 45)**
- Session and lookup: Login/Logout, `RetrieveServiceContent`, `RetrievePropertiesEx`/`Continue`, `CreateContainerView`, `FindByUuid`/`FindAllByUuid`, `FetchUserPrivilegeOnEntities`.
- VM and snapshot: `CreateSnapshot_Task`, `RemoveSnapshot_Task`, `RevertToSnapshot_Task`, `ConsolidateVMDisks_Task`, `ReconfigVM_Task`, `QueryChangedDiskAreas`, `CreateVM_Task`, `PowerOnVM_Task`/`PowerOffVM_Task`, `ShutdownGuest`, `Rename_Task`, `Destroy_Task`, `CancelTask`.
- Files and placement: `MakeDirectory`, `DeleteDatastoreFile_Task`, `DatastoreNamespaceManager.CreateDirectory` (vSAN), `QueryConfigOption`/`QueryConfigTarget`.
- Guest operations: `InitiateFileTransferToGuest`, `StartProgramInGuest`, `ListProcessesInGuest`, `MakeDirectoryInGuest`, `ValidateCredentialsInGuest`.
- Datastore `/folder` GET/PUT, only for `.vmx` (kept for reference) and `.nvram`.

**Guarding against element-order mistakes**
- golden-file tests;
- the fake checks element order;
- an optional CI lane with a vcsim container (Testcontainers) for the operations vcsim implements (it has no `QueryChangedDiskAreas`).

### Provider (`src/Backdafuckup.VSphere`)

**Classes:** `VSphereProvider`, `VSphereHypervisorSession`, `VSphereInventory`, `VSphereBackupSession`, `VSphereRestoreSession`, `VSphereRollback`, `VSphereCbt`.
- `VSphereCbt` pages `QueryChangedDiskAreas` into `DiskExtent`s.
- `HotAdd/` handles attach, detach, sweep and proxy selection.
- `VSphereConfigBlob` (`vsphere-config-v1`) and `VSphereVmSpecBuilder` turn config into a `ConfigSpec`. They stay standalone so spec 2 can reuse them.
- Also `GuestOps`, `VSphereOptions`, `VSphereArtifactKinds` and `VSphereTags`.

**Inventory properties:** moref, `biosUuid`, host, cluster, folder path, CBT state, snapshot count, Tools status and an "is proxy" flag.

**Disks**
- `Key` = `scsi0:1`, `sata0:0` or `nvme0:0`.
- `SourceDiskId` = `uuid` of the root backing in the chain. It is stable across snapshots and Storage vMotion; if it is empty, `{instanceUuid}/{deviceKey}`. The runner already matches base disks by `SourceDiskId` (`BackupSessionRunner.cs` ~999).
- `Locator` carries the device key, snapshot file name, datastore moref, disk mode and provisioning.

**CBT**
- **Token:** `ChangeToken("vsphere-cbt", changeId)`, the changeId of that disk in the snapshot's own config. `ReleaseChangeTokensAsync` does nothing for vSphere.
- **Enabling:** if `changeTrackingEnabled` is false and the hardware version is 7 or later, call `ReconfigVM` with `changeTrackingEnabled=true`. The stun of the backup snapshot should activate it (spike S3). An empty `backing.changeId` means a full read, no token stored, and a log line.
- **Epoch:** before QCDA, compare the epoch (the UUID part of the changeId). If it differs, do a full read with reason `cbt_reenabled`.
- **Reset:** an inventory action and the job option "reset on active full". It disables CBT, runs a stun cycle through a temporary `backdafuckup cbt-reset` snapshot, enables CBT, and stuns again.
- **First full with `"*"`:** returns the allocated areas. A new `ChangedExtents.Allocated` lets `DiskBackupPipeline` read only chunks that touch allocated areas and record the rest as zero. On NFS, `"*"` returns the whole disk, which is harmless.
- **Paging:** loop on `startOffset + length` until the capacity is reached. The fake caps each answer so the loop is exercised.

**Snapshots**
- Name `backdafuckup temporary snapshot`; description `backdafuckup session=… job=… restorepoint=…`.
- Before the snapshot: a gate per datastore (default 4) and a free-space check (`MinDatastoreFreePercent` 5).
- After removal: if `runtime.consolidationNeeded` is set, run `ConsolidateVMDisks_Task`.
- User snapshots are never touched. Ours sits on top of them, with a warning above N snapshots.
- `SnapshotFacts.DisksAtOnce = true`: vSphere stuns once for all disks, so `AcrossDisks` does not warn about disks captured one after another.
- **Ledger kinds:** `VsphereTask`, `VsphereSnapshot`, `VsphereHotAddDisk`, `VsphereRestoredDisk`, `VsphereRestoredVm`, `VsphereRollbackSnapshot`. Cleanup removes them; for a rollback snapshot it first reverts to it, then removes it.
- `FindTaggedArtifactsAsync` scans all snapshot trees for our description and the proxies' devices.

**Disks and VMs that are not supported**
- Excluded with a warning, restored empty: independent disks, physical RDMs, multi-writer disks.
- Virtual RDMs: excluded unless spike S1 shows they work.
- Failed with a clear message: encrypted VMs, vVols, FT VMs, templates, passthrough devices.
- The source controller type (IDE/SATA/NVMe/SCSI) should not matter, because the VMDK is attached to the proxy's SCSI controller (spike S1).

### HotAdd

- **Attach** the snapshot's disk (base or delta) with `ReconfigVM` on the proxy:
  - device add with no `fileOperation`, mode `independent_nonpersistent`;
  - a free unit on a hot-add PVSCSI controller (unit 7 is skipped; LSI has 15 units, PVSCSI 64).
- **Never emit `fileOperation=destroy`** when removing a disk from a proxy. A unit test asserts this for every reconfiguration the proxy code builds.
- **Detach order:** the agent closes the device and deletes the SCSI device; then `ReconfigVM` remove; then `RemoveSnapshot`.
- vSAN and NFS work when the proxy's host mounts them (vSAN untested in the lab).
- vMotion during a backup cannot be blocked, because `DisableMethods` is not public. Document this.

### Linux proxy agent

**Projects**
- `src/Backdafuckup.Proxy` (library):
  - `ProxyAgentProtocol`, `ProxyAgentClient` (agent side), `ProxyAgentConnection` (server side);
  - `BlockDevices/`: sysfs discovery, VPD 0x80/0x83, rescan and delete, `O_DIRECT` open, `BLKROSET`.
- `src/Backdafuckup.ProxyAgent`: executable, self-contained single file for linux-x64. Built by `scripts/build-proxy.sh`, runs from `deploy/linux/backdafuckup-proxy.service` (root, `Restart=always`, `ProtectSystem=strict`).
- **Framing:** the length-prefixed JSON header + binary framing moves out of `src/Backdafuckup.Tape/Agent/TapeAgentProtocol.cs` into a generic `AgentSocketFraming<THeader>` in Core. Tape keeps its message type.

**Control connection.** An outbound WSS to `api/agent/proxies/{id}/control`, authenticated with the per-agent key. Version negotiation works as for the tape agent. The server holds all logic.

**Messages**
- `hello`: protocol, version, `FilesHash`, host name, SMBIOS UUID, BIOS serial, IPs, capabilities `hotadd-v1`/`nbd-v1`, non-system block devices.
- `rescan`, `devices`.
- `wait-device{match: serial|wwn, sizeBytes, timeoutMs}`, answered by `device{path}`.
- `open{handle, path, writable}`.
- `tunnel{handle, streamId, token}`: the agent dials `…/tunnel/{streamId}` and serves NBD on it (pattern: `Core/DataMover/ClusterTunnel.cs`).
- `release{handle}` (flush, close, delete the SCSI device), `update`, `error`. Errors name the proxy host and the device path.

**NBD server.** `src/Backdafuckup.Nbd/NbdServer.cs`, written from the public NBD protocol spec: options GO/INFO; commands READ/WRITE/FLUSH/WRITE_ZEROES/DISC. Also add `NbdClient.ConnectAsync(Stream, …)`; `NegotiateAsync(Stream)` already exists. The test helper `InProcessNbdServer` becomes a thin wrapper around it.

**Server side**
- `ProxyDiskReader : IDiskReader`, transport name `hotadd`. Connections per disk = `job.NbdConnectionsPerDisk`.
- `ProxyDiskWriter : IDiskWriter, IExtentDiskWriter` with `RandomAccess|SkipsZeroRegions`, the first user of `RandomAccess`.
- `src/Backdafuckup.Server/Proxies/`: `ProxyAgents` (like `TapeAgents.cs`), `ProxyEndpoints` (enroll, control, tunnel, files), `ProxyPool`.

**Device mapping.** The proxy VM must have `disk.EnableUUID=TRUE`, so VPD 0x80 holds the disk UUID. The server reads the attached device's `backing.uuid` back and checks the size too. Without `disk.EnableUUID` it refuses and tells the admin to set it and power-cycle the proxy.

**Enrollment.** "Add proxy" in the console creates a one-time install token (24 h) instead of the global agent secret, because a proxy can read every disk. `install.sh --server … --token … --pin …` posts to `/api/agent/proxies/enroll` and receives the agent id and key, stored in `/etc/backdafuckup-proxy/agent.json` (mode 0600).

**Self-update.** `AgentPackages` gets the build `linux-x64-proxy`, offered only when the proxy has no open handles. The agent downloads with its key, checks the SHA-256, installs to `releases/<hash>`, swaps the `current` symlink and exits with `RestartExitCode`. It keeps the Windows agent's loop guard.

**OS setup**
- A udev rule sets `UDISKS_IGNORE`.
- Documented: LVM devices file (`use_devicesfile=1`) and a multipath blacklist.

**How the server finds the proxy VM.** The hello carries the SMBIOS UUID. The server calls `FindAllByUuid(instanceUuid: false)` on each vSphere connection, in both byte orders. The result (connection, instanceUuid, moref) is stored on `ProxyEntity` and refreshed with the inventory; it can be bound by hand when the lookup is ambiguous.

**Proxy selection per VM**
- **Candidates:** the job's list, or all proxies. They must be connected, have `hotadd-v1`, run as a powered-on VM, and have free units and leases.
- **Filter:** the proxy's current host must mount every datastore of the VM (`mountInfo.accessible`).
- **Ranking:** same host, then same cluster, then fewest leases.
- **If none fits,** the VM fails with one reason per proxy, for example "proxy p1 (VM proxy01 on esx03.lab, cluster A) does not see datastore ds-02".

### Guest processing

- Move `FreezeGuestAsync`/`ThawGuestAsync`/`ReportGuestFreeze` from `XenBackupSession.cs` (~535-606) into Core as `GuestFreezeOrchestration.RunAroundSnapshotAsync`. Xen calls it too.
- **VM with our agent:** `AgentQuiescer` freezes, then `CreateSnapshot` with `quiesce=false`, then thaw.
- **VM without our agent:**
  - `VmBackupRequest.PlatformQuiesce = true` makes the snapshot use `quiesce=true`. `GuestProcessingService` returns this instead of a crash-consistent reason.
  - Windows (VSS through Tools) counts as application-consistent.
  - Linux (fsfreeze): `ApplicationConsistent=false`, reason "file systems frozen by VMware Tools".
  - If quiescing fails, retry without it, following `GuestFailurePolicy`.
- **Agent install through VMware Tools:** an optional `IGuestOperations` on the session.
  - `AgentDeployer` uses it when the VM is vSphere and SMB fails, or always if the job says so.
  - Files go through `InitiateFileTransferToGuest`. The PUT goes to the ESXi host, pinned from `HostSystem.config.certificate` read over the authenticated vCenter session.
  - `sc.exe` runs through `StartProgramInGuest`; the exit code is polled with `ListProcessesInGuest`. Uses the job's Windows credential.
  - UAC with non-built-in admins is checked in spike S5.

### Restore

**Placement fields on `VmRestorePlan`** (also on `RestoreRequestDto`): `TargetFolderId`, `TargetResourcePoolId` (a cluster or resource pool; the host is optional), `DiskFormat` per disk key (`source|thin|thick-lazy|thick-eager`), `ProxyIds`, `QuickRollback`, `KeepSafetySnapshot`.

**`vsphere-config-v1`:** zstd-compressed JSON of the relevant `ConfigInfo` parts.
- guestId, hardware version, firmware, secure boot, CPU/cores/memory, hot-add flags, nested HV;
- controllers; disks (key, controller, unit, capacity, mode, provisioning); NICs (type, MAC, standard/dvPortgroup/opaque backing); annotation;
- a filtered `extraConfig` (without `vm.genid*`, swap paths, `nvram`);
- `uuid`/`instanceUuid`;
- `files.vmx` (text, for reference only) and `files.nvram` (base64), both fetched over `/folder`.

**New VM**
1. `MakeDirectory` (or `CreateDirectory` on vSAN).
2. `CreateDiskAsync`: the proxy creates the disk itself with `ReconfigVM` add and `fileOperation=create` in the target folder. This works the same on VMFS, NFS and vSAN.
3. The writer writes only the image's data extents and flushes.
4. Detach, keeping the file.
5. `CreateVmAsync`: `CreateVM_Task` with the existing disk files and mapped networks, after checking the target with `QueryConfigTarget`/`QueryConfigOption` (lowering the hardware version if needed).
6. Upload the nvram with a `/folder` PUT, then power on.

vTPM and passthrough devices in the backup are not recreated; the restore warns about it.

**Original location:** the same flow as Xen (`OriginalVmIds`, `VmReplacementEntity`). Shut the old VM down gracefully, rename it, disconnect its NICs, create the new VM with the same BIOS UUID, then delete the old one if `DeleteReplaced` is set.

**Cross-kind guard:** `RestoreSessionRunner` refuses a restore point whose `Source.Provider` or metadata format does not match the target kind.

### Quick rollback

An optional interface `IVmRollbackSupport` on the session, with its own branch in the runner.
1. **Checks:** same vCenter; the VM is found through the replacement chain; every disk matches by `SourceDiskId` and capacity; CBT is on.
2. **Shut down** (gracefully, then power off).
3. **Changed areas:** QCDA without a snapshot (allowed while powered off) against changeId c_X of each disk.
4. **Fallback:** if c_X is refused, use the newest later restore point Y that is accepted: dirty = QCDA(c_Y) ∪ diff(index_X, index_Y), with the diff computed from the chunk indexes. If no restore point works, refuse and suggest a full restore.
5. **Safety snapshot** (on by default; ledger cleanup reverts to it).
6. **Write:** attach the VM's current disks writable to the proxy and write exactly the dirty extents, zeros included (`IExtentDiskWriter`). Flush and detach.
7. **Finish:** remove the safety snapshot unless it is to be kept; reset CBT while powered off, so the next backup is full with a reason; power on if requested.
8. **On failure:** revert to the safety snapshot and remove it.

### Server refactors (task T1)

- `HypervisorKind.VSphere`, added at the end of the enum.
- `EngineServices.cs` (~579-590): a helper registers a kind's provider only when no provider of that kind exists.
- `HypervisorConnection.Options`: a new jsonb dictionary. The migration copies `AllowInsecureNbd` into it.
- `ConnectionManager.BuildSettings`: generic, without `using Backdafuckup.Xen`.
- `InfrastructureEndpoints`:
  - parse the kind through a helper with display nouns (pool, cluster, vCenter);
  - `TestConnectionAsync` gets the real options;
  - `?kind=` accepts several values.
- `StateMetrics.cs` (~456): polls XenServer and VSphere.
- `AgentService`: matching by `GuestUuid`.
- `BackupSessionRunner`:
  - the `xe` hint (~548) comes from the provider;
  - `AcrossDisks` (~716) honours `DisksAtOnce`;
  - transport validation per kind (~1040).
- `RestoreSessionRunner`: the cross-kind guard.
- Neutral texts in `OperationEndpoints.cs` (~29) and `AgentDeployer.cs` (~53).
- `Contracts/Api.cs`: default kinds.
- Web pages: remove the hard-coded `kind=XenServer` filters (Pools, Inventory, Agents, RestoreWizard, JobEditor `KindVms`).

### Console, metrics, documentation

**Connection wizard** (`AddVcenterWizard`, or a kind-aware `AddPoolWizard`):
- probe and trust the vCenter certificate;
- SSO user;
- test: `apiType` must be VirtualCenter, check the licence (free ESXi is detected), list missing privileges via `FetchUserPrivilegeOnEntities`, store the ESXi pins.

**Pages**
- `Pools.razor` becomes "Virtualization" and lists pools and vCenters.
- Inventory tree Datacenter → Cluster → Host, plus folders. New `InventoryKind` values: `Datacenter`, `Cluster`, `Folder`, `ResourcePool`.
- Job editor:
  - adds "vSphere VMs", proxy selection (Automatic or a list), the Tools fallback, the install options and "reset CBT on active full";
  - hides transport mode and "With memory" for vSphere.
- Restore wizard: New / Original (replace) / Quick rollback, placement fields, disk format, network map, proxy.
- New `Proxies.razor`: the VM and host each proxy resolved to, the datastores it sees, version, capabilities, leases, install command, detach leftovers.

**Metrics**
- `StateMetrics`: datastore space; age and size of snapshots (from `layoutEx`).
- New: `bdfu_proxy_connected`, `bdfu_proxy_attached_disks`, `bdfu_hotadd_attach_seconds`.
- Alerts: `BackdafuckupProxyDisconnected`, `BackdafuckupLeftoverBackupSnapshot`, `BackdafuckupLeftoverHotAddDisk`.

**Documentation**
- `docs/admin-guide.md`: chapter "VMware vSphere" (licence, proxy VM setup, CBT, guest processing, quick rollback, limits) and "Required vSphere privileges".
- `docs/lab-runbook.md`: section V.

## Tasks (in order; each with its tests, the full suite green, and a commit)

**T0 — Spikes** on the vSphere 8 test host, written up in `spike.md`:

| Spike | What it settles |
|---|---|
| S0 | Client skeleton: login, property collector, snapshot; how strict vCenter is about XML. |
| S1 | HotAdd of a base and of a delta snapshot disk; IDE/SATA/NVMe sources; vRDM; VPD mapping; proxy reboot with a disk attached. |
| S2 | Writable attach of a new disk, and of the current delta of a powered-off VM. |
| S3 | CBT activation with and without user snapshots; `"*"` on VMFS and NFS; QCDA page sizes; whether writes from the proxy are tracked or change the epoch; QCDA on a powered-off VM without a snapshot. |
| S4 | How visible the Tools quiesce result is. |
| S5 | Guest operations and UAC; ESXi pins. |
| S6 | `/folder` for `.vmx` and `.nvram`. |
| S7 | NBD over WSS throughput; target at least 500 MB/s per disk with 4 connections. |

**T1 — Server refactors** (above). Tests: several providers registered; cross-kind restore refused; VMware serial matching; `AcrossDisks`; all existing suites green.

**T2 — Core:** `GuestFreezeOrchestration`, `ChangedExtents.Allocated` in the pipeline, `IExtentDiskWriter`, proxy pool abstractions, request and plan fields. Tests: only allocated chunks are read and the rest is zero; Xen tests unchanged.

**T3 — vim25 client** and `tests/Backdafuckup.Testing/Vim/FakeVcenter.cs` + `FakeVimHandler.cs` (in the style of `FakeXenPool`, with CBT and HotAdd simulated over files). Tests: golden envelopes, fault mapping, new login after `NotAuthenticated`, paging, version negotiation; optional vcsim lane.

**T4 — Provider:** connection test, inventory, storage state, guest addresses. Tests against the fake.

**T5 — Proxy:**
- framing, NBD server and client over in-memory WebSockets, discovery against a fake sysfs;
- enrollment and auth, update offer and loop guard, tunnels;
- unit file and build script; `ProxyAgents`, `ProxyEntity` and its migration.
- A Linux-only test lane with loop devices.

**T6 — HotAdd manager and proxy selection.** The fake simulates attaching by binding fake disk bytes to an in-process proxy. Tests: rejection messages, crash sweep, no destroy on removal, unit 7 skipped.

**T7 — Backup session.** Integration tests:
- full and incremental;
- epoch change;
- user snapshots;
- excluded disks;
- crash leftovers cleaned up;
- Tools quiescing fails, so the backup is crash-consistent.

**T8 — Guest processing**, and agent install through guest operations.

**T9 — Restore** (new VM and original location). Tests: disk images byte-identical, config round-trip, a failure removes everything.

**T10 — Quick rollback.** Tests: number of dirty chunks written, the X/Y union, refusal reasons, safety-snapshot revert after a crash, CBT reset afterwards.

**T11 — Console** (bUnit tests).

**T12 — Metrics, documentation, `THIRD-PARTY-NOTICES.md`.**

**T13 — Lab rows V1–V22 on the test host:**
- adding the vCenter and the proxy; self-update;
- Linux/Windows full and incremental;
- agent, Tools quiescing and install through guest operations;
- server or proxy killed mid-run;
- restores (thin, UEFI, network map); replace; rollback; CBT reset;
- user snapshots; excluded disks; NVMe/IDE; a proxy without datastore access; two proxies;
- FLR/SQL/AD from a vSphere restore point; metrics.

## Risks

- HotAdd quirks that VDDK normally hides: locks, rescan races, LVM auto-activation on the proxy.
- CBT bugs, and writes from the proxy not being tracked. The forced full read after a reset covers the latter.
- XML element order in the hand-written client.
- A wrong `fileOperation` destroying a production disk. Unit test, review, and never `destroy` from proxy code.
- Rollback hitting the wrong VM. Strict identity checks and a typed confirmation in the console guard against it.
- A proxy can read every disk: one-time install tokens, hardening, pinned TLS.
- The lab licence expiring (evaluation: 60 days).

## Seams for specs 2 and 3

**Spec 3**
- The runner computes `GetChangedExtentsAsync` for all disks before opening any reader (a small reordering).
- The vSphere session then calls `ISnapshotLifecycleHook.OnVmSnapshotCreatedAsync` (array snapshot), may remove the VM snapshot early, and reads through `IDiskTransport` by priority:
  - `HotAddTransport` now;
  - a resignatured-datastore HotAdd or a SAN-LUN transport later.
- The proxy's capabilities (`san-lun-v1`) and the NBD server stay as they are.

**Spec 2**
- `VSphereVmSpecBuilder` takes disk paths, so it can register a VM on an NFS datastore (`CreateNasDatastore`) and move it with `RelocateVM_Task`.

## Prerequisites from the user (asked again before the matching task)

- **Test host:** ESXi 8 + vCenter 8 with a licence (evaluation or paid), a vCenter account with the backup privileges, and a Linux proxy VM with `disk.EnableUUID=TRUE`.
- **No deploy to the production backup server** without asking. Live tests run on the test host.
