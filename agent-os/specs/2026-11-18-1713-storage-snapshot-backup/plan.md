# Plan: Backup from storage-array snapshots (spec 3 of 3)

Hypervisors: XCP-ng (exists) and VMware vSphere (spec 1, `../2026-11-18-1713-vsphere-provider/`). Array: HPE Alletra Storage MP B10000, through its public WSAPI v3.

## Context

**The sequence.** The roadmap's MVP item "Backup from storage snapshots" keeps the live cluster almost unstressed:
1. a VM snapshot (application-consistent, held only seconds);
2. an array snapshot of the volumes holding the VM's disks;
3. the VM snapshot is removed;
4. the backup reads from the array snapshot over the SAN, not from the hosts;
5. the array snapshot is deleted.

Changed blocks still come from the hypervisor's CBT.

**What exists.** Only the two unused interfaces `IDiskTransport` and `ISnapshotLifecycleHook` (`src/Backdafuckup.Core/DataMover/Abstractions.cs`). Nothing calls them, so their shape can change.

**Earlier verdict.** The SAN report (`reports/XCP-ng snapshot speed and SAN backup.md`) rated the XCP-ng path "not now, an opt-in experimental accelerator". The user now wants it, so it is built opt-in with the safeguards below.

**Approach E superseded.** Approach E of `../2026-11-04-2026-consistent-snapshots-slow-storage/` (array snapshot without an XAPI snapshot) gives no CBT and reads live leaves; this spec replaces it.

## Decisions

### By the user (2026-11-18)
- **Array plugin interface plus the Alletra MP plugin**, for **both** hypervisors.
- **vSphere, two read methods:**
  - **(a) supported default:** present the snapshot LUN to an ESXi host, resignature and mount it as a temporary datastore with public vim25 calls, and let the proxy hot-add the VMDKs from it;
  - **(b) opt-in accelerator:** a **VMFS6 reader** of our own in the Linux proxy reads the snapshot LUN directly over iSCSI.
- **The VMFS policy exception.** VMware never documented VMFS. The user accepted relaxing "nothing undocumented" for this one on-disk format. Only GPL-2.0-or-later code may be ported, with verification and a fallback.
- **2026-11-19: OpenVmfs instead of a port.** The user's own Apache-2.0 reader is used as a dependency (nuget.org once published; until then the backdafuckup side is built against a fake). Nothing is ported, so the GPL conditions do not apply; opt-in, verification and fallback still do.
- **XCP-ng:** the proxy reads LVHD (LVM VG with VHD-in-LV) from the snapshot LUN in user space. Opt-in, with safeguards.
- **One Linux proxy agent** (from spec 1) does HotAdd and SAN reads.
- **Test environment:** the vSphere 8 test host with Alletra MP over iSCSI, and the lab XCP-ng pool on the Alletra. **Only dedicated test volumes**, never production volumes without explicit approval. Nothing is deleted that the product did not create.

### In the design (2026-11-18)

**Batching**
- One array snapshot per volume for up to 8 VMs of a job run (60 s window).
- Snapshot and read phases get separate semaphores.

**When the VM snapshot is released**
- **"after open"** for XCP-ng and the VMFS reader: the disks are opened and checked on the proxy first, so a failure can still read from the VM snapshot.
- **"at once"** for vSphere (a), whose rescan, resignature and mount take 30–120 s. If it then fails, a new VM snapshot is taken without the hook.

**Alletra snapshots**
- **Safety net:** `expireSecs` as a fallback cleanup, extended while in use. **Never `retainSecs`** (retention cannot be shortened once set).
- **Presentation:** at most one presented snapshot per source volume and target at a time. ESXi reports "multipleCopies", and LVM sees duplicate identities.

**On XCP-ng, this does not make `VM.snapshot` itself faster.** It shortens how long the snapshot lives, moves the reads off the hosts, and gives sparse full backups.

## Array plugin

### Core (`src/Backdafuckup.Core/Arrays/`)
- **`IStorageArrayProvider`:** `Kind`, `ProbeCertificateAsync`, `TestConnectionAsync`, `OpenAsync`. The test returns `ArrayInfo`: model, serial, system WWN, OS and API version, licence features, the user's privileges, session timeout.
- **`IStorageArraySession : IAsyncDisposable`:**
  - `FindVolumesByWwnAsync`, `ListHostsAsync` (with initiators), `GetIscsiPortalsAsync`;
  - `CreateSnapshotsAsync(ArraySnapshotRequest{Volumes, Consistent, NameBase, Comment, ReadOnly, Expiry}, ledger, log)`, `ExtendExpiryAsync`;
  - `PresentAsync(snapshot, hostName) → ArrayPresentation(Uid, Lun, Wwn)`, `UnpresentAsync`;
  - `DeleteSnapshotAsync`, which needs proof that the snapshot is ours;
  - `FindTaggedAsync`, `CleanupArtifactAsync`.
- **Hypervisor side** (optional interfaces on `IHypervisorSession`):
  - `IStorageBackingSource.GetStorageBackingAsync(storageIds) → StorageBacking(StorageId, Layout lvhd|vmfs6|vmfs5|other, LunWwns[], Refusal?)`.
    - **Xen:** only `lvmoiscsi`/`lvmohba`, from PBD `device_config["SCSIid"]` ("3" + WWN); all PBDs must agree.
    - **vSphere:** `VmfsDatastoreInfo.vmfs.extent[].diskName` (`naa.<wwn>`) and `majorVersion`.
  - `IStorageSnapshotGate.HoldAsync(storageIds)`; Xen wraps `XenSnapshotGate`.
- **Artifact kinds:** `ArraySnapshot`, `ArrayVlun`, `ArrayHost`, `EsxTempDatastore`, `EsxScsiLunState`, `ProxyLun`. HotAdd disks use spec 1's kind.

### Alletra plugin (`src/Backdafuckup.Arrays.Alletra`)

A hand-written `HttpClient` client. DTOs come from `research/HPE-Alletra/ws-open-api-2.5.0.json`, only the fields we use. The array also serves its own spec under `/docs`; it is the reference for its version.

| Area | Calls and rules |
|---|---|
| Session | `POST /api/v3/credentials {user,password}` returns `key`, `sessionId`, `arrayPrivileges`. `Authorization: Bearer <key>` (RFC 6750 per the spec; confirm in S1). Idle timeout from `/server/info`. One new login on 401. `DELETE /api/v3/credentials/{key}` on dispose. TLS trust on first use with SPKI pins (`Core/Tls`), as for pools. |
| Async calls | Handle 201 (resource) and 202 (task), each with a `Location` header. Poll `/api/v3/tasks/{uid}` until `STATE_FINISHED` or `STATE_WARNING` (logged); fail on `STATE_FAILED`/`STATE_CANCELED`. New objects come from `createdUids`. A 409 shows the array's message. |
| Volumes | `GET /api/v3/volumes`, filtered client-side by `wwn`. The spec documents no query filters. |
| Snapshot | `POST /api/v3/volumes/{uid}` with `CREATE_SNAPSHOT_VOLUME {customName, namePattern: CUSTOM, readOnly, expireSecs, comment}`. **Never `retainSecs`.** `CREATE_SNAPSHOT_APPSET` only for a datastore spanning several LUNs on an existing appset with exactly matching members; v1 refuses that case. While a lease lives, `PATCH /volumes/{uid}` `{expireSecs}` every 30 minutes. |
| Naming | `customName`: `bdfu-<8 hex of session>-<nn>`, kept to 31 characters or fewer until S1 shows 255 is honoured. `comment`: `backdafuckup server=<instance> session=<id> source=<base uid> created=<utc>`. |
| Before any delete or unexport | All of these must hold: the name prefix, our server id in the comment, `type=VVTYPE_SNAPSHOT`, `copyOfID` equal to the recorded base, and a ledger row (or an orphan older than 2 h). |
| Presentation | `POST /api/v3/vluns {volumeName, hostName (one host, never "set:"), autoLun: true}`; uid from `Location`. Removal is `DELETE /vluns/{uid}`, always before deleting the snapshot. |
| Hosts | Map ESXi IQNs (`HostInternetScsiHba.iScsiName`) and proxy IQNs to host objects through `/hostpaths`. A proxy's host object (`POST /hosts {name: bdfu-proxy-x, iscsiPaths: [IQN], persona}`) is created only when the admin clicks. |
| Licence | Show `licenses.consolidatedFeatures` (name, expiry) from `/systems`. Warn if no snapshot feature is listed or it has expired; never block. |
| Least privilege | Document a WSAPI user with the "edit" role, scoped to a Virtual Domain. WSAPI v3 has no custom roles. |

### Server and console
- **Entities and migration:**
  - `StorageSystemEntity`: kind, address, credential, trust mode and pins, options as JSON, facts, status;
  - `StorageVolumeMapping`: volume uid/WWN, `ConnectionId`, storage native id, layout, enabled, methods, refusal;
  - `StorageHostLink`;
  - `ProviderArtifactEntity` gains a nullable `StorageSystemId`.
- **API:** `/api/storage-systems`: probe certificate, test, volumes, hosts, leftovers.
- **`Pages/StorageSystems.razor`** (built like `Pools.razor`):
  1. add the system, accept its certificate, test it (model, serial, OS, WSAPI version, licences, role);
  2. **Volumes:** datastores and SRs mapped automatically by WWN, with a toggle per volume and the method or the refusal (for example "spans 2 LUNs");
  3. **Access:** ESXi hosts matched by IQN; proxies with "create host object";
  4. **Options:** expiry, maximum VMs per array snapshot, VM-snapshot release policy, experimental readers, verify mode, probation runs;
  5. **Leftovers.**
- **Mapping refresh:** with the inventory, and checked again at job start (the SR's `SCSIid` or the datastore's NAA must equal the volume's WWN).
- **Job editor:** "Backup from storage snapshots: Off / Automatic" (Off for existing jobs). For vSphere also the read method: ESXi (supported), VMFS reader (experimental), or Automatic.

## Orchestration

**The hook**
- **When:** right after `BeginBackupAsync` (the guest is already thawed) and before `BackupDisksAsync` in `VmBackupTask.RunCoreAsync` (`src/Backdafuckup.Server/Engine/BackupSessionRunner.cs`).
- **Finish:** `OnBackupFinishedAsync` runs after `CommitAsync`, in the `finally`.
- **Return value:** `OnVmSnapshotCreatedAsync` returns an `ArrayReadLease?`; null means the normal path.

**New default members on `IVmBackupSession`**
- `CanReleaseEarly`;
- `ReleaseSnapshotAsync(log, ct)`: afterwards `OpenReaderAsync` throws, and `CommitAsync` only completes ledger rows;
- `SnapshotFacts.Lifetime`: the time from snapshot to removal, this feature's key metric.

**Batching** (`ArraySnapshotService`, a server singleton keeping batches per job session and storage system)
1. **Two phases:** the `MaxParallelVms` semaphore is split. A snapshot-phase semaphore of size `MaxVmsPerArraySnapshot` (default 8) runs VMs up to the array snapshot; `MaxParallelVms` then limits only reading.
2. **A batch closes** when every eligible VM of the run has joined or failed, when the size limit is reached, or 60 s after the first VM joined.
3. **On close:**
   - take `IStorageSnapshotGate` for all SRs and datastores on the batch's volumes, so none of our hypervisor snapshot operations is half done;
   - take one snapshot per volume (about 1 s each);
   - release the gate.

   Log line: "Array snapshot bdfu-… of <volume> in 1.2 s for 6 VMs".
4. **Per VM:**
   - compute `ChangedExtents` for all disks into a cache (the CBT queries still need the VM snapshot);
   - record the new tokens;
   - release the VM snapshot according to the policy:
     - **"after open"** (XCP-ng, VMFS reader): present to the proxy, open and check every disk, then release. On failure the VM snapshot still exists, and the disks are read through NBD or HotAdd.
     - **"at once"** (vSphere (a)): release immediately. If a later step fails, take a new VM snapshot without the hook in the same attempt and compute the extents again.
5. **Lease** with a reference count: when the last VM of the batch has been read, unpresent and delete.

**Fallback**
- One log line, for example "Disk X of Y: not read from the array snapshot (reason); reading through NBD from the VM snapshot".
- Counted in `bdfu_array_fallback_total{reason}`.
- In v1 the **whole VM** takes the normal path when any disk is not on a mapped array volume, or is encrypted, an RDM, a linked clone, QCOW2, or on several extents.

**Crash cleanup**
- A ledger row is written before every call.
- Recovery groups artifacts by `StorageSystemId` and cleans in a fixed order:
  1. proxy devices;
  2. HotAdd disks;
  3. ESXi temporary datastore;
  4. VLUNs;
  5. array snapshots;
  6. hypervisor snapshots (as today).
- Then the orphan scan. The array's `expireSecs` is the last safety net.

## vSphere method (a): ESXi host (supported)

1. **Before the snapshot:** the datastore has one extent (VMFS 5 or 6). Refuse encrypted disks (`backing.keyId`), RDMs, linked clones.
2. **`CreateSnapshot_Task`** (quiescing per spec 1). From `snapshot.config` take each disk's backing `fileName` (the leaf at snapshot time, with its parent chain) and its `changeId`.
3. **Array snapshot,** read-write. Resignaturing writes a new VMFS signature to the snapshot; the source volume is never written.
4. **CBT:** `QueryChangedDiskAreas(snapshot, deviceKey, offset, previous changeId or "*")`, cached. The new token is the snapshot disk's `changeId`. Then `RemoveSnapshot_Task(removeChildren=false, consolidate=true)`.
5. **Present** the snapshot to the array host object of the proxy VM's `runtime.host` only.
6. **Mount:**
   - `HostStorageSystem.RescanHba(<iSCSI vmhba>)` (`RescanAllHba` only if needed), then `RescanVmfs`;
   - `HostDatastoreSystem.QueryUnresolvedVmfsVolumes`; match `extent.device.diskName == naa.<snap wwn>` with status resolvable;
   - `ResignatureUnresolvedVmfsVolume_Task({extentDevicePath: ["/vmfs/devices/disks/naa…"]})` returns a datastore `snap-xxxxxxxx-<label>`;
   - `RenameDatastore` to `snap-bdfu<8hex>-<label>` as our tag; keep ESXi's name if the rename is refused;
   - alternative to check in the spike: `HostStorageSystem.ResolveMultipleUnresolvedVmfsVolumes`.
7. **Path mapping:** `[orig] dir/x.vmdk` becomes `[snap-bdfu…] dir/x.vmdk`, checked with `SearchDatastore_Task` (the file exists and its size matches).
8. **HotAdd** through spec 1's transport, with a path override and mode `independent_nonpersistent`. Read the cached CBT extents.
9. **Teardown,** in this order:
   1. `ReconfigVM_Task` removes the disk (no `fileOperation`);
   2. `UnmountVmfsVolume(new uuid)`;
   3. `DetachScsiLun`;
   4. `DELETE` the VLUN;
   5. `RescanHba`;
   6. `DeleteScsiLunState(naa)`;
   7. delete the array snapshot.

Document a DRS override for proxy VMs. Presenting the LUN to one host only keeps HA from using it as a heartbeat datastore.

## vSphere method (b): VMFS6 reader (experimental, opt-in)

### The reader: OpenVmfs (decided 2026-11-19, replaces the port of vmfs6-tools)
The user wrote **OpenVmfs** in a side project: a read-only VMFS6 and VMDK reader of its own (Rust library, C interface, .NET binding; Apache-2.0, nothing ported from vmfs-tools, vmfs6-tools or dissect). It replaces the planned port of vmfs6-tools, so the licence audit, the GPL-2.0-or-later headers and the legal review fall away. Background and measurements: the OpenVmfs repository (`docs/consistency.md`).

- **What it reads:** VMFS 6.82 thin and thick disks (to-be-zeroed blocks read as zeros), SESparse snapshot chains, datastores over 16 TiB and over several LUNs. Compared with ESXi itself (hashes, `vmkfstools`, consolidated clones), dissect.vmfs and QEMU; vmfs6-tools reads lazy-zeroed blocks and volumes over 16 TiB wrongly.
- **What it refuses:** VMFS5, VMFS-L, RDM, encryption, COWD deltas, chains across datastores (`Unsupported`), and **a disk a host had open for writing when the array snapshot was taken (`InUse`)**. The measurements make the order of the spec mandatory: VM snapshot, then array snapshot, and the disk *under* the VM snapshot is read; without a VM snapshot the newest blocks were missing in 10 of 10 runs.
- **Where it runs:** first in the server, over the LUN reader the proxy already serves (as LVHD). A reader in the proxy stays a later optimisation.
- **In backdafuckup:** `Core/Arrays/VmfsReader.cs` (`IVmfsReader`, `IVmfsVolume`, `IVmfsDisk`, `VmfsRefusedException`), the VMFS branch of `ArraySnapshotService`, `IArraySnapshotFacts` and the early release in `VSphereBackupSession`. The adapter over the OpenVmfs package (`src/Backdafuckup.Vmfs`) comes once the package is on nuget.org; until then a build has no VMFS reader and VMFS VMs take the normal path. The package is not committed to this repository.
- **Accepted, unlike the first plan:** VMs that already had snapshots (the disk under our snapshot is a closed delta). **Still refused:** datastores on several LUNs (they would need one consistent snapshot of all volumes).
- **Wanted from OpenVmfs:** an extent mapping ("logical offset → LUN offset or zero"), so that data is read asynchronously through our pipelined reader and only metadata through its blocking device callback; a licence list of the linked Rust crates for the notices; build paths remapped out of the binaries before publishing.

### Safety
- The snapshot is presented read-only.
- **Trust anchor:** method (b) is used only for a disk that already has a restore point (built).
- **Gap:** the array snapshot is taken at least `MinSecondsAfterVmSnapshot` (2 s) after the batch's last VM snapshot; the measurements covered 0.6-2 s.
- **Every run** first compares 32 chunk-aligned 1 MiB regions that CBT reports unchanged with the previous restore point's chunk ids (content hashes).
- **Verify mode** for the first 5 runs per storage system and reader, and after a VMFS format version change: 16 regions of each disk, spread over the regions read, are compared with HotAdd of the VM snapshot's disk, while the VM snapshot is still held (built for LVHD and VMFS: `VerifyAgainstVmSnapshot`, state in `storage_systems.readers`).
- **On a mismatch,** the disk falls back, the reader is switched off ("tripped") for that storage system until an admin turns it back on (Storage systems, `POST /api/storage-systems/{id}/readers/{reader}/reset`), and `BackdafuckupArrayReaderSwitchedOff` fires. A mismatch of the unchanged regions after the release trips it too (that backup fails).

### Recording the read method
- `DiskBackupStats.Transport` gains `array-vmfs`, `array-esxi-hotadd` and `array-lvhd`.
- New `DiskBackupStats.Verification`.
- New `ArraySnapshotInfo` in the manifest.
- New `RestorePointDisk.Transport` column, so the console shows the method.

### Tests
- **Without the package:** the orchestration against a fake `IVmfsReader` whose files are byte ranges of the snapshot LUN (`FakeVmfsReader`): reading, refusals (`InUse`, missing, size), verify mode, format change, trust anchor, gap.
- **With the adapter:** OpenVmfs's own fixtures (`fixture-zero-v1`, `fixture-chain-v1`, with ESXi's hashes) behind a `IDiskReader`; the format itself is tested in OpenVmfs.
- **Lab differential test:** SHA-256 per MiB of whole disks read by (a) and by (b).

## XCP-ng path (LVHD, opt-in)

**Facts captured before the release** (in `SnapshotDisk.Locator`)
- `srScsiId` and `srUuid`;
- the disk format: VHD only; QCOW2 and raw are refused;
- the chain: the snapshot VDI, then `sm_config["vhd-parent"]` followed through the hidden base copies with `VDI.get_by_uuid`, with `virtual_size` and `physical_utilisation` for each member;
- the product version and the `SM` record version, checked against an allow-list.

**Order**
1. `VM.snapshot` (through the gate, with freeze and thaw).
2. Array snapshot, while the SR gate is held.
3. `list_changed_blocks` per disk, cached.
4. Open and check on the proxy (policy "after open").
5. `VM.destroy`.
6. `VDI.data_destroy`: the artifact becomes `CbtMetadataVdi` (Active). It is destroyed if the backup fails and marked Done when the restore point commits.
7. Read from the array snapshot.

**Why reading after `data_destroy` works.** The array snapshot keeps `VHD-<snapUuid>` and its parents as they were; `data_destroy` and the leaf-coalesce that follows change only the live LUN. For a session released early, `CommitAsync` only updates the ledger. Spike S3 also checks `list_changed_blocks` between two `cbt_metadata` VDIs.

**Library (`src/Backdafuckup.Lvhd`)**
- **`Lvm2Reader`:** our own code, written from the libvslvm format description; DiscUtils.Lvm stays a test oracle (its public API exposes no tags or segment maps).
  - Finds `LABELONE` in sectors 0–3 and reads the PV header and metadata-area header.
  - Takes the `raw_locn` with the highest seqno and checks its CRC, then parses the metadata text.
  - Requires VG name `VG_XenStorage-<sr>`, exactly one PV, and linear segments only.
  - **Never runs `vgimportclone`, never activates anything.**
- **`LvhdValidator`** refuses the snapshot when:
  - a journal LV (`clone_`, `leaf_`, `relink_`, `coalesce_`, `inflate_`, `zero_`) or an `OLD_` LV names a member of our chain;
  - a member is `QCOW2-` or `LV-`;
  - an LV's size differs from `physical_utilisation`, or is smaller than the end of its VHD;
  - the chain read from the VHD headers (parent UUIDs, locators `VHD-<uuid>`) differs from the chain XAPI reported.
- **`VhdChainReader`:** new, random access, written from the Microsoft VHD specification; shares footer and header parsing with `VhdStreamReader`.
  - Uses the footer copy at the start of the file; a trailing footer overwritten with `0xC7` is logged.
  - Checks the checksum, reads the BAT and the per-block sector bitmaps (cached), and resolves each sector from the leaf towards the root.
  - Xen's `tdbatmap` is an optional speed-up.
  - The union of allocated blocks gives sparse full backups.

**Safety and tests**
- The CBT-unchanged comparison runs on every backup.
- Verify mode compares a sample with NBD reads of the XAPI snapshot VDI.
- Tests use synthetic PV images (LVM text plus VHD chains written by `VhdImageWriter`) covering every refusal case, with DiscUtils as an oracle, plus a lab test on our own VM on a dedicated test SR.

## Proxy agent extensions (on spec 1's agent)

- **iSCSI through `iscsiadm`** (no initiator of our own):
  - discover portals from `/ports` on the storage subnets; log in once per target with `node.startup=manual`;
  - per snapshot: `iscsiadm -m session --rescan` and `udevadm settle`;
  - find the device by `/sys/block/*/device/wwid == naa.<snap wwn>` or `/dev/disk/by-id/wwn-0x…` (dm-multipath if configured);
  - `blockdev --setro` on all paths, open `O_RDONLY|O_DIRECT`, check the size.
- **Release:** close the device, `multipath -f`, `echo 1 > /sys/block/sdX/device/delete`. Then the server deletes the VLUN, and a rescan confirms the device is gone.
- **Self-check at startup:** SAN mode stays off unless LVM leaves foreign PVs alone (devices file with local disks only, or a `global_filter`, and `event_activation=0`) and nothing auto-mounts.
- **Placement:**
  - a proxy VM uses an in-guest initiator; on XCP-ng it has VIFs on the storage networks and never uses the pool's own iSCSI sessions;
  - a physical Linux proxy runs the same agent but cannot do HotAdd, so it serves (b) and XCP-ng only.
- **Messages:**
  - `AttachLun`;
  - `OpenSource{vmfs-file | lvhd-vdi, facts}`, which returns a report with structure, allocation and refusals;
  - `ReadExtents`, `VerifySample`.

## Tasks (in order; each with its tests, the full suite green, and a commit)

**Spikes first,** written up in `spike.md`:

| Spike | What it settles |
|---|---|
| S1 | Alletra, on our own test objects only: auth header, 201 vs 202, snapshot and expiry, name length, deleting an exported snapshot, expiry while exported, `PATCH expireSecs`, how long a snapshot takes. |
| S2 | Proxy VM iSCSI in the lab XCP-ng pool: host persona, rescan, WWN lookup, throughput compared with NBD. |
| S3 | LVHD read from a snapshot of a dedicated test SR compared with NBD; `list_changed_blocks` after `data_destroy`; whether the VHD headers are on disk when the array snapshot is taken. |
| S4 | Resignature on vSphere 8 plus HotAdd plus teardown, timing each step. |
| S5 | Done differently: OpenVmfs compared with ESXi, dissect.vmfs and QEMU (OpenVmfs repository, `docs/consistency.md`). |

**Build tasks**
1. Core interfaces, the hook, early release, artifact kinds, manifest fields (unit tests).
2. Alletra client with a fake WSAPI server (`tests/Backdafuckup.Arrays.Tests`, an ASP.NET TestServer replaying the OpenAPI shapes: 201/202/401/409, task failures, refusal to touch objects that are not ours).
3. Server: entities, migration, API, mapping, ledger `StorageSystemId`, cleanup order, orphan scan. Server tests with fake array, proxy and hypervisor; a simulated crash after each step.
4. Engine: `ArraySnapshotService`, the two phase semaphores, fallbacks, metrics.
5. Proxy SAN access (fake sysfs and `iscsiadm` in tests).
6. The Lvhd library.
7. **XCP-ng integration plus lab rows: the first part that can ship.**
8. vSphere method (a). Needs spec 1's vim25 client and HotAdd.
9. Method (b) with OpenVmfs: verify mode and trip (done for LVHD and VMFS), the VMFS branch with a fake reader (done), then the OpenVmfs adapter once the package is published.
10. Console, metrics, alerts, Grafana row, documentation.

**Metrics:**
- `bdfu_array_snapshot_seconds`, `bdfu_array_batch_vms`;
- `bdfu_vm_snapshot_lifetime_seconds{path}`;
- `bdfu_array_fallback_total{reason}`, `bdfu_array_verify_total{reader,result}`;
- `bdfu_array_leftovers{kind}`, `bdfu_array_present_seconds{method}`.

Alerts fire on leftovers older than 1 h and on any verify mismatch.

**Documentation:**
- `docs/admin-guide.md` section "Backup from storage snapshots": WSAPI user in a Virtual Domain, the proxy's host object, storage networks, LVM filter, DRS, what is experimental, what is refused, cleanup.
- `docs/lab-runbook.md` section N: register, mapping, XCP-ng incremental, verify mode, vSphere (a), (b) against (a), crash after the array snapshot, proxy unreachable, coalesce in progress refused, expiry safety net, leftovers.
- `THIRD-PARTY-NOTICES.md` rows: OpenVmfs (Apache-2.0, with the licences of its linked crates), libvslvm description, VHD specification, `sm` (read for the layout, no code copied), WSAPI OpenAPI.

## Risks

- OpenVmfs was measured in a small lab (one ESXi 7.0.3 host, VMFS 6.82): nothing yet against a real Alletra snapshot LUN, other ESXi versions or several hosts on one datastore. Verify mode, the unchanged check and the trip carry that risk.
- To-be-zeroed blocks and the VMFS journal can leak stale data; the verification layers catch it.
- `sm` layout changes and garbage-collection states: the refusal rules plus the allow-list.
- The proxy writing to the snapshot or seeing the live LUN: read-only on every path, and the LVM self-check.
- Leftovers on the array or on ESXi: ledger, orphan scan, `expireSecs`.
- The "edit" role is powerful: a Virtual Domain limits it.
- Batching makes VM snapshots live longer; the 60 s window and the batch size limit this.

## Prerequisites from the user (asked again before the matching task)

- A WSAPI user in a Virtual Domain.
- **Dedicated test volumes:** one as an SR in the lab pool, one as a datastore on the test host. Creating them is a system change and is asked for first. No snapshots of production volumes without explicit approval.
- A proxy VM in the lab XCP-ng pool with VIFs on the storage networks, and its host object on the array.
- Legal review of the VMFS port.
