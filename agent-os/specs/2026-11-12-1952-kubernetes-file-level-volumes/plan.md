# Plan: file-level backup of Kubernetes volumes without snapshots

Follow-up to `../2026-11-11-1343-kubernetes-workload-backup` (its "Phase 3: NFS volumes at file level"). Built after `../2026-11-12-1952-kubernetes-platform-profiles`, whose profiles give the node agent its kubelet and runtime paths; the groundwork tasks 1–2 (spec docs, RBAC hardening) are in that spec.

## Context


**What prompted this.** The phase 2 lab runs showed two gaps.
1. **Hard-coded platform knowledge.** The code knows Rancher, RKE2, Fleet, vSphere CSI and the CNIs by name:
   - `KubernetesClusterScope.DefaultExcludes`;
   - `KubernetesClusterResources` (`SkippedCrdGroups`, `ManagedPrefixes`, `IsManaged`);
   - `KubernetesObjects.IsRancherMade` / `IsRancherField`;
   - log and UI texts.

   On OpenShift, GKE, EKS, AKS, k3s, MicroK8s or Tanzu the same jobs would back up platform namespaces and objects and restore them into other clusters. K13 already needed `vmware-system-csi` added by hand.
2. **Data that is not backed up.** Volumes without CSI snapshots (NFS, local-path, hostPath PVs, CSI without a snapshot class) are only backed up as objects ("file-level backup comes later"). Pod volumes without a PVC (emptyDir, hostPath) and data written into a container's own file system are not backed up, and nothing warns about them.

**User decisions (2026-11-12):**
- Platform rules become data-driven profiles, detected per cluster.
- Reading files is hybrid:
  - NFS, hostPath and local PVs through a read-only *shadow PV* and a mover in `backdafuckup-system`, with no extra rights;
  - an **optional node agent** (privileged DaemonSet) for emptyDir, pod hostPath, and RWO CSI volumes without snapshots.
- Pod volumes without a PVC are **opt-in**: a per-job switch, default off, plus the pod annotation `backup.velero.io/backup-volumes` (excludes: `…-excludes`). Without the opt-in, backups name what they did not back up, and its size.
- Pod-volume data comes back **as files**: browse, download, into a PVC or a share. A namespace restore re-creates pods with empty emptyDirs and names the data.
- Usage numbers (emptyDir per volume, writable layer per container) come from the node agent (kubelet `/stats/summary`, `nodes/stats` only), else from an optional Prometheus address per cluster. Never `nodes/proxy`.

**Found while exploring.** The backup ClusterRole grants `get` on `*/*`, which also matches `nodes/proxy`, `pods/proxy` and `services/proxy`. `GET nodes/proxy` is a known way to exec into pods through the kubelet. Task 2 hardens this first.

## File-level backup


**Repository format (manifest version 2 only when a restore point has file trees; older servers refuse it before GC deletes anything)**
- **File content.** Each file's content is its own chunk stream: pieces of the chunk size, starting at a chunk boundary, with zero pieces not stored. They deduplicate across runs and clusters.
  - Files of 0 bytes have no content.
  - Files up to 512 B are stored inline in their directory listing.
  - There is no packing, because write-once packs would waste space forever.
- **Directory listings** (`BDTL` v1). One chunk stream per directory, with entries sorted by raw name bytes. Each entry holds:
  - type, mode, uid and gid, `mtime_ns` and `ctime_ns`, inode and nlink, size;
  - flags: inline, changed-while-read, racy, unreadable, sparse-seen;
  - content refs, or a child listing ref with subtree totals.

  There is no atime, so unchanged subtrees produce identical listings and deduplicate.
- **Manifest.** The root sits in `tree-<key>.idx` (the existing `ChunkIndexFormat`). `RestorePointManifest.FileTrees` holds key, name, kind (pvc or pod-volume), reader (shadow-pv or node-agent), root meta, stats and the base restore point.
- **GC and verify** walk each tree depth-first. The "subtree already walked" set is separate from the chunk mark set, so content that equals a listing cannot hide one. Restore points never reference each other; the base is pinned during a run.

**Mover read protocol**
- `GET /api/agent/movers/{id}/previous` sends the base tree in canonical order (`BDFUPRV1`).
- `PUT /data` sends `BDFUTRE1` records:
  - `D`/`U` directory enter and leave;
  - `F` + `C` (one chunk-size piece) / `0` zero piece / `Z` or `X`;
  - `S` unchanged (reuse the base content);
  - `H` hard link, `L` symlink, `P` special file;
  - `I` issue, `B` heartbeat with counters and the current path;
  - `E` end with totals.
- **Incremental runs** are a streaming merge of the walk and `/previous`. A file is unchanged when size, `mtime_ns`, `ctime_ns` and inode are all equal and it was not racy. The server checks every `S` against its own cursor. ActiveFull reads everything.
- **The walker** uses `openat`/`getdents64`/`statx` with `O_NOFOLLOW`:
  - it keeps raw names and stays on one file system;
  - it detects files that changed while read;
  - it retries `EACCES` on the walker thread with `setfsuid`/`setfsgid` to the owner (for NFS root_squash).
- **Backpressure:** a pipe on the mover side; on the server, the bounded hashing and storage queue of `DiskBackupPipeline`. `MinRequestBodyDataRate` and `MinResponseDataRate` are off for these requests. A stall timeout names the path it stalled on.

**Shadow PV (NFS, hostPath, local; no extra rights)**
- **The shadow PV** copies only `spec.nfs`, `spec.hostPath` or `spec.local`, plus `nodeAffinity`, `mountOptions` and capacity. It is forced to:
  - ReadOnlyMany, `Retain`, `storageClassName: ""`;
  - a pre-bound `claimRef` to a PVC in `backdafuckup-system`;
  - `nfs.readOnly: true`;
  - for hostPath, `type: Directory`.
- **It never copies** annotations (`provisioned-by`!), finalizers or the reclaim policy.
- **Node pinning:**
  - hostPath without `nodeAffinity` is pinned to the consuming pod's node; without one, the volume stays objects-only with the reason;
  - NFS prefers the consumer's node.
- **The read mover pod** runs as root with the baseline-allowed `DAC_OVERRIDE`, `SETUID` and `SETGID`, a read-only mount and 512Mi.
- **Ledger:**
  - a new PersistentVolume kind;
  - deletion order pod → PVC → PV, and the PV only when it is labelled and `Retain`;
  - `FindTaggedArtifactsAsync` scans for stale shadow objects as a backstop.
- **Pending pods** report their `FailedMount` event; the connect timeout is 15 min.

**Hooks and consistency**
- When a namespace has file trees, post-hooks run after those trees are read (Velero order: pre-hook → snapshots → file reads → post-hook), exactly once on every path.
- The consistency text names the volumes read file by file while in use and the number of files changed during the read.

**Node agent (optional, last)**
- **Placement:** a DaemonSet in its own namespace `backdafuckup-node` with Pod Security privileged.
  - It runs as uid 0, not privileged: drop ALL, add `DAC_READ_SEARCH`, read-only root FS.
  - The kubelet root from the platform profile is mounted read-only with HostToContainer propagation. There is no host-root mount.
  - Self-check: the agent's own pod directory must exist.
- **Dispatch:**
  - The server execs `node-request` in the agent pod on the right node and passes JSON on stdin (operation, channel id, one-time token, pod UID, plugin, volume). This needs a stdin overload of `IPodExecutor`.
  - The daemon builds the paths from fixed patterns and uses its own server URL and pins.
  - It reads with the same walker and protocol, at most 2 reads per node.
- **Usage:** kubelet `/stats/summary` with its own ServiceAccount (`nodes/stats` get only). TLS is verified:
  1. against the cluster CA;
  2. else a pinned SPKI of the host's kubelet certificate (path from the profile);
  3. else no kubelet numbers, measuring the emptyDirs instead.
- **Updates:** the image is the mover image; the server patches the DaemonSet like the cluster agent.

**Tasks**

10. **Tree format.**
    - `FileTree` writer and reader, `FileTreeWalker`, manifest v2, the index methods on the repository writer and reader, GC and verify.
    - Storage tests: round trip; GC keeps and frees tree chunks; content equal to a listing; v1 code refuses v2.
11. **Mover read mode.**
    - `BDFUTRE1` in `Core/DataMover/MoverTree.cs`, and `TreeReader` with native calls.
    - Tests on Linux temp trees: odd names, hard links, symlinks, FIFOs, sparse files, a file that changes while read.
12. **Server receive.** `FileTreeBuilder`, `TreeBackupChannel`, and `IMoverChannels.OpenTreeBackupAsync`. Tests with the in-process mover.
13. **Engine.**
    - `FileTreeSource`, `IVmBackupSession.FileTrees` / `OpenFileTreeAsync` / `FileTreesReadAsync`, and `BackupFileTreesAsync` next to the disks.
    - `ApplyManifest` bytes, consistency text, metrics.
    - DB migration: `RestorePointDisk.Kind`, file and issue counts.
14. **Kubernetes backup.**
    - `DataPathOf` gains "files" with a reader and a reason.
    - `ShadowVolume`, `VolumeFileExport`, `MoverPods.BuildRead`, the ledger PV kind, deferred post-hooks.
    - The fake API checks every forced or never-copied shadow-PV field and the cleanup order.
15. **Read side.**
    - `IGuestVolumeSet` and `RestorePointVolumes` replace the hard-coded `GuestImage`/`OpenDisksAsync` sites (`FileRestoreMounts`, `BrowseMount`, `FileRestoreDownloads`, `FileRestoreSessionRunner`, `VolumeFileSender`, `FileRestoreEndpoints`, `AgentTransfers`).
    - `FileTreeFileSystem : IGuestFileSystem`.
    - The ext/XFS gates become a check that the volume is a Unix file system.
    - Existing file-restore tests stay green.
16. **Console.**
    - "files (read-only copy of the PV)" chips with reasons.
    - The restore point's volumes with file and issue counts.
    - File browser titles.
    - `NamespaceRestore.razor` counts "files" as data.
17. **Namespace restore of trees.**
    - Trees always take the Files path. The PVC size is the larger of the request and tree bytes × 1.2 + 1 GiB.
    - Same cluster with a static PV: bind it again, optionally writing the files into it through `PvcFileImport` after a typed confirmation, or use a mapped class.
    - The `RestoreSessionRunner` image-open fix and a loop over the trees.
18. **Incremental and robust.** `/previous`, `S`/`H`, racy flags, the server's `S` check, the root_squash fallback, `SEEK_HOLE`, stall detection, progress estimates.
19. **Pod volumes and usage without the node agent.**
    - The job switch and the Velero annotations.
    - Descriptor `PodVolumes` and `ContainerWrites`; `InventoryKind.PodVolume`.
    - `IUsageSource` from Prometheus: a URL per cluster with a credential and a CA or pin, and configurable queries (ephemeral-storage-exporter, cAdvisor, `kubelet_volume_stats`).
    - Warnings about unsaved data, in the console and in the backup log.
20. **Node agent.**
    - `KubernetesSetup.NodeAgent(profile)`, an opt-in in the manifest endpoint and the console.
    - The daemon, the `node-request` CLI, the unix socket, the stdin executor, `NodeAgentClient`.
    - `DataPathOf` for emptyDir, RWO CSI mounts, and `IUsageSource` from the kubelet.
    - Image and environment updates.
21. **Docs and lab.**
    - Admin guide: file-level backup, root_squash, consistency, the node agent's rights, Prometheus.
    - THIRD-PARTY-NOTICES if anything new ships.
    - Runbook rows K17 onwards (below).

**Later, not in this plan:** pod hostPath volumes from an allow-list; xattrs and ACLs; restores that keep hard links; content-defined chunking.

## Verification


- **Each task:** unit and integration tests as listed, and the full suite green (`~/.dotnet/dotnet test`). CI green on Ubuntu and Windows, integration on Linux.
- **Live, on test clusters only.** Deploy only when asked, after checking sessions, not at :00/:15/:30/:45.
  - **K17 (NFS):**
    - the Prometheus NFS PVC and an nfs-subdir test namespace on `rke2-test` are backed up as files: browse, ZIP, a folder into a PVC and onto a share;
    - `kubectl get pv -l backdafuckup.io/shadow-of` is empty afterwards, the ledger is clean, the hooks ran in Velero order;
    - the export's PV and data are untouched (its reclaim policy and annotations unchanged).
  - **K18 (local-path):** kind, with the mover pinned to the node and `type: Directory`.
  - **K19 (namespace restore):** an NFS namespace into `rke2-target` (nfs-client) and into kind (local-path); a same-cluster restore after deleting the namespace.
  - **K20 (incremental):** a second run of the Prometheus PVC reads only the changed files (bytes read and unchanged count logged). A root_squash export reports its unreadable count. ActiveFull reads everything.
  - **K21 (pod volumes, no agent):** with Prometheus configured, emptyDirs are listed with sizes, writable layers are warned about, and annotated volumes are named "not backed up: no node agent".
  - **K22 (node agent):**
    - kind (pinned kubelet certificate path; cgroup v1 host): a disk emptyDir and a tmpfs emptyDir via the annotation, restored into a PVC;
    - `rke2-test` (kubelet certificate signed by the cluster CA): usage numbers, and an RWO CSI volume without snapshots that a pod has mounted.

    On `rke2-test` the user applies the manifest with the node agent from the console; on kind the developer applies it. It is installed on `rke2-target` only after asking.

- **Safety:**
  - no writes to QA or production;
  - in `rke2-target`, only new namespaces and `backdafuckup-system`;
  - nothing deleted in XCP-ng;
  - shadow PVs never carry a provisioner annotation or a policy other than `Retain`.
