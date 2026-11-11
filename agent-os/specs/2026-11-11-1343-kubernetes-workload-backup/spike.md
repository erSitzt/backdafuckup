# Spike: Kubernetes PV Data Path on vSphere CSI

**Test cluster `rke2-test`:**
- RKE2 1.32.13, 3 servers, 4 agents (VMs on vSphere).
- **vSphere CSI v3.6.3**, the newest release for Kubernetes 1.32, installed on 2026-11-11.
  - Rolled out as a Fleet bundle in the cluster's GitOps repository: the QA cluster's bundle at the new version, with its own `cluster-id`.
  - StorageClass `vsphere-csi-block-rwo` (not the default class here), VolumeSnapshotClass `vsphere-csi-snapclass` (`deletionPolicy: Delete`).
  - RKE2's snapshot-controller and the snapshot CRDs were already there.
- **Rollout:** about 1.5 minutes, 3 controller and 7 node pods. A few sidecars restarted once or twice at the first start, before the driver socket existed. The controller found every node VM in vCenter.

The tests ran by hand with `kubectl` and busybox pods in the namespaces `bdfu-spike` (the "app") and `backdafuckup-system` (the product's work namespace).

| # | Question | Result |
|---|---|---|
| S1 | How long does a CSI snapshot take, and its deletion? | Ready in **4.9 s**, deleted in **2.0 s** (1 GiB volume, attached, 200 MB written) |
| S2 | Can a snapshot be exposed in the work namespace as a block PVC and read as a raw device? | **Yes.** Exposed in 0.6 s, block PVC bound in 3.1 s, read by an unprivileged pod. A block volume must not be read-only. |
| S3 | Can a written block volume be bound as a Filesystem PV to a PVC in the app namespace? | **Yes.** The data and its SHA-256 matched. |
| S4 | Throughput of a mover pod to the backup server | open (needs the mover) |
| S5 | Agent tunnel for API requests incl. watch and exec | open (needs the agent) |

## S1: snapshot (2026-11-11 14:31)

**Setup:**
- The PVC `spike-data` (1 GiB, `vsphere-csi-block-rwo`) was bound after 3 s, and the pod using it was ready after 15 s. Attaching works, so the node VMs have `disk.EnableUUID`.
- The volume is ext4 and holds 200 MB of random data, a marker file and its SHA-256.

**Snapshot:** a VolumeSnapshot `spike-snap-1` with class `vsphere-csi-snapclass`, taken while the pod ran (after a `sync`).
- `readyToUse` after **4.9 s**, `restoreSize` 1Gi.
- The handle has the form `<FCD id>+<FCD snapshot id>`; `sourceVolumeMode` Filesystem.

**Deletion:** deleting the VolumeSnapshot removed its VolumeSnapshotContent and the FCD snapshot in **2.0 s**.

## S2: expose and read as a block device

**Expose:**
1. A static VolumeSnapshotContent `bdfu-spike-exposed-1`:
   - `deletionPolicy: Retain`, so the product's copy never deletes the FCD snapshot;
   - the same `snapshotHandle`, `sourceVolumeMode: Filesystem`;
   - annotation `snapshot.storage.kubernetes.io/allow-volume-mode-change: "true"`;
   - `volumeSnapshotRef` to `backdafuckup-system/spike-snap-1-exposed`.
2. The VolumeSnapshot `spike-snap-1-exposed` on it was ready after **0.6 s**.
3. A PVC `spike-block` with `volumeMode: Block` and `dataSource` that VolumeSnapshot was bound after **3.1 s**, as a new FCD: vSphere copies the snapshot.

**Read:**
- An unprivileged busybox pod got the device through `volumeDevices`.
- Size 1073741824; the ext4 magic `53 ef` at offset 1080; the marker string on the device.
- Reading the whole device took 0.6 s (1.6 GB/s). Most of it is unallocated and comes back as zeros, so this is not a throughput figure for data.

**Found:** vSphere CSI refuses a block volume mounted read-only: `MapVolume.MapPodDevice failed … read only not supported for Block Volume`. The pod hung in ContainerCreating until the timeout. The mover must mount the clone read-write; the clone is thrown away anyway.

## S3: restore by rebinding (block volume to a Filesystem PVC)

1. An empty PVC `spike-restore-block` (`volumeMode: Block`) in `backdafuckup-system`.
2. A mover pod copied the S2 clone onto it with `dd` (662 MB/s), standing in for the product writing an image from the repository.
3. Its PV was patched to `persistentVolumeReclaimPolicy: Retain`. The PVC was deleted (PV Released), then the PV object (the FCD stays).
4. A static PV `bdfu-spike-restored` was created on the same volume:
   - `csi.volumeHandle` = the FCD id, `fsType: ext4`, `volumeAttributes: {type: "vSphere CNS Block Volume"}`;
   - `volumeMode: Filesystem`, reclaim Delete;
   - `claimRef` to `bdfu-spike/spike-restored`.
5. The PVC `spike-restored` bound to it. A pod on another node mounted it: the marker matched the original, and `sha256sum -c` of the 200 MB file passed.

So a restore can write any image through a block PVC and hand the volume to the app as a Filesystem PVC. The writer ran on one agent and the mover on another, so attach and detach across nodes worked.

## Cleanup order

The order the product must keep:
1. Delete the clone PVC (its PV is Delete, so the FCD goes).
2. Delete the exposed VolumeSnapshot and VolumeSnapshotContent. Both are Retain, so only the objects go.
3. Delete the original VolumeSnapshot, which deletes the FCD snapshot.

Afterwards nothing was left: no VolumeSnapshot or VolumeSnapshotContent, no PV, PVC or pod of the spike, no VolumeAttachment. Only the empty namespaces remain.

## Open
- **Larger volumes:** the copy that a PVC from a snapshot costs on vSphere (S2) and the snapshot time under write load. 1 GiB says little.
- **CNS:** the vCenter view of FCD snapshots, and the CNS metadata of a rebound volume (does the syncer attach the new PV and PVC names?).
- **S4 and S5:** once the mover and the agent exist.
