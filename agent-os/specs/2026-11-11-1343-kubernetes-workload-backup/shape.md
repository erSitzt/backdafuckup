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
- **Restore of PVC data:** a block PVC in `backdafuckup-system`, written by the mover, then a snapshot of it; the target PVC is created from that snapshot (see "Restores as built"; the PV rebind of spike S3 does not hold on vSphere).
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
- **Restores as built (task 8, part 1, 2026-11-11):**
  - **The engine's VM restore carries a namespace:**
    - Each volume with data is a "disk", written by a mover in restore mode into a new block PVC in `backdafuckup-system` on the original StorageClass.
    - Then the "VM" is created: the namespace's objects, with the PVCs from the written volumes.
  - **From the written volume to the target PVC, through a snapshot:**
    1. A VolumeSnapshot of the work PVC in `backdafuckup-system`.
    2. Once the target namespace exists, the snapshot is exposed there: a static VolumeSnapshotContent on the same handle (`Retain`, `sourceVolumeMode: Block`, the volume-mode-change annotation) and a VolumeSnapshot on it.
    3. The target PVC is created from it (`dataSource` VolumeSnapshot, Filesystem). On a StorageClass with WaitForFirstConsumer it gets `volume.kubernetes.io/selected-node` of the mover's node.
    4. Once it is bound: the exposed snapshot and its content, the work snapshot and the work PVC are deleted.
  - **Why not the rebind of spike S3:** on vSphere, deleting the work PV object, even with `Retain`, makes the CNS syncer unregister the volume; afterwards it can be neither attached nor deleted (seen on `rke2-test`, 2026-11-11; the CSI full sync removed the stuck PV later). The snapshot path costs one more full copy of the volume on its datastore, but uses only what the CSI driver supports.
  - **Order and cleaning:**
    - Velero's order: CRDs, namespace, StorageClasses, PVCs, Secrets, ConfigMaps, ServiceAccounts, quotas, roles, Services, custom resources, workloads, HPA, Ingress, pods.
    - Each object is restored without uid, resourceVersion, timestamps, owner references, status and managed fields. Services lose their cluster IPs and node ports, Jobs their generated selector, ServiceAccounts their token references; RoleBindings to ServiceAccounts follow a new namespace name.
    - Objects made by a controller (`ownerReferences` with `controller: true`) and what the cluster makes itself (ServiceAccount tokens, `kube-root-ca.crt`, the default ServiceAccount) are not restored.
  - **What, three ways:**
    - The whole namespace.
    - Apps and workloads (user request 2026-11-11), grouped by `app.kubernetes.io/instance`, else the Helm release, else `app.kubernetes.io/name`. A workload brings what it needs: ConfigMaps and Secrets of its volumes and environment, pull secrets, its ServiceAccount with the RoleBindings and Roles naming it, its PVCs (a StatefulSet's from its claim templates), the Services selecting its pods and the Ingresses to them, its HPA and PDBs.
    - Single PVCs with their data, also under new names next to the originals.
  - **Where:** the original namespace or another one, created when missing.
  - **Existing objects:** left as they are, or replaced (updated) on request. A PVC with data that exists in the target stops the restore before anything is written: delete it first, restore elsewhere, or under a new name. An objects-only PVC comes back empty on its StorageClass, or bound to its static PV again (e.g. an NFS export) when that PV is gone or available.
  - **A restore that does not complete takes back what it created:** a namespace it created with everything in it, else each object. Work PVCs, snapshots and their contents are in the ledger, so a crash still removes them.
- **Restores as built (task 8, part 2, 2026-11-11):**
  - **Replacing a PVC in place** (typed confirmation):
    - The controllers of the pods that mount it (Deployment, StatefulSet, ReplicaSet, ReplicationController) are scaled to 0, each with its replicas in the ledger (`k8s.ScaledWorkload`), so a crash still scales them back.
    - When no pod uses the PVC any more, it is deleted and created again from the restored volume's snapshot; then the workloads are scaled back.
    - A pod of a DaemonSet or Job, or one without a controller, stops the swap before anything changes.
    - Verified on `rke2-test`: the Redis data came back, about 9 s without the pod.
  - **Object explorer:** the restore point's objects compared with the live namespace (same, changed, deleted since, new since; the deployment revision annotation and a PVC's `volumeName` do not count as changes). Each object can be shown as stored and as it is now (Secrets with their keys, never their values). Picked objects come back exactly as stored, without what they need, existing ones replaced on request.
  - **Files:**
    - The file browser reads the PVC's image (ext4 or XFS on the whole device, no partition table); the ZIP download works as for VMs.
    - **Back into the live PVC:** a mover pod in "files" mode in the PVC's own namespace mounts the PVC as a file system, on the node of the pods that use it (`nodeName`, tolerating that node's taints; ReadWriteMany anywhere), while the workload keeps running. A ReadWriteOncePod PVC in use is refused.
    - **Stream** (`MoverFileWriter`, server to mover over a files channel with back-pressure): folders; files in chunks of at most 1 MiB, each with an end or an abort marker (a chunk the repository cannot read discards just that file); owner, mode and times; links; an end.
    - **The mover** follows the SSH restore script: each file under a hidden temporary name, flushed, owner, mode and times set, then renamed over the old one (or to a free RESTORED- name); links after the files, folder times last; nothing written through a symbolic link or outside the volume. It reports what failed, was renamed or lacks owner, mode or times, per item.
    - **Privileges:** root with CHOWN, DAC_OVERRIDE, FOWNER and FSETID (Pod Security "baseline"). A namespace that enforces "restricted" is refused before anything is created; the ZIP download remains.
    - The token secret, a copy of the image pull secret and the pod are in the ledger.
- **Docs as built (task 9, 2026-11-11):** the administrator guide's chapter *Kubernetes namespaces* (requirements and ports, the setup manifest and its roles, the mover image, jobs and hooks, consistency, Secrets, the three ways to restore, cleaning up, limits, troubleshooting); the lab runbook's checks K1–K11 with the results of the test cluster; the mover image's base layers in `THIRD-PARTY-NOTICES.md`. Open: the lab run through the agent path (K11).
- **Agent path as built (2026-11-11):**
  - **The agent is the mover image in agent mode** (`BDFU_MOVER_MODE=agent`), a Deployment `backdafuckup-agent` in `backdafuckup-system`. It reuses the image, its registry push and pull secret, and runs as a non-root user without capabilities.
  - **One WebSocket per TCP connection** instead of multiplexed streams:
    - The agent keeps a control WebSocket open (`/api/agent/clusters/<id>/control`). For each connection the server asks for (`open`), it opens a WebSocket of its own (`…/tunnel/<stream>`) and joins it to a TCP connection to `KUBERNETES_SERVICE_HOST`.
    - Flow control is TCP's; a slow watch cannot stall the others.
  - **Credentials:**
    - The agent sends its ServiceAccount token (again when the kubelet renews it) and the cluster CA. The server keeps both in memory.
    - The agent authenticates with a per-cluster connect token from the setup manifest, stored like a credential.
  - **The server's side:**
    - A loopback port per cluster: the unchanged Kubernetes client (HTTP, watch, exec) connects there.
    - TLS is checked by our own callback for HTTP and for the WebSockets of exec (`CreateWebSocketBuilder`): the chain to the cluster CA and the name `kubernetes.default.svc`, since the URL's host is the loopback address.
  - **Connections:** `access` = `Direct` or `Agent`. An agent cluster learns its platform at the agent's first contact (a cluster already connected is refused). A direct cluster can change to its agent, keeping jobs and restore points.
  - Not yet: updating the agent's image from the server (apply the manifest again after a server update).
- **Agent path as a TCP tunnel (as planned):** exec (hooks) runs over the client's own WebSocket, which bypasses HTTP message handlers, so relaying HTTP requests is not enough.
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
- **Restoring Filesystem PVCs from block images:** resolved through a snapshot of the written volume (the rebind of spike S3 fails on vSphere CNS). It needs a VolumeSnapshotClass for the driver and costs one more copy.
- **Files into a live PVC:** written while the workload runs, as with a restore into a running VM; a file the application holds open is replaced underneath it. Nodes with SELinux and per-pod MCS labels would deny the second pod access (not the case on our RKE2 nodes). Not yet verified on the test cluster.
- **GitOps:** restoring objects that Fleet manages can race with Fleet. The restore names Fleet-/Helm-managed objects in its preview.

## Context
- **Visuals:** `visuals/mockups/`. The eight artboards of both variants as Design Component sources (`A*`/`Main` = variant A, `B*` = variant B) and the canvas layout; see `visuals/README.md`.
- **References:** see `references.md`.
- **Product alignment:**
  - The mission's "hypervisor-agnostic design" and "public, documented APIs only" apply.
  - Kubernetes is new on the roadmap. It is not a hypervisor, but it fits the same core of connection, inventory, job, restore point and restore.

## Standards Applied
See `standards.md`.
