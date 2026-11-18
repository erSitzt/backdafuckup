# Standards for the VMware vSphere Provider

No standards are indexed in `agent-os/standards/` yet. The conventions of the earlier specs apply, in particular the foundation (`../2026-11-01-2134-xcpng-backup-foundation/`) and scale-out repositories (`../2026-11-17-1250-scale-out-repositories/`):
- restore points are written once;
- the manifest is the commit point;
- nothing is referenced before it is durable;
- readers pin restore points;
- the server holds the logic, agents are thin.

## Additions for this spec (shared with specs 2 and 3)

### Only public, documented interfaces
- **Allowed:** vim25 operations from the public reference, the NBD protocol spec, datastore `/folder` for small files.
- **Not allowed:** VDDK, NFC, `RetrieveInternalContent`, TAP, VAIO, PowerCLI DLLs.
- govmomi, pyVmomi and vcsim are references only.
- No code or design details from purchased products.

### Never destroy what we did not create
- A HotAdd removal never carries `fileOperation=destroy`. A unit test checks every reconfiguration the proxy code builds.
- User snapshots, VMs and disks are never changed, except by an explicit restore or rollback the admin confirmed.
- A rollback works only after strict identity checks, and keeps a safety snapshot.

### A ledger row before every external call
- Every snapshot, attached disk, created disk or VM and safety snapshot is recorded in the artifact ledger before the call that creates it.
- Crash cleanup works from the ledger in a fixed order: proxy device, HotAdd disk, snapshot.

### Errors name the place
Messages name:
- the vCenter, ESXi host, cluster and datastore;
- the VM (name and moref);
- the proxy (name, VM, host) and the device path;
- the operation and the vim25 fault.

Never only an id.

### Both operating systems, and the proxy on Linux
- The server runs on Windows and Linux.
- The proxy agent is linux-x64 only.
- Tests that need loop devices or sysfs run in a Linux-only lane.
