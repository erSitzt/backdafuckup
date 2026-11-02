# XCP-ng Backup Foundation — Shaping Notes

## Scope

Build the project foundation and the first end-to-end slice for XCP-ng / XenServer:

- Solution skeleton: .NET 10, ASP.NET Core REST API, a Blazor web console (MudBlazor), a PostgreSQL catalog, Serilog. Runs as a service on Linux (systemd) and on Windows.
- A hypervisor-neutral core: a hypervisor provider abstraction, a data mover abstraction and a repository abstraction.
- Registering an XCP-ng pool: TLS trust on first use, credentials, pool master handling, and an inventory of VMs, SRs and networks.
- One backup repository type: a directory on a locally mounted volume.
- Backup jobs: VM selection, target repository, a daily or weekly schedule, and retention (keep N restore points).
- **Full and CBT-incremental backups, read through NBD**, stored in our own chunk format.
- Restoring a whole VM to the original location (with replace) or an alternate location (another host or SR, new name, network mapping, new MACs).
- Live progress and session log in the UI (SignalR), plus session history.

### Out of scope (later specs)

- App-consistent backups (VSS). These need our own in-guest agent, because XAPI's quiesced snapshots were removed in XenServer / XCP-ng 8.1.
- AD object restore, MS SQL backup and restore, SQL log backup.
- Backup targets other than a local volume: SMB, NFS, S3.
- Storage-snapshot backup from the HPE Alletra MP B10000.
- Notifications, tape, offsite copies, file-level restore.
- vSphere and Hyper-V providers.
- LDAP, AD or OIDC authentication.

## Decisions

### Confirmed by the user

- **What to build first:** the foundation plus an XCP-ng backup and restore slice.
- **Server platform:** Linux *and* Windows.
- **First backup target:** a local volume, e.g. an iSCSI LUN from the HPE MSA mounted on the backup server.
- **Storage format:** our own chunk format (compressed and content-addressed). It must be ready for CBT, dedup, S3 and immutability later.
- **Also in this slice:** schedule, retention, restore to an alternate location, live progress.
- **Data path:** NBD and CBT incrementals from the start, not plain HTTP export first.
- **Licensing policy:** the whole product will be published as open source. Open-source dependencies are fine, including copyleft. Forbidden: tools, libraries or code obtained only through a purchased product (e.g. Veeam binaries, VDDK), partner-gated SDKs, and undocumented APIs.
- **Project licence:** AGPL-3.0, with a CLA for outside contributors so dual licensing stays possible. The planned business model is support subscriptions, a turnkey appliance and updates, like Vates / Xen Orchestra.
- **Product alignment:** confirmed as described in `agent-os/product/`.
- **Visuals:** none provided.
- **References:** `research/Xen/` (the Veeam plugin; config and behaviour only, never decompiled) and the official XAPI documentation.

### Technical decisions (from planning)

- **XAPI client:** our own thin async JSON-RPC client with per-host TLS pinning. The XenServer.NET SDK (BSD-2) is used only as a reference.
- **NBD client:** our own C# implementation, written from the public-domain NBD protocol spec. No .NET NBD client exists, and an own client works on both Linux and Windows.
- **Chunks:**
  - 1 MiB, fixed size per repository.
  - SHA-256 over the plaintext.
  - zstd level 3 via ZstdSharp.Port.
  - All-zero chunks are not stored.
  - Every restore point has a complete index, which makes backups forever-incremental with dedup.
- **Restore:** `VDI.create`, then `import_raw_vdi` with a raw stream rebuilt from the chunks, then `import_metadata` with a VDI mapping.
- **Scheduler:** our own `PeriodicTimer`-based scheduler, daily and weekly.
- **UI:** Blazor WebAssembly, using only REST and SignalR.
- **Auth:** local Identity accounts plus a first-run setup token.
- **Credentials:** encrypted with ASP.NET Data Protection; the key ring stays on disk.

### Constraints noted

- CBT is silently disabled after a host or SR crash, so we must detect that and fall back to a full backup.
- While CBT is enabled, storage live migration of that VDI is blocked. This must be documented and shown in the UI.
- The NBD client must not run in dom0. XenServer allows at most 16 concurrent NBD connections, and networks with purpose `nbd` require TLS.
- `research/` holds proprietary Veeam files. It must never be committed or published.

## Context

- **Visuals:** None
- **References:** `research/Xen/Service/appsettings.json` (Veeam's timeouts, retries and concurrency, inspiration only), the XAPI documentation, and the XenServer CBT/NBD developer guide. See `references.md`.
- **Product alignment:** Implements the MVP items "Full VM backup", "Scheduling & retention", "Restore" and "Status" (live progress) from `agent-os/product/roadmap.md`. It also pulls CBT incrementals into the MVP. It lays down the hypervisor and data mover abstractions that the storage-snapshot feature and the vSphere / Hyper-V providers need later.

## Standards Applied

None yet: `agent-os/standards/index.yml` is empty. This spec sets the first conventions; see `standards.md`.
