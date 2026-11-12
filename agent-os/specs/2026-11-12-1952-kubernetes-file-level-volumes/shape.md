# File-level Backup of Kubernetes Volumes — Shaping Notes

## Scope

Data that block snapshots cannot reach is backed up file by file:
- PVCs on NFS, hostPath and local PVs, and CSI volumes without a snapshot class;
- opt-in pod volumes without a PVC (emptyDir, pod hostPath).

Usage numbers name what is not backed up, including data written into a container's own file system. Restores work as files: browse, download, into a PVC or a share. A namespace restore puts PVC data into new PVCs through the existing files path.

## Decisions

### By the user (2026-11-12)
- **Scope:** "file level wegen nfs und so". Also "workloads die kein pvc haben aber daten schreiben … sollten gesichert werden".
- **Reading is hybrid:**
  - NFS, hostPath and local PVs through a read-only shadow PV and a mover in the work namespace, with no extra rights;
  - an optional node agent (privileged DaemonSet) for emptyDir, pod hostPath, and RWO CSI volumes without snapshots.
- **Pod volumes without a PVC are opt-in:**
  - a job switch, default off, plus the Velero pod annotation `backup.velero.io/backup-volumes`;
  - the user's idea: use storage usage numbers to find workloads that write a lot.
- **Restore of pod-volume data:** as files (browse, download, into a PVC or a share). The workloads are not changed (no init container). The user chose this after the trade-offs: emptyDir data lives only as long as its pod, and an injected init container breaks with GitOps, restarts and several replicas.
- **Usage numbers:** from the node agent (the kubelet's `/stats/summary` with `nodes/stats` only), else from an optional Prometheus address per cluster. Never `nodes/proxy`.

### In the design (2026-11-12, reviewed against the code)
- **Repository:**
  - a hash tree of directory listings plus per-file chunk streams in the existing chunk store;
  - small files inline, no packing, no atime (so unchanged subtrees deduplicate);
  - manifest version 2 only when a restore point has file trees.
- **Mover read mode:**
  - raw system calls (`openat`, `getdents64`, `statx`) with `O_NOFOLLOW`, staying on one file system;
  - incremental runs as a streaming merge with the previous tree, checked by the server;
  - a root_squash fallback with `setfsuid`/`setfsgid`.
- **Shadow PV safety:** only the volume source, node affinity, mount options and capacity are copied. Never annotations, finalizers or the reclaim policy. The copy is always `Retain` and read-only; hostPath is forced to `type: Directory`.
- **Hooks:** Velero order: pre-hook, snapshots, file reads, post-hook.
- **The node agent:**
  - a DaemonSet in its own privileged namespace, driven by exec (the server never sends it a URL or a path);
  - not privileged itself: `DAC_READ_SEARCH`, a read-only kubelet mount;
  - kubelet TLS always verified, against the cluster CA or a pinned certificate.
- **Phasing:** NFS, hostPath and local first, then the namespace restore, incremental runs, pod volumes with Prometheus, and the node agent last.

## Context

- **Visuals:** none.
- **References:** see `references.md`.
- **Product alignment:** roadmap "Phase 3: NFS volumes at file level" of the Kubernetes spec, moved forward.
