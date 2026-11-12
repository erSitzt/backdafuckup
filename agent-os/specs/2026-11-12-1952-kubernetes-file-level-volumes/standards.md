# Standards for File-level Backup of Kubernetes Volumes

The standards of `../2026-11-11-1343-kubernetes-workload-backup/standards.md` apply, including its references to the earlier specs, in particular file-level restore (guest data is untrusted, fail loudly, audited browsing) and the foundation (write-once restore points, the manifest as the commit point, format versions).

## Additions for this spec

### Shadow PVs never risk the source data
- Copied: only the volume source, node affinity, mount options and capacity.
- Never copied: annotations (such as `pv.kubernetes.io/provisioned-by`), finalizers, the reclaim policy.
- Always set: `Retain`, read-only, `storageClassName: ""`, a pre-bound claim, the product's labels; hostPath `type: Directory`.
- A PV is deleted only when it carries the product's labels and `Retain`.

### Live reads are named as such
- Files read from volumes in use are not a point-in-time copy. The restore point's consistency text names those volumes and the files that changed while read.

### Formats are versioned
- New restore point content raises the manifest format version, so older servers refuse it before GC or verify could miss its chunks.

### Least privilege on nodes
- The node agent is optional and opt-in. It runs in its own namespace, without `privileged: true`, and reads only through the kubelet's directory, read-only.
- It accepts no URLs or paths from requests.
- Kubelet TLS is always verified (cluster CA or a pinned certificate), never skipped.
