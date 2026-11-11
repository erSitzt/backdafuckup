# Kubernetes Workload Backup — Shaping Notes

## Scope

backdafuckup backs up Kubernetes workloads at the Kubernetes level: the API objects of a namespace and the data of its persistent volumes, as Velero and Kasten do. The restore works at the same level.

- **Why now:** the RKE2 agent nodes run as VMs on vSphere. Their VM snapshots (Veeam) disturbed the vSphere CSI (CNS) volumes attached to them, so the nodes will be excluded from VM backups. The workloads still need a backup.
- **Phase 1:**
  - Back up namespaces: objects, the data of PVCs on a CSI driver with snapshots, and hooks.
  - Restore in the same cluster: a namespace (all or selected objects), a single PVC (as new or replacing), and files.
  - Both ways to the cluster API: the server directly, and an in-cluster agent that dials out.
- **Phase 2:**
  - Restore into another cluster, with namespace and StorageClass mapping.
  - Whole-cluster DR.
- **Later step: Kubernetes clusters on XCP-ng** (user request 2026-11-11). Their PVs may be backed up through XAPI (VDI snapshots with CBT) instead of the CSI driver. The CSI drivers for XCP-ng are to be researched first.
- **Phase 3:**
  - NFS volumes at file level.
  - Changed block tracking through CSI SnapshotMetadata.
  - VolumeGroupSnapshots.
  - Hook presets for common applications.

## Decisions

### Confirmed by the user (2026-11-11)
- **Snapshots:** CSI VolumeSnapshots may be used. The problem was VM snapshots of the nodes, not CSI snapshots.
- **Workloads:** all kinds. Databases (MongoDB, Redis, RabbitMQ, Qdrant; no operators), app data, monitoring (Prometheus, MinIO), and every namespace generically.
- **Restore cases:** all of them (namespace, PVC and files, another cluster, DR), phased as above.
- **API access:** both paths in Phase 1, one control:
  - the server directly to the API URL;
  - an in-cluster agent that dials out and relays the API requests, as Rancher's cattle-cluster-agent does.
- **NFS volumes:** their data comes later (Phase 3). Phase 1 backs up their objects only, and says so.
- **Objects matter as much as data:** the workloads come partly from Git (Fleet/Helm) and are partly made by hand.
- **Testing:**
  - On the test cluster `rke2-test`, where the user installs vSphere CSI ≥ 3.5 with a VolumeSnapshotClass, plus the CSI hostpath driver.
  - The QA cluster `rke2-qa` was only read.
  - No writes to the QA or production clusters.
- **UI:** variant **A**, like the VM pages (chosen 2026-11-11 from the mockups in `visuals/mockups/`):
  - Kubernetes Clusters under Backup Infrastructure, next to the pools;
  - namespaces in jobs and under Backups & Restore, like VMs;
  - a restore wizard with object comparison.

  Variant **B**, an applications view per cluster with policies, stays an option for parts later: policies that select namespaces by wildcard or label (also new ones), application cards with protection status, the 14-day restore point strip, and per-volume choices in the restore.

### Technical
- **Data path for block PVCs:**
  1. A CSI VolumeSnapshot.
  2. Exposed in `backdafuckup-system` as a static VolumeSnapshotContent/VolumeSnapshot with `snapshot.storage.kubernetes.io/allow-volume-mode-change`.
  3. A PVC with `volumeMode: Block` from it.
  4. A mover pod pushes the device to the server, where it lands in the existing repository as a disk image.

  Reasons:
  - It reuses the chunk repository, dedup and zero-skipping.
  - `FileLevel` reads ext4/XFS from the images, so single files come back without a file store.
  - It works for any file system inside the PVC.
- **No CBT on vanilla vSphere CSI:** every run reads the whole device, and dedup stores only what changed. The volumes are small (5–10 GiB in QA).
- **Short-lived snapshots:** snapshot, copy, delete within minutes. The limit is 3 per volume, FCD snapshots cost I/O, and a volume with a snapshot can be neither expanded nor deleted.
- **Hooks:** the Velero annotation names `pre.hook.backup.velero.io/*` and `post.hook.backup.velero.io/*`, so annotations already on the workloads keep working.
- **Objects:**
  - API discovery with preferred versions; every namespaced object plus the cluster-scoped ones it uses (PVs, StorageClasses, CRDs).
  - One blob `k8s-resources` per restore point.
  - Restore order, metadata reset and existing-object policy follow Velero's documented behaviour; no code is taken over.
- **Restore of PVC data:** a block PVC in `backdafuckup-system`, written by the mover, then bound as a Filesystem PV to the target PVC (spike S3).
- **Model:**
  - Provider kind `Kubernetes`; a connection is a cluster.
  - Job objects get a kind (VM or namespace) and options.
  - Restore points get an object kind and a namespace descriptor; `Vm` becomes optional.
  - PVCs are the restore point's disks, keyed by PVC name.
- **Library:** the official C# client `KubernetesClient` 18.0.13 (Kubernetes 1.34 models, within the client's supported skew to our 1.32 clusters).
  - Apache-2.0; its dependencies Fractions and YamlDotNet are MIT.
  - The API server certificate is pinned through `FirstMessageHandlerSetup`.
- **Agent path as a TCP tunnel:** exec (hooks) runs over the client's own WebSocket, which bypasses HTTP message handlers, so relaying HTTP requests is not enough.
  - The agent keeps a WebSocket to the server and carries TCP connections to the API server, as Rancher's remotedialer does.
  - The server gives each agent cluster a local port, so the same client code (HTTP, watch, exec, TLS and pinning) works unchanged on both paths.

### Facts from the QA cluster (`rke2-qa`, read-only, 2026-11-11)
- **Cluster:** RKE2 1.32.13, 3 servers, 8 agents.
- **vSphere CSI v3.3.1:**
  - csi-snapshotter v7.0.2 and RKE2's snapshot-controller run; the snapshot and group snapshot CRDs are installed; there is no VolumeSnapshotClass.
  - 23 block PVs (RWO, Filesystem, 5–10 GiB).
- **NFS:** 40 PVs (static, and nfs-subdir-external-provisioner) on external NFS servers. An SMB CSI driver is installed but unused.

### Risks
- **Snapshot limit and I/O:** a failed run must not leave snapshots behind (ledger, tags, cleanup at startup). Parallel runs on one volume must be excluded.
- **Secrets in the repository:** the resource blob holds the namespace's Secrets, protected like disk images (repository permissions). Encryption at rest is a separate roadmap item, and the admin guide says so.
- **vSphere CSI v3.3.1 on Kubernetes 1.32:** outside the supported range and EOL. RKE2 ≥ 1.32.9 ships v3.5.0; recommend the upgrade.
- **Across volumes:** there are no group snapshots on vSphere, so the PVCs of an application are captured one after another. Hooks narrow the gap; the restore point says so.
- **Restoring Filesystem PVCs from block images** needs the PV rebind (spike S3). If it fails, the fallback is a filesystem-level write through a mover that mounts the new PVC.
- **GitOps:** restoring objects that Fleet manages can race with Fleet. The restore names Fleet-/Helm-managed objects in its preview.

## Context
- **Visuals:** `visuals/mockups/`. The eight artboards of both variants as Design Component sources (`A*`/`Main` = variant A, `B*` = variant B) and the canvas layout; see `visuals/README.md`.
- **References:** see `references.md`.
- **Product alignment:**
  - The mission's "hypervisor-agnostic design" and "public, documented APIs only" apply.
  - Kubernetes is new on the roadmap. It is not a hypervisor, but it fits the same core of connection, inventory, job, restore point and restore.

## Standards Applied
See `standards.md`.
