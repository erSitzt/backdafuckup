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
- **Mover image pushed from the console** (user decision 2026-11-11):
  - The build makes the mover image with the .NET SDK's container support (`PublishContainer` to an archive, no Docker) and ships it with the server, as the agent package.
  - The console pushes it to the user's registry over the OCI distribution API, with registry credentials stored like the others.
  - Pods use it by digest, so the mover always matches the server. Where the registry needs a login to pull, the product creates a pull secret in `backdafuckup-system`.
  - The agent image (agent path) goes the same way.
  - **Pull secret:** our clusters pull from the internal registry with credentials. In QA, 123 of 124 pods with its images carry the pull secrets the platform team distributes, so the mover needs one too:
    - by default the product creates its own pull secret in `backdafuckup-system` from the stored registry credentials (the work namespace's role may create secrets there);
    - alternatively the user names an existing secret there, e.g. one a reflector copies in.
- **Mover and data channel as built (task 6, 2026-11-11):**
  - **Wire format** (`MoverStream`): a header with the size, records of non-zero regions (offset, length, data; at most 16 MiB) in increasing order, heartbeats while the mover reads zeros, and an end with the total, so a cut stream is detected.
  - **Endpoints:** `/api/agent/movers/{id}/data` (PUT backup, GET restore) and `/result`, with a one-time bearer token per volume and run. The URL and the SPKI pins are those of the guest agents (Guest agents → Settings). With a certificate of the user's own CA, `Agent:ServerPins` gives the pins, because the chiseled image does not trust internal CAs.
  - **Waiting:** a channel waits up to 4 hours for its mover, because the mover's volume is a full copy of the snapshot. A watcher fails it at once when the pod cannot pull its image or fails, with the reason and the end of the pod's log.
  - **The mover:**
    - It reads exactly the snapshot's size (`BDFU_MOVER_BYTES`), because the new volume may be rounded up.
    - It skips zero blocks at 64 KiB.
    - It runs as root (block devices belong to root), but without capabilities and privilege escalation, with a read-only root file system. So the work namespace carries `pod-security.kubernetes.io/enforce: baseline`.
  - **Pull secret:** created as `backdafuckup-registry` from an optional read-only pull user (else the push user), or an existing secret, or none. The push is skipped when the registry already has the image (by digest); a backup pushes a newer image itself after a server update.
  - **Tags:** the image tag is `<version>-<digest prefix>`, so registries that forbid redeploying a tag (Nexus "Disable redeploy") accept every build.
- **Backup of namespaces as built (task 7, 2026-11-11):**
  - **The engine stays as it is:** the Kubernetes session gives a namespace to the backup engine like a VM (variant A).
    - The namespace is the "VM", its PVCs with a CSI snapshot class are the disks (key = PVC name, matched by name across runs), and its objects are the metadata blob (`k8s-resources-jsonl-v1`, one object per line, without `managedFields`).
    - The job's connection is the cluster, so jobs, schedules, retention, sessions and metrics need no new object kind.
    - The restore point records `ObjectKind` (`Vm` or `KubernetesNamespace`) and the manifest a `Kubernetes` descriptor (cluster, namespace, volumes with their data path, hooks).
  - **Objects:** every object of the built-in namespaced kinds (core, apps, batch, networking, policy, rbac, autoscaling) and of every namespaced CRD in its storage version, plus the namespace, the PVs and StorageClasses of its PVCs, and the CRDs that have objects in it. Events, endpoints and leases are left out, because the cluster makes them. A kind the account may not list is a warning.
  - **Hooks:** Velero's annotations (`pre`/`post.hook.backup.velero.io/command`, `container`, `on-error`, `timeout`).
    - The pre-hooks run before the first snapshot, the post-hooks after the last (also when a snapshot fails).
    - A failed pre-hook with `on-error: Fail` stops the namespace before any snapshot. With `Continue` the restore point is crash-consistent with a warning; with every pre-hook successful it is application-consistent.
  - **Snapshots:** taken one after another before any volume is read. Each one is deleted as soon as its volume has been read.
  - **No change tracking:** every run reads every volume in full, logged as information, not a warning. Dedup stores only changed chunks.
  - **The mover image is pushed before the first snapshot,** so a missing registry stops the run before anything is created.
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
