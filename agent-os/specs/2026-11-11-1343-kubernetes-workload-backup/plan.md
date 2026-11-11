# Plan: Kubernetes workload backup (namespaces, PVC data, files)

Spec folder: `agent-os/specs/2026-11-11-1343-kubernetes-workload-backup/`

## Context

**Why.** The agent nodes of the RKE2 clusters run as VMs on vSphere. They will no longer be backed up as VMs: Veeam's VM snapshots of the nodes disturbed the vSphere CSI (CNS) volumes attached to them. Workloads with persistent volumes are to be backed up by backdafuckup at the Kubernetes level instead, as Velero or Kasten do: the namespace's objects plus the data of its PVCs. The restore is at the same level.

**Facts from the QA cluster (`rke2-qa`)** (inspected read-only, 2026-11-11):
- **Cluster:** RKE2 1.32.13, 3 servers, 8 agents.
- **vSphere CSI v3.3.1:**
  - The csi-snapshotter sidecar and RKE2's snapshot-controller run, but there is no VolumeSnapshotClass yet.
  - It holds 23 block PVs (RWO, 5–10 GiB): MinIO ×7, Redis ×8, RabbitMQ ×3, Qdrant, MongoDB, Prometheus.
  - 40 PVs are NFS (static, and nfs-subdir), on external NFS servers.
- **No database operators.**
- **Test cluster (`rke2-test`):** RKE2 1.32.13 too, 7 nodes; no CSI driver yet.

**Research** (sources in `references.md` when saved):
- **vSphere CSI snapshots (vanilla):**
  - They are FCD snapshots and only crash-consistent; there is no quiescing.
  - The default limit is 3 per volume, and VMware advises 2–3 on VMFS/NFS. They cost I/O on VMFS/vSAN OSA.
  - While a snapshot exists, its volume can be neither expanded nor deleted.
  - A volume restored from a snapshot is a full copy on the same datastore, at the same size.
  - Vanilla clusters get no VolumeGroupSnapshot and no CBT (SnapshotMetadata), even in v3.8; that is Supervisor/VKS only.
  - v3.3.1 is EOL and not supported on Kubernetes 1.32; RKE2 ≥ 1.32.9 ships v3.5.0.
  - So snapshots must live only minutes: snapshot, copy, delete.
- **Velero (Apache-2.0) as a reference for the flow.** No code is taken over.
  - Snapshot data movement: snapshot, then a PVC from it in Velero's namespace, then a mover pod.
  - The block mover (1.19) reads the volume as a block device; it needs the annotation `snapshot.storage.kubernetes.io/allow-volume-mode-change` on the VolumeSnapshotContent.
  - The restore order of resources, metadata reset, status removal, existing-resource policy and namespace/StorageClass mapping.
  - Hooks are pod annotations, `pre.hook.backup.velero.io/*` and `post.hook.backup.velero.io/*`.
- **backdafuckup, reusable as it stands:**
  - the repository: fixed chunks, dedup, zero chunks free;
  - `DiskBackupPipeline` with `ISequentialDiskReader`;
  - the push-receiver pattern of `SqlBackupReceiver`;
  - the artifact ledger;
  - `FileLevel`, which reads ext4/XFS/LVM from disk images, so single files of a PVC image can be browsed;
  - the explorer pattern of the SQL and AD pages;
  - the agent protocol (`Agent.Protocol`/`Agent.Core`, netstandard2.0, dial-out long poll).

  VM-shaped, to be generalized: `IHypervisorSession`/`RestorePointManifest.Vm`, `BackupJobObject`, the catalog entities, the `HypervisorKind` and `ConnectionManager` guards (Xen only), the job editor, the restore wizard and `BackupVms`.

**User decisions (2026-11-11):**
- **Snapshots:** CSI VolumeSnapshots may be used; the problem was VM snapshots of the nodes.
- **Workloads:** all kinds (databases, app data, monitoring, everything generic).
- **Restore cases:** namespace, single PVC and files, another cluster, whole-cluster DR. They are phased.
- **API access:** both paths in Phase 1: the server directly to the API, and an in-cluster agent that dials out and relays the API requests.
- **Phase 1:** backup of namespaces (objects and CSI data, hooks), and restore of namespace, single PVC and files in the same cluster.
- **Phase 2:** another cluster and DR.
- **Phase 3:** NFS data, CBT, group snapshots.
- **Object restore** matters as much as data: the workloads come partly from Git (Fleet/Helm) and are partly manual.
- **Test cluster:** the user installs vSphere CSI ≥ 3.5 with a VolumeSnapshotClass, plus the CSI hostpath driver. No writes to the QA or production clusters.
- **UI:** first an artifact with mockups of both variants; the user then picks one.

## Design

### One control, two ways to the API
- **New provider kind `Kubernetes`.** A connection is a cluster. All orchestration uses the official C# client `KubernetesClient`; licence and dependencies are checked first and listed in `THIRD-PARTY-NOTICES.md`.
- **Direct path:**
  - API URL plus a ServiceAccount token, stored encrypted like credentials.
  - The API server certificate is pinned by trust on first use, as for pools.
- **Agent path:**
  - A Deployment in `backdafuckup-system` dials out to the server: enrollment secret, agent key, server pin, as for the guest agent.
  - It keeps a WebSocket tunnel open, through which the server's `KubernetesClient` sends its HTTP requests. The agent forwards them to the in-cluster API with its own ServiceAccount, including watch and exec streams, as Rancher's cattle-cluster-agent does.
  - The agent is a Linux .NET container. It reuses `Agent.Protocol` where that fits; it updates by its image tag, not by self-update.
- **Cluster setup from the console:**
  - Generates the namespace, the ServiceAccount, separate ClusterRoles for backup (read everything incl. Secrets; VolumeSnapshots/Contents; PVCs, pods and PVs in `backdafuckup-system`; `pods/exec` for hooks) and for restore (create namespaces and namespaced objects), and the agent Deployment.
  - Output as YAML or a Fleet bundle for the user to apply.
- **Inventory:**
  - Namespaces, workloads, PVCs (size, StorageClass, driver, and whether a VolumeSnapshotClass exists), StorageClasses, CSI drivers.
  - Each PVC shows its data path honestly: *snapshot* or *objects only (NFS, Phase 3)*.

### Backup of a namespace
1. **Objects:**
   - API discovery (preferred versions), then every namespaced object of the namespace, including Secrets and CRs.
   - Plus the cluster-scoped objects it uses: PVs of its PVCs, StorageClasses, CRDs of its CRs.
   - Stored as the blob `k8s-resources`: zstd JSON, one object per line with group, version, kind and name. A new manifest property.
   - It holds Secrets, which get the same protection as disk images; encryption at rest stays a separate roadmap item, and the admin guide says so.
2. **Hooks:**
   - Pre/post exec in containers, configured by the Velero annotation names, so existing annotations keep working; with timeout and on-error.
   - Pre-hooks run before the first snapshot, post-hooks right after the last.
3. **Volumes** (PVCs on a CSI driver with a VolumeSnapshotClass), one after another, crash-consistent:
   1. Create a VolumeSnapshot in the app namespace (tagged and recorded in the ledger) and wait for ReadyToUse.
   2. Expose it in `backdafuckup-system`: a static VolumeSnapshotContent and VolumeSnapshot on the same snapshot handle, with `allow-volume-mode-change`, then a PVC with `volumeMode: Block` from it (same size, same datastore).
   3. Start a mover pod with that block device.
   4. The mover pushes the device sequentially to the server over HTTPS (one-time token, server pin) and skips zero blocks. The server feeds `DiskBackupPipeline` through an `ISequentialDiskReader`.
   5. Every run reads the whole device: there is no CBT on vanilla vSphere CSI. Dedup stores only what changed.
   6. Then delete everything: the PVC, the pod, and both VolumeSnapshots and Contents. The snapshot lives only minutes.
   7. Crash recovery goes through the ledger and the tags.
4. **PVCs without snapshots** (NFS, no class): only their objects are backed up. The session log and the restore point say so.
5. **Restore point:**
   - One per namespace and run.
   - The manifest and entity get an object kind (VM or Kubernetes namespace) and a namespace descriptor (cluster, namespace, Kubernetes version, CSI facts). `Vm` becomes optional.
   - PVCs are disks: key = the PVC name; the base is matched by name, because the UID changes after a restore.
   - Consistency: crash-consistent per volume, and across volumes only as close as one after another; *application-consistent* when hooks ran without error.
6. **Jobs:**
   - `BackupJobObject` gets a kind and options (a namespace now; label selector and all namespaces later). Migration.
   - Schedule, retention and GC as for VMs; one job per cluster.

### Restores in the same cluster (Phase 1)
- **Namespace, all or selected objects:**
  - In Velero's order: CRDs, namespaces, StorageClasses, PVs/PVCs (data first), Secrets, ConfigMaps, ServiceAccounts, roles and bindings, …, workloads.
  - Metadata reset (uid, resourceVersion, ownerReferences …), status removed.
  - Existing objects: *skip* (default) or *replace*.
  - PVC data:
    1. Create a new volume as a block PVC in `backdafuckup-system`.
    2. The mover pulls the image from the server and writes it, skipping the zero ranges.
    3. Bind the volume as a Filesystem PV to the target PVC (static PV on the same volume handle; spike S3).
- **Single PVC:**
  - As a new PVC next to the original.
  - Or replacing it: scale the workload to 0, swap, scale back; with typed confirmation and a journal, as in the AD restore.
- **Files:**
  - The existing file browser on the PVC image (ext4/XFS without a partition table; new test fixture) and ZIP download.
  - Writing back into the live PVC needs a mover pod that mounts it next to the app pod (RWO: same node), as a new `IFileRestoreTarget`.
- **Object explorer:**
  - Browse a restore point's objects by kind, compare them with the live cluster, restore selected ones. The pattern of `AdExplorer`/`SqlBrowser`; a preview before writing.

### Later phases (same spec)
- **Phase 2:**
  - Restore into another cluster with namespace and StorageClass mapping. The data path does not depend on the snapshot.
  - Whole-cluster DR: cluster-scoped objects, CRDs, all namespaces; a "cluster" job object.
- **Phase 3:**
  - NFS PVs at file level (the mover reads the mounted share; needs a file store in the repository).
  - CBT through CSI SnapshotMetadata (gRPC; CSI hostpath already offers it).
  - VolumeGroupSnapshots where the driver has them.
  - Hook presets for Redis, MongoDB, RabbitMQ and Prometheus.

## Tasks
1. **Save spec documentation:**
   - `plan.md`, `shape.md` (decisions, QA facts with placeholders, risks: snapshot limit, I/O cost, Secrets in the repository, CSI EOL), `standards.md`, `references.md` (Velero docs, vSphere CSI code and KBs, KEP-3314, code paths), `visuals/`.
   - Roadmap entry under Phase 1.
2. **UI mockups (decision gate).** One artifact with both variants side by side, for the cluster page/inventory, the job editor (namespaces), Backups & Restore with namespaces, the restore wizard (namespace/PVC/files) and the object explorer:
   - **A:** like the VM pages. Clusters next to pools, namespaces like VMs.
   - **B:** an applications view per cluster with policies, like Kasten/Veeam.

   The user picks one; it goes into `shape.md`, and the screenshots into `visuals/`.

   **Done (2026-11-11):** variant A. B stays an option for parts later; the mockup sources are in `visuals/mockups/`.
3. **Test cluster preparation** (the user; the list comes from the plan):
   - vSphere CSI ≥ 3.5 with a VolumeSnapshotClass (`deletionPolicy: Delete`);
   - the CSI hostpath driver with snapshot support;
   - the namespace and RBAC from task 5;
   - the mover and agent images in the internal registry;
   - network: server → API 6443, pods → server 9555.
4. **Spike on the test cluster**, results in `spike.md`:
   - **S1:** snapshot create/ready/delete times and the FCD snapshot count.
   - **S2:** exposing a snapshot in the work namespace as a block PVC (time for the full copy; volume-mode annotation).
   - **S3:** restore rebind, block PVC to a Filesystem PV of the target PVC.
   - **S4:** mover throughput from pod to server.
   - **S5:** agent tunnel for API requests including watch and exec.
5. **Kubernetes connection:**
   - Provider kind, `KubernetesClient`.
   - Direct path (token, pin) and agent path (Linux agent image, enrollment, tunnel).
   - RBAC/Fleet generator, inventory, connection health and metrics, connection pages in the chosen UI variant.
6. **Mover and data channel:**
   - Mover image (Linux, minimal .NET).
   - Push (backup) and pull (restore) with one-time tokens; server receiver into the pipeline and `IDiskWriter`.
   - Ledger records for VS, VSC, PVC and pod; cleanup at startup and after failures.
7. **Backup of namespaces:**
   - Job object kind and options (migration), resource blob, hooks, the snapshot flow, manifest/entity changes, retention.
   - Consistency chips, session log, metrics (`bdfu_k8s_*`); PVCs without snapshots named.
8. **Restores in the same cluster:**
   - Namespace (all or selected objects; skip/replace) and PVC (new or replace, with confirmation).
   - Files (browse, ZIP, back into the PVC) and the object explorer.
9. **Docs and lab:**
   - Admin guide (Kubernetes chapter: setup, RBAC, snapshot limits, consistency, Secrets), runbook rows K1…, `THIRD-PARTY-NOTICES.md`.
   - Lab runs on the test cluster through both API paths.
10. **Phases 2 and 3:** their own tasks, planned once Phase 1 is in use.

## Verification
- **Tests:**
  - Unit tests for the resource order and metadata reset; the resource blob format; volume-mode/exposure manifests.
  - A fake Kubernetes API (HTTP test server) for the orchestration, including the agent tunnel.
  - `FileLevel` on an unpartitioned ext4/XFS fixture.
  - Full suite green; CI green.
- **Lab, on the test cluster only (CSI hostpath and vSphere CSI), both API paths.** A test namespace with Redis and MinIO on PVCs:
  - back it up (restore point with objects and PVC images; hooks in the log);
  - delete the namespace and restore it (apps start with their data);
  - restore one PVC as new and as a replacement;
  - browse, download and write back files;
  - restore selected objects through the explorer.
- **Nothing left:**
  - `kubectl get volumesnapshot,volumesnapshotcontent -A`, the PVCs and pods in `backdafuckup-system` are empty;
  - the FCD snapshot count is back to 0;
  - the ledger shows nothing pending.
- **No writes to the QA or production clusters.** Their changes (VolumeSnapshotClass, RBAC) are applied by the user through Fleet once Phase 1 is ready.
