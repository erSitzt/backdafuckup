# Plan: Kubernetes platform profiles

Follow-up to `../2026-11-11-1343-kubernetes-workload-backup` (phase 2 lab runs K12–K14). Its sibling `../2026-11-12-1952-kubernetes-file-level-volumes` is built after it and takes the kubelet and container runtime paths from the profiles.

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

## Groundwork (shared with the sibling spec)


1. **Spec docs.** Both spec folders (`plan.md`, `shape.md`, `standards.md`, `references.md`) and the roadmap.
2. **RBAC hardening.**
   - The backup role keeps `list` and `watch` on `*/*`, and `get` only where needed: secrets, the kinds the restore reads by name, `pods/log`, `nodes/stats` later.
   - `KubernetesApplier.ReadAsync` reads with `list` plus `fieldSelector=metadata.name` instead of `get`.
   - Test: a SelfSubjectAccessReview against the generated manifest shows no `nodes/proxy`, `pods/proxy` or `services/proxy`.
   - Admin guide and release note: re-apply the manifest.

## Platform profiles


**Catalog**
- Embedded JSON at `src/Backdafuckup.Kubernetes/Platforms/platforms.json`, embedded like `Backdafuckup.Ssh`'s `restore.sh`.
- Loaded with a source-generated context. Unknown members fail; the catalog is validated at load and by a test.
- The Web project never parses it: the server resolves it into DTOs.

**Profile schema (v1)**

| Field | Meaning |
|---|---|
| `id` | Stable forever; restore points record it. |
| `name` | Display name. |
| `category` | base / distribution / manager / addon. |
| `tested` | true only for what the lab ran. |
| `sources` | URLs of the public docs. Required when untested. |
| `notes` | Hints shown in the console. |
| `detect` | Any of: `gitVersion` regex, `apiGroup` glob (from `/apis` discovery, so aggregated APIs like OpenShift's count), `namespace` glob, `nodeProviderId` prefix, `nodeLabel`, `nodeAnnotation`, `csiDriver`. |
| `namespaces` | What `@platform` leaves out. |
| `crdGroups` | Definitions the platform installs: never backed up as cluster-scoped objects, never created from a backup. Into another cluster, their objects go only if the target serves the group, with a warning otherwise. |
| `made` | Matchers over kinds, groups, names, labels and annotations, each optionally `always`. Cluster-scoped matches are skipped at backup; namespaced matches are skipped when restoring into another cluster, or on every restore with `always`. |
| `strip` | Label and annotation keys removed in another cluster. |
| `kubeletRootDir`, `containerRuntimeRoot` | Distributions only. Used by the node agent. |

**What stays in code:** Kubernetes semantics and the product's own objects (`IsClusterMade`, `IsControlled`, the work namespace, the snapshot API, `managed-by=backdafuckup`).

**Built-in profiles**
- **Tested:**
  - kubernetes (base, always: `kube-*`, `system:*`, rbac-defaults, PriorityClass `system-*`, the `addonmanager.kubernetes.io/mode` label);
  - rke2, rancher, fleet;
  - calico / canal, vsphere-csi, istio (`istio-ca-root-cert` made always);
  - kind.
- **Untested, from public docs:**
  - k3s, openshift (sa.scc annotations kept on purpose, notes on SCCs), gke, eks, aks, microk8s, k0s, tanzu;
  - cilium, antrea, longhorn.
- **Legacy set:** kubernetes, rke2, k3s, rancher, fleet, calico, cilium, vsphere-csi. Restore points without recorded platforms use it, which reproduces today's behaviour.

**Tasks**

3. **Rules as data, no behaviour change.**
   - Catalog, loader and `KubernetesPlatformSet`:
     - `Ids`, `Display`, `NamespacePatterns`;
     - `IsCrdGroup`, `MadeBy(obj, otherCluster)`, `IsStripped`;
     - the kubelet and runtime paths;
     - `Base`, `Legacy`, `Of(ids)`.
   - `IsRancherMade`, `IsRancherGroup`, `IsRancherField`, `SkippedCrdGroups`, `ManagedPrefixes` and the cattle checks in `IsManaged` are replaced by set calls; every caller passes `Legacy`.
   - `ObjectRewrite` gains `Source`.
   - Touched: `KubernetesObjects`, `KubernetesClusterResources`, `KubernetesNamespaceRestore`, `KubernetesRestorePlanner`, `KubernetesClusterRestore`.
   - Tests: catalog checks, and a parity test (Legacy leaves out at least what today's code does; additions listed on purpose). Existing tests stay green apart from log texts.
4. **Detection.**
   - `ClusterFacts.ReadAsync`: `/version`, `/apis`, namespaces, up to 20 nodes, CSIDrivers and StorageClass provisioners. It tolerates 403 and 404.
   - `KubernetesPlatformDetector.Detect` is a pure function.
   - Runs at the inventory scan (create, rescan, agent connect) and at the start of each Kubernetes backup run, where it is logged and persisted.
   - Agent clusters work the same way through the tunnel.
   - The fake API serves `/apis`. Fixtures in `tests/fixtures/k8s-platforms/` cover:
     - captured live: the lab RKE2 cluster and kind;
     - from docs: openshift, gke, eks, aks, k3s, microk8s, k0s, tkg, plain kubeadm.

     Each fixture asserts the exact detected set and at most one distribution.
   - `scripts/k8s-platform-facts.sh` (read-only) captures new fixtures.
5. **Storage and API.**
   - New connection columns `DetectedPlatforms` and `PlatformOverride` (jsonb, one migration). `ProductName` becomes the display name, e.g. "RKE2 · Rancher · Fleet", refreshed on every scan.
   - The override travels as the connection option `kubernetes.platforms` (`ConnectionManager.BuildSettings`, `ClusterAgents.Settings`).
   - `PUT /api/connections/{id}/platforms`, `GET /api/kubernetes/platforms`, and `ConnectionDto.Platforms` (detected with evidence, override, effective, namespaces per profile).
   - `KubernetesNamespaceDescriptor.Platforms` (ids) is recorded by namespace and cluster-scope backups. No manifest version change.
6. **Profiles drive backup and restore.**
   - The cluster-scoped collector uses the run's effective set. Its log names the counts per profile ("302 left out: 210 Kubernetes, 80 Rancher, 12 RKE2").
   - Restores use the source set from the restore point, or `Legacy`.
   - Platform CRD handling, with one warning per group the target lacks.
   - Preview and restore texts per profile.
7. **`@platform` in whole-cluster jobs.**
   - `cluster:@platform,<patterns>`. The token expands at run time to the effective set's namespaces. Validation rejects other `@` items.
   - New jobs default to it.
   - Existing literal lists keep working unchanged. The job editor shows a hint and a "Use the platform's list" button when a job has the old default list.
   - The run log names each left-out namespace and its profile.
8. **Console.**
   - The clusters page gets a Platform column and a detail panel: profiles, evidence, an "untested" badge, notes.
   - A "Set platform…" override dialog.
   - The job editor's whole-cluster block shows the platform's patterns per profile and an "also leave out" field. Platform names leave the helper texts.
9. **Docs and lab.**
   - Admin guide: a profile table with a Tested column, overrides, the facts script, OpenShift notes.
   - Runbook rows:
     - K15: detection on `rke2-test`; a `@platform` job equals K13 without the hand-added exclude;
     - K16: kind detected; a restore RKE2 → kind; the override round trip.

## Verification


- **Each task:** unit and integration tests as listed, and the full suite green (`~/.dotnet/dotnet test`). CI green on Ubuntu and Windows, integration on Linux.
- **Live, on test clusters only.** Deploy only when asked, after checking sessions, not at :00/:15/:30/:45.
  - **K15/K16 (profiles):**
    - detection on `rke2-test` shows RKE2 · Rancher · Fleet plus Calico, vSphere CSI and Istio;
    - a `@platform` whole-cluster job leaves out `vmware-system-csi` by itself;
    - kind is detected as kind;
    - an RKE2 → kind restore leaves out what the profiles say;
    - the override round trip.

- **Safety:**
  - no writes to QA or production;
  - in `rke2-target`, only new namespaces and `backdafuckup-system`;
  - nothing deleted in XCP-ng;
  - shadow PVs never carry a provisioner annotation or a policy other than `Retain`.
