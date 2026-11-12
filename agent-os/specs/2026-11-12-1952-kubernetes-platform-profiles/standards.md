# Standards for Kubernetes Platform Profiles

The standards of `../2026-11-11-1343-kubernetes-workload-backup/standards.md` apply, including its references to the earlier specs: placeholders in the repository, nothing left behind in a cluster, only the chosen clusters, secrets, error messages with context, Velero as reference only.

## Additions for this spec

### Facts about third-party platforms
- Every untested profile names the public documentation it comes from (`sources`) and is marked untested in the console and the admin guide.
- No rule is taken from a purchased product or its binaries.

### What stays out is visible
- A run names each namespace and the count of objects left out, per profile.
- The restore preview names what stays out of another cluster, per profile.
- The console shows the detected profiles with the evidence that matched.

### Ids are forever
- Profile ids are recorded in restore points: they are never renamed or reused, only deprecated.
- Rule content always comes from the current catalog, so fixes reach old restore points.

### Least privilege
- The backup role grants `get` per resource, never `get` on `*/*` (which would include `nodes/proxy`, `pods/proxy` and `services/proxy`).
