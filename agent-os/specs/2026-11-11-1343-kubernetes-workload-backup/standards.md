# Standards for Kubernetes Workload Backup

There are no formal standards yet (`agent-os/standards/index.yml` is empty). These conventions apply:

- **The foundation spec** (`../2026-11-01-2134-xcpng-backup-foundation/standards.md`):
  - .NET 10, nullable, warnings as errors, async with `CancellationToken`;
  - REST under `/api/...` with ProblemDetails and `X-BDFU-Request: 1`, SignalR;
  - the console in MudBlazor;
  - format versions, write-once restore points, the manifest as the commit point;
  - platform objects tagged and recorded in the ledger.
- **The guest agent spec** (`../2026-11-02-1430-windows-guest-agent/standards.md`):
  - the agent dials out and the server never connects to it;
  - agent key, server certificate pinning, no secrets in logs;
  - only delete objects the product created itself.
- **The file-level restore spec** (`../2026-11-02-1713-file-level-restore/standards.md`):
  - guest data is untrusted;
  - fail loudly;
  - every browse is an audited session.
- **The AD object restore spec** (`../2026-11-06-2308-ad-object-restore/standards.md`):
  - a preview before writing to a live system;
  - typed confirmation for destructive steps;
  - every write named in the log.
- **The backups page spec** (`../2026-11-09-1337-backups-page-at-scale/standards.md`):
  - list rows lean;
  - state in the URL;
  - new views get new endpoints.
- **The metrics spec** (`../2026-11-10-1151-prometheus-metrics/standards.md`):
  - `bdfu_` names, bounded labels, no queries during a scrape.
- **The integration policy** in `agent-os/product/tech-stack.md`:
  - public, documented APIs only;
  - open-source code may be read, but nothing from purchased products;
  - every dependency in `THIRD-PARTY-NOTICES.md` and `Directory.Packages.props`, AGPL-compatible.

The additions below apply on top.

---

## Additions for this spec

### Nothing left behind in a cluster
- Every object the product creates is labelled with the session and restore point and recorded in the artifact ledger:
  - VolumeSnapshots in app namespaces;
  - VolumeSnapshotContents;
  - PVCs, PVs and pods in `backdafuckup-system`;
  - restored objects (labelled only).
- It is removed on success, on failure, and by the cleanup at startup.
- A VolumeSnapshot lives only as long as its copy runs.
- Nothing the product did not create is deleted. A restore that replaces an object names it in the preview and in the log.

### Only the clusters that were chosen
- The product writes only to the cluster of the job or of the restore's target.
- In development, tests write only to the test cluster `rke2-test`, never to QA or production.

### Secrets
- Cluster tokens and agent keys are stored like credentials (data protection) and never logged.
- Kubernetes Secrets are backed up as objects. Their values never appear in logs, previews or diffs: the diff shows keys only.

### Error messages with context
- Messages name the cluster, namespace, object kind and name, PVC and StorageClass, CSI driver, and the API URL or agent.
- Example: "VolumeSnapshot data-redis-0 in redis (cluster rke2-test, driver csi.vsphere.vmware.com) not ready after 5 min: …".

### Velero as reference only
- Velero (Apache-2.0) is read for behaviour: restore order, metadata reset, hook annotations. Nothing is copied.
- The hook annotation names are reused on purpose, so annotations already on workloads keep working.

### Placeholders in the repository
- Docs, specs, runbook rows and tests use placeholders, never the real cluster names, registries or addresses:
  - clusters `rke2-qa`, `rke2-test`;
  - registry `registry.corp.example`;
  - addresses `10.26.x.y`.
