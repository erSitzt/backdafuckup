# Tech Stack

## Frontend

- **Blazor WebAssembly (C#):** web management console (MVP), hosted by the backend server. It talks to the backend only through the REST API and SignalR (live progress). One language and toolchain shared with the backend
- **UI goal:** easy to use and on par with Veeam in look and feel and workflows
- **Component library:** MudBlazor (MIT)

## Backend

- **.NET 10 / C#:** one service process `backdafuckup` that runs the REST API, the SignalR hub, the job scheduler and engine, the data movers, and hosts the UI
- **ASP.NET Core:** REST API for managing jobs, restore points and status. Errors are ProblemDetails. Auth uses local accounts (ASP.NET Core Identity, cookies); AD/LDAP/OIDC come later
- **Runs on Linux and Windows:** systemd service (`AddSystemd`) or Windows service (`AddWindowsService`). Kestrel serves HTTPS with a self-signed certificate on first start
- **Logging:** Serilog
- **Metrics:** `System.Diagnostics.Metrics` instruments, exported for Prometheus at `/metrics` through OpenTelemetry's Prometheus exporter (Apache-2.0). Pull only: nothing is sent out. A metrics-only API token protects the endpoint
- **Own XAPI client:** thin async JSON-RPC client with per-host TLS certificate pinning. The XenServer.NET SDK (BSD-2) is a reference only
- **Own NBD client:** C# implementation of the public-domain NBD protocol spec, with TLS, pipelining and multiple connections. Reads VM disks for full and CBT-incremental backups

## Backup Repository Format

- **Chunk store:** disks are split into fixed-size chunks (default 1 MiB), hashed with SHA-256, compressed with zstd (ZstdSharp.Port) and stored once (dedup). All-zero chunks are not stored
- **Restore points:** each one has a manifest and a complete chunk index per disk, so every restore point is restorable on its own (forever-incremental)
- **Write-once:** chunks, indexes and manifests are never modified in place, which keeps S3 and immutability possible later. Retention deletes manifests; garbage collection removes unreferenced chunks
- **Self-describing:** a repository can be re-imported into a fresh server (disaster recovery of the backup server)

## Database

**PostgreSQL** for the backup catalog: jobs, restore points, and backup/restore/session history. Chosen over SQLite so the catalog can grow with larger installations and long-term history.

## Backup Storage

- **File shares:** SMB / CIFS and NFS
- **Object storage:** S3-compatible (AWS S3, MinIO, etc.)
- **Block storage:** iSCSI LUNs (e.g. HPE MSA) mounted as a local volume on the backup server
- **Tape (Phase 2):** HPE MSL tape library (LTO) for moving backups to tape

## Storage Snapshot Integration

- **Array plugin interface:** create and delete snapshots, present a snapshot to the proxy host, list and clean up. One plugin per array family
- **HPE Alletra Storage MP B10000 (MVP):** on-array Web Services API (WSAPI) v3 REST at `https://<array>/api/v3`
  - API is public: the OpenAPI 3.1 spec is downloadable without login from developer.hpe.com/ws/api-spec (file `ws-open-api.json`, "WS-APIv3" v3 2.5.0, 2025-05-29). Each array also serves it for its own OS version under `/docs`; use the array's copy as the reference. Our reference array serves v3 2.5.50 (2025-10-01), which is newer than the public copy. For now, development uses the public v3 2.5.0, stored at `research/HPE-Alletra/ws-open-api-2.5.0.json`
  - Endpoints for backup: `credentials` (session), `volumes` (`CREATE_SNAPSHOT_VOLUME`, `PROMOTE_SNAPSHOT_VOLUME`), `applicationsets` (`CREATE_SNAPSHOT_APPSET` for consistent snapshots across several volumes), `vluns`, `hosts` / `hostsets` (present the snapshot to the proxy), `tasks` (track async jobs)
  - Check whether the array needs a Virtual Copy (snapshot) licence. Veeam lists one as a requirement for this array family
- **XCP-ng data path from an array snapshot:**
  - A Linux backup proxy gets the snapshot LUN over iSCSI/FC
  - The proxy parses the LVM metadata of the snapshot LUN in user space and reads the VHD chains from the logical volumes. It never activates or imports the volume group (`vgimportclone` would write to the PVs). LVM and the VHD format are openly documented; the LVHD layout is read from the open-source `sm` code
  - XAPI CBT supplies the changed blocks
  - Opt-in, with verification against NBD reads (spec `2026-11-18-1713-storage-snapshot-backup`)
- **vSphere data path from an array snapshot** (same spec):
  - Supported: the snapshot LUN is presented to an ESXi host, resignatured and mounted as a temporary datastore (public vim25), and the HotAdd proxy reads the VMDKs from it
  - Opt-in accelerator: our own VMFS6 reader on the proxy reads the snapshot LUN directly (see the VMFS exception in the Integration Policy)
- **Proxy requirement:** the backup proxy needs SAN access (iSCSI or FC) to the primary array

## Other

- **Hypervisor integration:** pluggable hypervisor provider layer behind a common core
  - XCP-ng / XenServer (MVP): XAPI for snapshots, VM export/import and SR handling
  - VMware vSphere (in progress, spec `2026-11-18-1713-vsphere-provider`): vim25 Web Services API for snapshots and CBT, through our own hand-written C# SOAP client (the WSDL that vCenter serves is the reference; no Broadcom DLLs). Disk data goes through a Linux HotAdd proxy VM (`ReconfigVM_Task`), which serves the attached disks to the server as NBD inside a WebSocket tunnel. No VDDK
  - Hyper-V (later): WMI v2, Resilient Change Tracking (RCT), virtdisk.dll and VSS, all publicly documented by Microsoft
- **Data mover abstraction:** hypervisor-neutral interface (changed-extent source plus random-access reader/writer), designed in Phase 1 so each hypervisor plugs in its own data mover
- **Windows guest helpers:** VSS for application-consistent backups, plus MS SQL Server and Active Directory integration for app-aware backup and item-level restore
- **Hosting:** self-hosted on-premises by each customer; our own environment is the first reference installation

## Integration Policy

The product is open source under **AGPL-3.0**. Outside contributors sign a CLA, which keeps dual licensing possible.

We use only publicly available, documented APIs and documentation. That means:

- **Open-source dependencies are fine,** including copyleft licences (GPL, LGPL, AGPL), as long as they are compatible with AGPL-3.0
- **Nothing from purchased products:** no code, binaries, tools or libraries that are obtainable only through a purchased product (e.g. Veeam binaries or VDDK)
- **No vendor partner programs:** no Broadcom TAP or similar
- **No gated SDKs:** no VDDK
- **Nothing undocumented:** no internal or undocumented APIs and protocols, such as VMware NFC or `RetrieveInternalContent`
  - **One exception (user decision 2026-11-18): undocumented on-disk formats may be read through open-source readers, here VMFS6.** Only code under GPL-2.0-or-later (or a more permissive licence) may be ported, file by file after a licence audit. Such a reader is opt-in, verified on every run against data known from CBT and the previous restore point, and falls back to the supported path (ESXi resignature + HotAdd) on any doubt. Protocols and APIs stay public-only
- **Record every dependency** in `THIRD-PARTY-NOTICES.md`

Background: `reports/VMware vSphere integration access.md`.

## Reference Material

`research/` holds third-party material only as a reference for how Xen / XCP-ng integration can work. Everything is our own implementation; no code or binaries from it are used. `research/` contains proprietary files (Veeam binaries) and is **never committed or published** (excluded via `.gitignore`).
