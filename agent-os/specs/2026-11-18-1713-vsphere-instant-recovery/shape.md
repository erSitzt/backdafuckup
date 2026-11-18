# Instant VM Recovery (vSphere) — Shaping Notes

## Scope

**What it does.** Start a vSphere VM directly from a restore point:
- the backup server exports the restore point's disks as an NFS datastore with a write overlay;
- ESXi mounts it, and the VM is created and powered on.

**Afterwards:**
- migrate the VM to production storage (Storage vMotion), or
- stop it and discard everything.

Several VMs can share one session, for example a DC, a SQL server and an app server.

**Related specs.** Spec 2 of 3. It builds on spec 1 (vim25 client, VM config metadata, `VSphereVmSpecBuilder`).

## Decisions

### By the user (2026-11-18)
- Instant VM recovery is part of the vSphere work, as its own spec.
- Test environment: the separate vSphere 8 test host.

### In the design (2026-11-18)

**Where it runs**
- **NFS server in the backup server process,** on Windows and Linux.
- The library stays separate, so a proxy can host it later.

**Protocol**
- **Our own NFSv3 server**, from the RFCs, with no new runtime dependency.
- Fallback if spike S1 fails: FUSE plus kernel nfsd on the Linux proxy.

**Datastore and VM**
- **One datastore per session,** with one directory per VM.
- The VM is created through `CreateVM_Task` with existing disks. No generated .vmx.

**Persistence and safety**
- **Restarts:** file handles, fsid and fileids stay stable; the overlay and namespace persist; sessions resume after a server restart.
- **Nothing is destroyed automatically.**
- **Stop:** destroys the VM only if all its files are on our datastore.

## Context

- **Visuals:** none.
- **References:** see `references.md`.
- **Product alignment:** `mission.md` lists a UI "on par with Veeam" and reliable restore workflows. Instant recovery fills the gap between file-level restore and a full restore.

## Standards applied

- See `standards.md`: conventions of the earlier specs, plus the additions shared by the three vSphere specs.
