# Kubernetes Platform Profiles — Shaping Notes

## Scope

The rules for what a cluster's platform makes itself are data, not code. A profile per platform says:
- which namespaces a whole-cluster job leaves out;
- which CRD groups the platform installs;
- which objects it makes (and makes again);
- which fields mean nothing in another cluster;
- where its kubelet and container runtime keep their files.

Profiles are detected per cluster, can be overridden per connection, and are recorded in restore points.

## Decisions

### By the user (2026-11-12)
- **Why:** "when we check ownership (isRancher…) currently we use rancher, but that may change and others may not use rancher for their k8s". The product must not assume Rancher, RKE2 or vSphere.
- **Order:** platform profiles first, then the file-level backup (`../2026-11-12-1952-kubernetes-file-level-volumes`), which needs the kubelet paths.

### In the design (2026-11-12, reviewed against the code)
- **The catalog:**
  - one embedded JSON file in `Backdafuckup.Kubernetes`, loaded with source-generated JSON;
  - it fails on unknown members;
  - the console never parses it, the server resolves it into DTOs.
- **Several profiles per cluster:** one distribution, managers, addons. The base profile `kubernetes` is always in the set.
- **Detection:**
  - Sources: `/version`, `/apis` discovery (OpenShift's groups are aggregated APIs, not CRDs), namespaces, a sample of nodes, CSI drivers.
  - Runs at the inventory scan and at the start of each backup run.
  - Restores never detect: they use the platforms recorded in the restore point.
- **Older restore points** have no platforms and use a fixed legacy set that reproduces the behaviour before profiles.
- **Code vs. catalog:** Kubernetes semantics and the product's own objects stay in code (cluster-made objects, controller-owned objects, the work namespace, the snapshot API, `managed-by=backdafuckup`).
- **OpenShift `openshift.io/sa.scc.*` annotations are kept** in another cluster on purpose: the restored volume data keeps its owners, and new UID ranges would make it unreadable.
- **Whole-cluster jobs:**
  - get the token `@platform`, expanded at run time;
  - existing literal exclude lists are never rewritten, because that would change what they back up;
  - new jobs use the token.
- **Untested platforms** (OpenShift, GKE, EKS, AKS, k3s, MicroK8s, k0s, Tanzu, Cilium, Antrea, Longhorn) come from public documentation, are marked untested, and name their sources.
- **RBAC hardening (found while designing):** the backup role's `get` on `*/*` also grants `nodes/proxy`, `pods/proxy` and `services/proxy`. The role gets explicit `get` rules instead.

## Context

- **Visuals:** none.
- **References:** see `references.md`.
- **Product alignment:** the integration policy in `agent-os/product/tech-stack.md` (public, documented APIs; no code from purchased products). The roadmap entry for Kubernetes workloads.
