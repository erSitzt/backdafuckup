# References for Kubernetes Platform Profiles

## Code in this repository

### Where the platform knowledge lives today
- **`src/Backdafuckup.Contracts/KubernetesClusterScope.cs`:** `DefaultExcludes`, the `cluster:` job object and its pattern grammar.
- **`src/Backdafuckup.Kubernetes/KubernetesClusterResources.cs`:** `SkippedCrdGroups`, `DefaultRoles`, `ManagedPrefixes`, `IsManaged`.
- **`src/Backdafuckup.Kubernetes/KubernetesObjects.cs`:**
  - `IsRancherMade`, `IsRancherGroup`, `IsRancherField` (stripped in `Prepare`);
  - `IsClusterMade` and `IsControlled`, which stay in code.
- **Restore paths that filter by platform:**
  - `KubernetesNamespaceRestore.cs` (the Rancher counter, the CRD filter);
  - `KubernetesRestorePlanner.cs` (the Rancher warning, `CheckDefinitionsAsync`);
  - `KubernetesClusterRestore.cs` (the rewrite for cluster-scoped objects).
- **The job editor:** `src/Backdafuckup.Web/Pages/JobEditor.razor` (default excludes, helper text). Validation in `OperationEndpoints.cs`, expansion in `BackupSessionRunner.ExpandSelectorsAsync`.

### Detection inputs and storage
- **`KubernetesInventory.GetPlatformAsync`** (`/version`, the `kube-system` uid). `PlatformInfo.ProductName` is hard-coded to "Kubernetes" today.
- **`HypervisorConnection`** in `src/Backdafuckup.Server/Data/Entities.cs` (`ProductName`, `ProductVersion`). Refreshes in `InfrastructureEndpoints.cs`.
- **`KubernetesNamespaceDescriptor`** in `src/Backdafuckup.Core/Repository/Manifest.cs`.
- **Connection options:** `ConnectionManager.BuildSettings` (`EngineInfrastructure.cs`), `ClusterAgents.Settings`.
- **Embedded resource precedent:** `src/Backdafuckup.Ssh/Backdafuckup.Ssh.csproj` (`restore.sh`).

### Tests that pin today's behaviour
- **Kubernetes tests:** `tests/Backdafuckup.Kubernetes.Tests/ClusterResourcesTests.cs`, `KubernetesObjectsTests.cs`, `KubernetesProviderTests.cs`.
- **Server tests:** `tests/Backdafuckup.Server.Tests/KubernetesClusterScopeTests.cs`, `ApiIntegrationTests.ClusterJob.cs`, `ApiIntegrationTests.OtherCluster.cs`.
- **Fake API:** `tests/Backdafuckup.Testing/Kubernetes/FakeKubernetesApi.cs`.

## External references (public documentation, behaviour only)
- **Kubernetes API discovery** (`/apis`) and the RBAC defaults (`kubernetes.io/bootstrapping=rbac-defaults`, the addon manager label `addonmanager.kubernetes.io/mode`).
- **Platforms:** the platform documentation of RKE2, k3s, Rancher, Fleet, OpenShift, GKE, EKS, AKS, MicroK8s, k0s and Tanzu Kubernetes Grid. Each profile's `sources` field names the pages it relies on.
- **Velero** (Apache-2.0): its notes on restoring into other clusters. Read for behaviour only.
