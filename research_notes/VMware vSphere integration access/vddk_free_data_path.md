# VDDK-free data paths for vSphere VM disk backup and restore (state as of 2026-11-01)

Scope: ways to read VM disk data (and write it, for restore) without Broadcom's VDDK, for a commercial C#/.NET backup product. The control plane (CreateSnapshot_Task, QueryChangedDiskAreas, ReconfigVM_Task over vim25 SOAP / VI-JSON) is assumed usable. VDDK access, partner programmes, editions and .NET SDKs are covered in the sibling notes and not repeated here.

Conventions: "[Primary]" marks first-party code, API references, or the project's own documentation. "[Secondary]" marks press, blogs and vendor marketing. "[Forum]" marks community posts. Legal points are non-lawyer research and **not legal advice**. Most primary sources here are git repositories read on 2026-11-01 through the GitHub/GitLab APIs.

**Headline finding.** After the VDDK withdrawal, three independent VDDK-free implementations of VMware's NFC disk protocol (TCP 902) appeared: one shipped by a commercial vendor, one Apache-2.0 drop-in replacement library, and one Python library. The datastore HTTP `/folder` endpoint and HotAdd-style attachment via ReconfigVM_Task are also in production use without VDDK. Two open-source tools reacted directly: Xen Orchestra (Vates) removed VDDK entirely in September 2026, and nbdkit deprecated its VDDK plugin in October 2026.

---

## 1. Datastore HTTP file access (`https://<host>/folder/<path>?dcPath=…&dsName=…`): base flat VMDK under a snapshot, Range reads, throughput, sessions/privileges, vSAN/vVols, and how Proxmox and others use it

### Takeaway
The `/folder` endpoint supports HTTP Range reads, so CBT-guided partial reads of a flat VMDK are technically possible. Proxmox's and Xen Orchestra's code both use Range reads in production. However, the endpoint is slow and throttled: about four concurrent requests, 503 rate limiting, and roughly 70–130 MB/s reported. It cannot read the disk a running VM is writing to. Reading the base disk under a snapshot is plausible but not confirmed by a primary source for `/folder`. vSAN is reported broken for Proxmox's importer, and vVols has no flat file at all. It is best treated as a fallback or full-copy path, not the primary incremental data path.

### Cited Findings
**How Proxmox VE's importer (pve-esxi-import-tools / esxi-folder-fuse) uses it [Primary, source code]**
- The URL is built as `"{base_url}/folder" + "/{path}?dcPath={datacenter}&dsName={datastore}"`. The datacenter and datastore are percent-encoded; the path keeps its slashes. — [pve-esxi-import-tools src/esxi.rs](https://git.proxmox.com/?p=pve-esxi-import-tools.git;a=blob;f=src/esxi.rs;hb=HEAD)
- Reads use `Range: bytes=start-end`. The size comes from `Content-Range` or a `HEAD` request's `Content-Length`. HTTP 416 is mapped to EOF, and `content-type` must be `application/octet-stream`. — [esxi.rs](https://git.proxmox.com/?p=pve-esxi-import-tools.git;a=blob;f=src/esxi.rs;hb=HEAD)
- Auth sends HTTP Basic on every request and captures a `vmware_soap_session` cookie from responses. There is no SOAP login call. — [esxi.rs](https://git.proxmox.com/?p=pve-esxi-import-tools.git;a=blob;f=src/esxi.rs;hb=HEAD)
- Throttling handling:
  - An HTTP 503 is retried, up to 5 attempts in total, with a fixed 10 s sleep between them. The code then fails with "rate limited".
  - A `Semaphore::new(4)` caps concurrent requests. Code comment: "Hyper might open multiple concurrent connections, let's limit this specifically to avoid esxi from getting bombarded with requests."
  - Only one task retries at a time.
  — [esxi.rs](https://git.proxmox.com/?p=pve-esxi-import-tools.git;a=blob;f=src/esxi.rs;hb=HEAD)
- Per-file cache defaults are a 32 MiB page × 8 pages, set by `--cache-page-size` / `--cache-page-count`. The client advertises HTTP/2 (ALPN "h2"). — [src/main.rs](https://git.proxmox.com/?p=pve-esxi-import-tools.git;a=blob;f=src/main.rs;hb=HEAD). Note: the Proxmox wiki instead says "8 blocks of 128 MiB" read-ahead per disk, so the wrapper may override the binary's default — [Proxmox wiki](https://pve.proxmox.com/wiki/Migrate_to_Proxmox_VE)
- The repository's last commits are about 7 months old ("fix warnings with current rustc", "bump proxmox-fuse to 3"). I found no post-August-2026 change. — [pve-esxi-import-tools shortlog](https://git.proxmox.com/?p=pve-esxi-import-tools.git;a=shortlog)

**Proxmox documentation [Primary]**
- "The ESXi API has a relatively low limit on the number of available connections." Clients that hit it are blocked for about 30 s. esxi-folder-fuse caps parallel connections at four and serialises retries. — [Proxmox wiki: Migrate to Proxmox VE](https://pve.proxmox.com/wiki/Migrate_to_Proxmox_VE)
- For 503 errors the wiki suggests raising session limits. On ESXi 7.0, increase or set to 0 `Config.HostAgent.vmacore.soap.maxSessionCount`. On 6.5/6.7, set `<sessionTimeout>`/`<maxSessionCount>` to 0 in `/etc/vmware/hostd/config.xml`. — [Proxmox wiki](https://pve.proxmox.com/wiki/Migrate_to_Proxmox_VE)
- The source VM should be powered off. Import "can be significantly slower if the VM has snapshots".
- Through vCenter, performance is "dramatically" reduced.
- Limitations:
  - "Importing a VM with disks backed by a VMware vSAN storage does not work."
  - Encrypted disks cannot be imported.
  - Datastore names with "+" may fail.
  - Tested on ESXi 6.5–8.0.

  — [Proxmox wiki](https://pve.proxmox.com/wiki/Migrate_to_Proxmox_VE)

**Throughput reports [Forum, anecdotal]**
- Users report about 70–130 MB/s for the built-in importer (one: "about 110/130 MB a sec"). One theory is roughly 30 MB/s per HTTP call × 4 parallel calls; this is unconfirmed by Proxmox.
- A community fork that adds SSH+dd claimed about 2× over HTTP on 1 GbE.
- Importing from the host rather than vCenter is faster.

— [Proxmox forum: Import from ESXi Extremely Slow](https://forum.proxmox.com/threads/import-from-esxi-extremely-slow.173450/)

**Xen Orchestra's use of `/folder` [Primary, source code]**
- XO's ESXi client sends `Range: bytes=…` and checks for HTTP 206. If a host returns the whole file instead of the range, it raises `RANGE_IGNORED`, except for the harmless case of a small descriptor read from offset 0.
- It retries on 408/425/429/500/502/503/504 with backoff up to 30 s.
- Comment: "a vmdk descriptor is a small file, but the /folder endpoint of a host is not fast" (concurrency 4).

— [xen-orchestra @xen-orchestra/vmware-explorer/esxi.mjs](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/vmware-explorer/esxi.mjs)

**Locking: running VM, snapshots, and `/folder` vs NFC**
- [Primary] Broadcom KB 407282: downloading a VMDK of a powered-on VM fails with "Server returned HTTP response code: 500". The example URL is `…/external-vecs/http1/<vcsa>/443/folder/VM-NAME/VM-NAME-flat.vmdk`, i.e. proxied through vCenter. Cause: "The VMDK files are locked by the ESXi hosts when the virtual machine is powered on. This is expected behavior." The resolution is to consolidate snapshots, power off, and retry. The KB mentions downloading from a vSAN datastore, and it applies to all versions. It says nothing about the base disk under a snapshot. — [Broadcom KB 407282](https://knowledge.broadcom.com/external/article/407282/unable-to-download-vmdk-files-for-a-powe.html)
- [Forum, anecdotal] A community poster says taking a snapshot first leaves the base disk unlocked, so it can be downloaded. — [Broadcom community thread](https://community.broadcom.com/vmware-cloud-foundation/discussion/cannot-download-vmdk-files-from-vm-storage-expected-but-message-got-error)
- [Forum] A Vates team member explained that warm migration powers off the source "because the active disk is locked (unreadable) as long as the VM is running". In XO's older, pre-VDDK V2V, this meant reading the frozen disks under a pre-existing snapshot. — [XCP-ng forum thread](https://xcp-ng.org/forum/post/97675)
- [Primary] Vates' NFC client documentation, measured on ESXi 7.0.3/8.0.1:
  - "A base link under a snapshot of a running virtual machine: Yes, and the bytes are frozen for the duration."
  - "The link a running virtual machine writes to: No."
  - "A file the datastore browser is serving at the same time: No: `DiskLib error 16392: Failed to lock the file`. The reverse holds too: the datastore browser answers `500` while the disk is open over NFC."

  So `/folder` takes a file lock of its own while serving. — [vectura host-requirements-and-limits.md](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/vmware-explorer/vectura/docs/reference/host-requirements-and-limits.md)

**Session / authentication via vCenter without ESXi root credentials [Primary, govmomi code]**
- govmomi's `Datastore.ServiceTicket` uses `SessionManager.AcquireGenericServiceTicket` with a `SessionManagerHttpServiceRequestSpec` (URL and method such as `httpGet`/`httpPut`). It sends the ticket as cookie `vmware_cgi_ticket` directly to a chosen ESXi host's management IP, removes `dcPath` because a "VC datacenter path will not be valid against ESX", and pins the host thumbprint from the ticket. The feature is opt-in (`GOVMOMI_USE_SERVICE_TICKET`); otherwise downloads go through vCenter. — [govmomi object/datastore.go](https://github.com/vmware/govmomi/blob/main/object/datastore.go)
- [Primary] virt-v2v's vCenter HTTPS input (`-ic vpx://` without `-it`): "This is the slowest method." The guest "must be shut down", and "VMware vCenter appears to limit HTTP sessions" (`<maxSessionCount>` 0 = unlimited). — [virt-v2v-input-vmware(1)](https://libguestfs.org/virt-v2v-input-vmware.1.html)

### Inferences
- **Incremental via `/folder`.** For VMFS/NFS thick or thin disks, the base `-flat.vmdk` is a raw image. QueryChangedDiskAreas offsets are virtual-disk byte offsets, which map 1:1 onto Range offsets of the base flat file. So CBT extents can be fetched with Range GETs once a snapshot has frozen the base (this is not tested end-to-end by me). Two caveats:
  - If the VM already had snapshots before the backup snapshot, the frozen state is a chain (base plus one or more `-delta`/`-sesparse` files). The client must parse the delta formats itself; Proxmox and XO do this.
  - The backup snapshot's own redo log must never be read. It is the running VM's write target and is locked.
- **Throughput.** Expect roughly 100 MB/s-class throughput per host, about 4 parallel requests, and 503/rate-limit behaviour that may need hostd tuning on customer hosts. Customers will dislike that hostd tuning in a commercial product. Going through vCenter is worse. A service ticket from AcquireGenericServiceTicket to hit the ESXi host directly is the better pattern.
- **Interaction with other backup tools.** `/folder` locks the file it serves (vectura observation). A `/folder` read could therefore block or fail a concurrent NFC- or HotAdd-based backup by another product, and vice versa.
- **vSAN / vVols.** On vSAN, the "flat" file shown in the namespace is virtual. Proxmox reports vSAN import as non-working, while KB 407282 talks of downloading from a vSAN datastore once the VM is powered off. The two sources conflict, and running-VM reads on vSAN via `/folder` are unproven. vVols has no flat file in the datastore namespace (descriptor plus vVol objects), so `/folder` is very unlikely to work there. NFC and HotAdd go through the host's disk library and are the realistic options for vSAN/vVols.
- **Legal risk.** Lowest. This is a documented, long-standing HTTP interface used by VMware's own Apache-2.0 govmomi and by Broadcom KBs, and it needs no SDK binary.

### Gaps
- I did not fetch a current Broadcom TechDocs page that formally documents `/folder` (URL grammar, required privileges such as Datastore.Browse or Datastore.FileManagement, lockdown-mode behaviour) for vSphere 8.0 U3 or 9.x.
- No primary source confirms that `/folder` GET of the base `-flat.vmdk` succeeds while a snapshot exists on a *running* VM. The evidence is the forum anecdote, XO's design and the NFC lock analogy. This needs a lab test on 8.0 U3 and 9.x.
- I found no measured LAN throughput figures from a primary source, only forum anecdotes. I found no statement on whether ESXi 9.x changed `/folder` rate limits.
- vSAN/vVols behaviour of `/folder` for running VMs is unverified, and the sources conflict.

---

## 2. HttpNfcLease / ExportVm / ExportSnapshot (OVF export API): range reads or full streams? Usable for incremental backup? ImportVApp for restore

### Takeaway
HttpNfcLease is a public, documented API. ExportVm requires a powered-off VM, while ExportSnapshot (since API 5.5) works on a snapshot and lists no power-state fault, so it is usable for full backups of running VMs. I found no evidence that export disk URLs support HTTP Range. Uploads are stream-optimized VMDK streams. So this path is full-only, with no CBT-based incremental reads, and restore is whole-disk via ImportVApp.

### Cited Findings
- [Primary] HttpNfcLease "Represents a lease on a VirtualMachine or a VirtualApp, which can be used to import or export disks for the entity."
  - "While the lease is held, operations that alter the state of the virtual machines covered by the lease are blocked."
  - "The client must call HttpNfcLeaseProgress periodically to keep the lease alive … Failure to do so causes the lease to time out."
  - On an import, objects created are removed if the lease enters the error state.
  - Methods: HttpNfcLeaseAbort/Complete/GetManifest/Progress, HttpNfcLeasePullFromUrls_Task (since 6.7, "Upgrades current lease from push to pull mode", privilege VApp.PullFromUrls), HttpNfcLeaseProbeUrls (since 7.0.2.0), and HttpNfcLeaseSetManifestChecksumType (since 6.7).
  — [vSphere Web Services API: HttpNfcLease](https://developer.broadcom.com/xapis/vsphere-web-services-api/latest/vim.HttpNfcLease.html)
- [Primary] `ExportVm` "Obtains an export lease on this virtual machine". Privilege VApp.Export, since API 4.0. Fault **InvalidPowerState: "Thrown if the virtual machine is powered on."** — [vSphere API: VirtualMachine](https://developer.broadcom.com/xapis/vsphere-web-services-api/latest/vim.VirtualMachine.html#exportVm)
- [Primary] `ExportSnapshot` "Obtains an export lease on this snapshot. The export lease contains a list of URLs for the virtual disks for this snapshot, as well as a ticket giving access to the URLs." Privilege VApp.Export, **since vSphere API 5.5**. It lists InvalidState but **no InvalidPowerState fault**. — [vSphere API: VirtualMachineSnapshot](https://developer.broadcom.com/xapis/vsphere-web-services-api/latest/vim.vm.Snapshot.html)
- [Primary] An `HttpNfcLeaseDeviceUrl` carries a per-device URL, which may contain `*` in place of the host for the client to substitute, a `disk` flag, and an SSL thumbprint. — [VI-JSON HttpNfcLeaseDeviceUrl](https://developer.broadcom.com/xapis/virtual-infrastructure-json-api/latest/data-structures/HttpNfcLeaseDeviceUrl) (search snippet)
- [Primary, govmomi code] For import, govmomi uploads disk items with `POST` and `Content-Type: application/x-vnd.vmware-streamVmdk`, and non-disk files with `PUT` plus `Overwrite: t` ("ovftool does the same"). Download is a plain GET of the device URL, with no Range logic in the lease client. — [govmomi nfc/lease.go](https://github.com/vmware/govmomi/blob/main/nfc/lease.go)
- [Primary, govmomi code] vcsim simulates HttpNfcLease over HTTP (`ServeNFC` handles GET/PUT/POST, plus lease Complete/Abort/Progress/GetManifest). It is the HTTP lease mechanism, not the TCP-902 NFC wire protocol. — [govmomi simulator/http_nfc_lease.go](https://github.com/vmware/govmomi/blob/main/simulator/http_nfc_lease.go)
- [Primary] virt-v2v's OVA input requires an OVA exported from vSphere (for example with ovftool `vi://…`) and needs no VDDK. — [virt-v2v-input-vmware(1)](https://libguestfs.org/virt-v2v-input-vmware.1.html)

### Inferences
- **Full backups.** ExportSnapshot on the backup snapshot gives a VDDK-free, fully public-API full backup of a running VM. The output is a sequential, generated stream-optimized VMDK (compressed grains, zeros skipped). Unpacking it requires a stream-optimized VMDK parser in .NET; the format is public in VMware's VMDK specification.
- **Incrementals.** Because the stream is generated sequentially, Range requests are very unlikely to work, and none of the clients I inspected (govmomi, ovftool behaviour as described by govmomi) use them. Without random access, CBT extents cannot be fetched. Every run transfers the whole allocated disk, so this does not meet the incremental requirement.
- **Side effects.** The lease blocks state-changing operations on the VM while held. A long export therefore blocks snapshot removal, vMotion and similar operations for its duration.
- **Restore.** ImportVApp + HttpNfcLease (POST streamVmdk) creates a *new* VM with full disks. It cannot write changed blocks into an existing disk, so it is a full-VM-restore path only. PullFromUrls (6.7+) lets the ESXi host pull the disks from an HTTP server run by the backup product, which may simplify firewalling.
- **Legal risk.** Lowest: a public, documented API with no SDK binary.

### Gaps
- No primary source explicitly states whether export device URLs honour `Range`. A one-request lab test would settle it.
- I found no current throughput figures for ExportSnapshot on ESXi 8.0 U3/9.x. I also found no confirmation that ExportSnapshot works on vSAN/vVols or with encrypted VMs.

---

## 3. NFC protocol (TCP 902): public documentation, independent open-source implementations, and legal risk of a clean-room implementation

### Takeaway
There is no public wire specification. Broadcom documents NFC only as a transport with limits, and KB articles document the greeting lines. Since August 2026, however, three independent client implementations have been published, and they serve random-access reads, which is exactly what CBT incrementals need:
- **vectura** (Vates/Xen Orchestra, AGPL-3.0, Rust): "written from public sources only"; shipped in a commercial product.
- **OpenVixDiskLib** (Cloudbase, Python): reverse-engineered from VDDK traffic with TLS interception; supports read and write.
- **OpenVDDK** (Apache-2.0, Go): observation of VDDK's behaviour, including TLS plaintext captured in a lab.

govmomi/vcsim does **not** implement port-902 NFC. In the EU, protocol interoperability work has strong statutory protection that a contract cannot override. In the US, contract terms can override, so how the engineers learned the protocol matters a great deal.

### Cited Findings
**Documentation status**
- [Primary] The vectura authors write: "The client side of the protocol is written from public sources only: the vSphere API reference, Broadcom knowledge base articles, and measurements taken on ESXi 6.0, 7 and 8 hosts. No proprietary binary or header is opened, and no third-party reimplementation is read. It is slower to write; it is the only way the result can be shipped." — [vectura scope-and-design-choices.md](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/vmware-explorer/vectura/docs/explanation/scope-and-design-choices.md)
- [Primary] "The greeting lines and their capability words are documented in Broadcom knowledge base articles 343952, 338286, 341384 and 417531." The documented NFC session limits are "1 MB per stream" and a 48 MB NFC buffer per host (about 48 concurrent streams). — [vectura host-requirements-and-limits.md](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/vmware-explorer/vectura/docs/reference/host-requirements-and-limits.md)

**How NFC works, per vectura's documentation [Primary]**
- Management plane on port 443 (four SOAP calls): `RetrieveServiceContent`, `Login`, `RetrieveInternalContent` ("to reach the NFC service"), and `NfcGetVmFiles` with the VM moref. The last call returns a ticket, the port and the SHA-1 thumbprint; through vCenter it also names the ESXi host.
- Data plane on port 902: a clear-text `220` greeting listing capabilities (`NFCSSL supported`, `SHA256 supported`), then TLS. A ticket dialogue follows, then a second TLS handshake inside the first for `nfcssl`, then six fixed-size 264-byte handshake messages.
- Reads are at most 1 MiB, in 64 KiB chunks. Chunk compression is none/zlib/skipz.
- A snapshot chain is read through its top link, and "the host resolves the parents".

— [vectura how-a-disk-is-read.md](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/vmware-explorer/vectura/docs/explanation/how-a-disk-is-read.md); [host-requirements-and-limits.md](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/vmware-explorer/vectura/docs/reference/host-requirements-and-limits.md)

**Measured behaviour and throughput [Primary, vectura documentation]**
- A management session expires after 30–52 min without a management call, and data reads do not renew it. An idle open disk stays open at least 30 min.
- TLS 1.2 ECDHE AES-GCM is used on both ports, with the same certificate on 902 as on 443.
- One 16 GiB disk on ESXi 7.0.3 over a 61 ms RTT link with a 5.81 MB/s ceiling:
  - `skipz`, depth 16: 26.0 MiB/s served
  - `skipz`, depth 32: 36.5 MiB/s served
  - `none`: 5.3 MiB/s
  - "Not a LAN figure."
- ESXi 6.0: 3–5 MiB/s per process, one read in flight.

— [host-requirements-and-limits.md](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/vmware-explorer/vectura/docs/reference/host-requirements-and-limits.md)

**Independent implementations**
- **vectura (Vates).** [Primary] "Standalone client for VMware's NFC disk-read protocol", version 0.2.0, licence `AGPL-3.0-or-later`. It is a single Linux binary that serves one disk read-only as NBD on stdin/stdout. It targets ESXi 7.x/8.x (6.x in a legacy dialect), directly or via vCenter. Certificate verification cannot be skipped. "Why read-only: The use is backup, replication and inspection." — [vectura README](https://github.com/vatesfr/xen-orchestra/tree/master/@xen-orchestra/vmware-explorer/vectura), [Cargo.toml](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/vmware-explorer/vectura/Cargo.toml)
- **Cloudbase OpenVixDiskLib.** [Primary]
  - What it is: a Python replacement for VixDiskLib's NBD path over vSphere NFC, with ConnectEx/Open/Read/**Write** and pyVmomi for login.
  - Provenance: "The NFC ticket, ESXi authd handshake, and disk I/O were reverse-engineered from VDDK 8 NBD traffic". "AI tools (Cursor + Grok 4.6) have been heavily used to reverse engineer the NBD and NFC protocols". The docs include `ssl_hook.md` ("TLS intercept used for capture") and `reverse_engineering_procedure.md`.
  - Not implemented: CBT and allocated-block queries, encrypted disks, and direct ESXi `ha-nfc` without vCenter.
  - Tested on vSphere 8.0.1 only; 9 untested, 7 and older unsupported.
  - Repository: created 2026-09-04, last push 2026-10-08, 24 stars. GitHub reports the licence as "Other", and the LICENSE file text looks like git's own GPL/LGPL preamble. OpenVDDK's clean-room log describes it as LGPL-2.1. **The licence is therefore unclear.**

  Forks exist at hystax (created 2026-10-01) and petrutlucian94. — [cloudbase/OpenVixDiskLib](https://github.com/cloudbase/OpenVixDiskLib); [hystax fork](https://github.com/hystax/OpenVixDiskLib); [GitHub API metadata, read 2026-11-01]
- **OpenVDDK (Apache-2.0).** It has NBD/NBDSSL over NFC; see §4.
- **maci0 OpenVDDK (GPL-3.0).** It claimed a "Clean-room implementation of the VMware Network File Copy (NFC) protocol over TCP (port 902) and SSL/TLS (port 443)"; see §4. The original repository is gone.
- **nbdkit-nfc-plugin (Red Hat / libguestfs), announced but not published.**
  - [Primary] virt-v2v commit "Add -it nfc mode" (Richard W.M. Jones, 2026-08-24): "This uses nbdkit-nfc-plugin (a free golang reimplementation) to access the NFC protocol over port 902." — [virt-v2v commit cf46038](https://github.com/libguestfs/virt-v2v/commit/cf460384b229267b1d68b0e03543c7b457c0b7eb)
  - The virt-v2v man page now says: "If you don't have or don't want to use non-free software then the nfc, VMX or SSH methods above will be best." — [virt-v2v-input-vmware(1)](https://libguestfs.org/virt-v2v-input-vmware.1.html)
  - Issue #191 (opened 2026-09-02, still open, no replies) reports that the plugin is not in nbdkit's source tree, docs, releases or packages. — [virt-v2v issue #191](https://github.com/libguestfs/virt-v2v/issues/191)
  - The nbdkit `plugins/` tree on 2026-11-01 has no `nfc` directory. — [nbdkit plugins tree](https://gitlab.com/nbdkit/nbdkit/-/tree/master/plugins)
- **nbdkit deprecates its VDDK plugin.** [Primary] Commit "vddk: Deprecate this plugin" (Richard W.M. Jones, 2026-10-17): "The VDDK plugin is deprecated and will be removed in nbdkit 1.50. The VDDK library was withdrawn by VMware." — [nbdkit commit b8284518411a](https://gitlab.com/nbdkit/nbdkit/-/commit/b8284518411a)
- **govmomi/vcsim.** [Primary] govmomi's `nfc` package and vcsim implement the HTTP-based HttpNfcLease, not port-902 NFC; see §2. — [simulator/http_nfc_lease.go](https://github.com/vmware/govmomi/blob/main/simulator/http_nfc_lease.go)

**Legal framework (non-lawyer research, not legal advice)**
- [Primary, EU law] Directive 2009/24/EC:
  - Art. 5(3): a person with a right to use a program may "observe, study or test the functioning of the program in order to determine the ideas and principles which underlie any element of the program" while loading, running, transmitting or storing it.
  - Art. 6(1): decompilation is permitted where "indispensable to obtain the information necessary to achieve the interoperability of an independently created computer program with other programs". Conditions: (a) a licensee or other lawful user; (b) the information is not "previously readily available"; (c) the acts are confined to the necessary parts.
  - Art. 6(2): the information may not be used for other goals, given to others except as needed, or used for "a computer program substantially similar in its expression".
  - **Art. 8: "Any contractual provisions contrary to Article 6 or to the exceptions provided for in Article 5(2) and (3) shall be null and void."**
  — [Directive 2009/24/EC Art. 5](https://www.legislation.gov.uk/eudr/2009/24/article/5), [Art. 6](https://www.legislation.gov.uk/eudr/2009/24/article/6), [Art. 8](https://www.legislation.gov.uk/eudr/2009/24/article/8)
- [Secondary, case-law summaries] CJEU *SAS Institute v WPL* (C-406/10, 2012): functionality, programming languages and data file formats are not protected by copyright. Copying functionality by observing and testing, without decompiling, is not infringement. Licence terms cannot restrict the Art. 5(3) right while the licensee acts within the licence. — [Fieldfisher](https://www.fieldfisher.com/en/insights/sas-institute-inc-v-world-programming-limited); [SCL](https://scl.org/?p=2550)
- [Secondary] EU Trade Secrets Directive 2016/943 Art. 3(1)(b): acquisition by "observation, study, disassembly or testing" of a product is lawful only if the acquirer "is free from any legally valid duty to limit the acquisition". Commentators note that the final text "explicitly permits contractual limitations" on reverse engineering. — [legislation.gov.uk Art. 3](https://www.legislation.gov.uk/eudr/2016/943/article/3/data.htm); [Carpmaels](https://www.carpmaels.com/eu-trade-secrets-directive/)
- [Primary, US law] 17 U.S.C. §1201(f) lets a lawful user circumvent access controls "for the sole purpose of identifying and analyzing those elements of the program that are necessary to achieve interoperability of an independently created computer program". Tools and information may be shared "solely for the purpose of enabling interoperability". "Interoperability" means "the ability of computer programs to exchange information, and of such programs mutually to use the information which has been exchanged." — [17 U.S.C. §1201 (Cornell LII)](https://www.law.cornell.edu/uscode/text/17/1201)
- [Secondary] *Bowers v. Baystate* (Fed. Cir. 2003): copyright law does not preempt a shrink-wrap "no reverse engineering" clause, and breach of that clause was upheld. In the US, contracts can therefore remove the fair-use and reverse-engineering latitude. Cites vary (320 F.3d 1317 vs 302 F.3d 1334). — [FindLaw opinion](https://caselaw.findlaw.com/court/us-federal-circuit/1202776.html); [Wikipedia](https://en.wikipedia.org/wiki/Bowers_v._Baystate_Technologies,_Inc.)
- [Primary, via sibling note] Broadcom's SDK License §3 forbids reverse engineering "except as expressly permitted". It adds that the restrictions do not apply where they contradict mandatory local law, "including … law implementing the EC Software Directive". — [Broadcom SDK License Agreement](https://docs.broadcom.com/doc/vmware-software-development-kit-license-agreement) (quoted in vddk_and_transport_modes.md)

### Inferences
- **The protocol is clean-room implementable without touching VDDK.** Vates claims to have done exactly that, by observing ESXi's own server behaviour plus KBs and API references, and ships the result commercially. It is the strongest precedent for a C# implementation.
- **Three provenance tiers carry rising legal risk:**
  - (a) Talking to ESXi's NFC server and observing its responses: vectura's claimed approach. Here the "program" observed is ESXi, which the customer licenses. ESXi's own EULA terms were not reviewed here.
  - (b) Observing VDDK's network traffic or TLS plaintext while running it: OpenVDDK's and Cloudbase's approach. In the EU this is covered by Art. 5(3), and Art. 8 voids contrary contract terms. In the US the SDK licence's anti-reverse-engineering clause may be enforceable (*Bowers*). Intercepting TLS inside the VDDK process (a "TLS hook") is closer to the reverse-engineering line than passive observation.
  - (c) Decompiling VDDK: an EU Art. 6 defence exists only if the information is "not readily available". That is arguable, because vectura and Cloudbase have now published working protocol descriptions. Avoid this tier entirely.
- **Recommended clean-room process (inference).** A spec team writes protocol notes only from:
  - observation of ESXi servers the company licenses,
  - Broadcom KBs and the API reference,
  - possibly the published, permissively licensed or documentation-only third-party notes. The AGPL and LGPL code of vectura and Cloudbase must not be copied into a proprietary product.

  A separate implementation team writes the C# code from those notes. Record provenance per behaviour, as OpenVDDK and vectura do. Get counsel review in both the EU (DE: §§69d(3), 69e, 69g(2) UrhG implement Art. 5(3), 6 and 8) and the US.
- **Other risks besides copyright:**
  - Technical: Broadcom can change NFC in any ESXi release, and no implementation has been tested against 9.x.
  - Support: customers may get no Broadcom support for issues caused by an unofficial client.
  - The NFC ticket call (`NfcGetVmFiles` via `RetrieveInternalContent`) is an internal, non-public vSphere API.
  - Possible patents (not researched).
  - Commercial: Broadcom may treat NFC use outside VDDK as against partner terms if the vendor is or becomes a TAP member and has signed additional agreements.
- **Licence fit for a proprietary C# product:**
  - vectura (AGPL) runs as a separate process speaking NBD over pipes. Bundling an unmodified AGPL binary as a separate program is commonly treated as "aggregation", but it still carries source-offer obligations. Writing your own implementation is cleaner.
  - Cloudbase's licence is unclear.
  - OpenVDDK's Apache-2.0 licence is the only permissive option.

### Gaps
- I did not read Broadcom KBs 343952/338286/341384/417531 directly to see how much of the greeting and session they describe.
- I found no legal commentary specific to NFC or VMware. The analysis applies general EU and US doctrine.
- I did not review the ESXi EULA (Broadcom General Terms / VCF licence terms) for clauses on unofficial clients of hostd/NFC services.
- I found no published LAN throughput numbers for any VDDK-free NFC client, and no tests on ESXi 9.x.
- The nbdkit-nfc-plugin (Red Hat) is unpublished. Its licence, authorship and timeline are unknown.

---

## 4. OpenVDDK and similar "drop-in" VDDK replacements: maintainers, licence, maturity, legality

### Takeaway
Two unrelated projects use the name "OpenVDDK":
- **Andrija Panić's Apache-2.0 Go library** (github.com/openvddk/openvddk, v0.7.4, source published 2026-10-29). It is a drop-in `libvixDiskLib.so.8.0.3` with **read-only** HotAdd/NBDSSL/NBD, tested on 6.7, 7.0 and 8.0.3 on Linux x86-64. Its provenance documentation is detailed; it is beta and single-maintainer.
- **Marcel W. Wysocki's GPL-3.0 C project** (github.com/maci0/openvddk). Its claims were sweeping, and its repository now returns 404; a mirror survives.

Cloudbase's OpenVixDiskLib is a third, Python, read-write VixDiskLib-alike (see §3). None is production-proven for backup. Only the Apache-2.0 OpenVDDK is licence-compatible with a closed-source product, and it cannot restore.

### Cited Findings
**openvddk/openvddk (Apache-2.0) [Primary]**
- "an independent implementation of VMware's VixDiskLib, the virtual disk library of VDDK 8.0.3, for Linux x86-64. It is a drop-in replacement: it builds as `libvixDiskLib.so.8.0.3` … It reads disks; writing is unsupported by design."
- "It was written from public documentation, the facts any program needs to link against the library, and observation of what VMware's library visibly does when it runs. It contains no VMware code."
- Status:
  - Version 0.7.4, released 2026-10-29. The repository is read-only for now (no pull requests accepted).
  - Supports vSphere 6.7, 7.0 and 8.0 (tested 6.7 U3, 7.0.3, 8.0.3), via vCenter or a standalone ESXi host.
  - Transports `hotadd:nbdssl:nbd`; "HotAdd is used through vCenter".
  - Linux x86-64 with glibc 2.28 or newer.
  - Implements the 43 public functions of VixDiskLib 8.0.3.
- "Copyright 2026 Andrija Panić and the OpenVDDK contributors"; contact hello@openvddk.com; community project with no warranty.

— [openvddk/openvddk README](https://github.com/openvddk/openvddk); repository created 2026-10-29, owner org "openvddk", homepage [openvddk.com](https://openvddk.com) [GitHub API metadata]

- Per-function status: `VixDiskLib_Write`, `WriteAsync` and `WriteMetadata` are "UNSUPPORTED-BY-DESIGN". `QueryAllocatedBlocks` is "PARTIAL" over hotadd/nbdssl/nbd. `Create`, `CreateChild`, `Clone` and `Grow` are "NOT-YET-INVESTIGATED". — [COMPATIBILITY.md](https://github.com/openvddk/openvddk/blob/main/COMPATIBILITY.md)
- Provenance:
  - Uses VDDK 8.0.3 public C declarations ("facts only"), the reference library's ELF symbol and version tables, and black-box conformance tests against the reference library.
  - Uses lab observation including "TLS plaintext in an authorized lab".
  - Uses govmomi's `authorization_manager_internal.go` for the `DisableMethods`/`EnableMethods` request format; these are calls "outside the public WSDL".
  - "No proprietary object code and no decompiler output is used."
  - It compiles in govmomi v0.56.0 (Apache-2.0).

  — [PROVENANCE.md](https://github.com/openvddk/openvddk/blob/main/PROVENANCE.md)
- CLEAN-ROOM.md:
  - The reference library's "code is not read, disassembled or decompiled".
  - Evidence included "NFC traffic on port 902 — input for the paused NBD work".
  - Cloudbase's OpenVixDiskLib (described as "LGPL-2.1") was "studied from 2026-10-07 … as leads for the NFC subset of allocated-block queries, each confirmed by our own lab observation … read only, no code taken". Its authors "obtained the protocol from traffic, TLS plaintext and system calls".

  — [docs/CLEAN-ROOM.md](https://github.com/openvddk/openvddk/blob/main/docs/CLEAN-ROOM.md)
- Performance (vendor-published, single runs, ESXi/vCenter 8.0.3):
  - [Secondary] V2K Migrate, built on OpenVDDK:
    - 200 GiB Windows VM: 15 min 2 s with VDDK 8.0.3 vs 7 min 24 s with OpenVDDK.
    - Warm migration of a 20 GB VM: 5 min 30 s vs 1 min 58 s.
    - It calls OpenVDDK "feature-complete for HotAdd, NBDSSL and NBD", "private beta with test partners", and names Andrija Panić (Apache CloudStack PMC, ex-ShapeBlue) as author.

    — [v2kmigrate.com](https://v2kmigrate.com/)
  - [Secondary] cloudnews.tech, an earlier article: HotAdd-only, Linux x86-64, read-only. A 200 GiB disk was read at 2,488 MiB/s vs 1,441 MiB/s for VDDK. A hot migration took 118 s vs 330 s. — [cloudnews.tech](https://cloudnews.tech/openvddk-aims-to-speed-up-vmware-to-kvm-migrations-without-relying-on-broadcoms-library/)

**maci0/openvddk (GPL-3.0) [Primary via mirror]**
- Its claims: "a clean-room, fully open-source (GPL-3.0) implementation of the VMware Virtual Disk Development Kit", "Developed entirely from published open format and protocol specifications with zero proprietary or reverse-engineered code". Further claims:
  - "100% C ABI-compatible drop-in replacement" for libvixDiskLib and vixMntApi, compatible with Veeam.
  - Tested on ESXi 6.7, 7.0, 8.0 and "ESX 9.1".
  - Transports nbd, nbdssl, file, san and hotadd; CBT via `*-ctk.vmdk`.
  - Read **and write** (2.7 GB/s claimed).
  - A Forklift/MTV container image `quay.io/openvddk/openvddk:latest`.
- The last commits are by Marcel W. Wysocki, 2026-08-28 (v0.11.0) and 2026-08-30.

— [jimmyma-zhanwei/openvddk (mirror, created 2026-09-02)](https://github.com/jimmyma-zhanwei/openvddk)
- `https://github.com/maci0/openvddk` returns HTTP 404 (checked 2026-11-01) and the GitHub API says "Not Found". I found no DMCA notice in the github/dmca repository mentioning "openvddk". — [github.com/maci0/openvddk](https://github.com/maci0/openvddk); [GitHub search of github/dmca]
- [Secondary] byteiota described OpenVDDK (GPL-3.0) as "promising but not production-proven", with no vSAN coverage. — [byteiota](https://byteiota.com/broadcom-pulls-vmware-vddk-exit-door-bolted/)

**Cloudbase OpenVixDiskLib** — see §3. Python, NFC read and write, reverse-engineered from VDDK traffic, no CBT, vSphere 8 only, licence unclear. [Secondary] ModernOps says Cloudbase's Coriolis migration engine uses it to keep reading VMDKs without Broadcom's binary. — [ModernOps Field Notes No. 09 (Sept 2026)](https://modernops.com/field-notes/vmware-vddk-removed.html)

### Inferences
- **Apache-2.0 OpenVDDK.** It is the only permissively licensed drop-in. A .NET product could P/Invoke it exactly as it would VDDK: same ABI, on a Linux proxy only. It covers full and CBT-incremental *reads*, since CBT itself comes from the vSphere API and the library only needs random-access reads, which it provides. It does **not** cover restore. Its risks:
  - Single maintainer, v0.x, read-only repository.
  - No 9.x testing, no Windows build.
  - It depends on VDDK-observed behaviour, so it carries tier-(b) provenance in the §3 sense.
  - It uses non-public `DisableMethods`.
- **maci0 OpenVDDK.** Its disappearance shortly after publication and its implausible claims make it unsuitable as a dependency:
  - "published … protocol specifications" for NFC do not exist publicly;
  - it claims Veeam compatibility and multi-GB/s throughput.

  The reason for removal is unknown and should not be speculated on.
- **Using any of these** means taking on maintenance of a third party's VDDK emulation. Writing a targeted C# NFC/HotAdd client gives more control over provenance and support. The cost is more engineering.

### Gaps
- Why maci0/openvddk was removed: there is no public statement and no DMCA entry was found.
- I found no independent (non-author) benchmarks or production references for any drop-in replacement, and no backup vendor publicly named as a user.
- Whether Broadcom has responded legally or publicly to any replacement project was not found.

---

## 5. HotAdd without VDDK: proxy VM attaching snapshot disks via ReconfigVM_Task and reading them as local block devices

### Takeaway
This is feasible with public vSphere APIs and now has two open-source references. Xen Orchestra (September/October 2026) attaches existing VMDKs to a "satellite" VM via ReconfigVM in `independent_nonpersistent` mode. OpenVDDK implements full VDDK-style HotAdd in Go: proxy reconfigure, SCSI discovery, a transaction journal and crash recovery. VDDK's documented HotAdd constraints still apply: the proxy must be a VM, must see the datastore, and only SCSI disks work. It likely offers the best throughput and full CBT random access, and it works on vSAN. The hard parts are robustness: cleanup after crashes, blocking vMotion, and stopping the proxy OS from touching attached disks.

### Cited Findings
- [Primary, XO code] `attachDisk`: "Attaches an existing vmdk to a VM, powered on or not, without ever writing to it. The disk is attached in `independent_nonpersistent` mode: the guest sees a writable disk, but its writes go to a redo log next to the VM, dropped on detach or power off." Lock or reachability problems are left to the host: "the host refuses the reconfiguration and the task fails with its fault". The code picks the first free unit on the VM's SCSI controllers. — [xen-orchestra esxi.mjs](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/vmware-explorer/esxi.mjs)
- [Primary, XO code] "vSphere has no read-only disk mode: the writes of the guest go to a redo log, which is dropped when the disk is detached … an independent disk is left out of the snapshots of the VM", hence `READ_ONLY_DISK_MODE = 'independent_nonpersistent'`, using a `VirtualDiskFlatVer2BackingInfo` with the existing fileName. — [esxi.mjs](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/vmware-explorer/esxi.mjs)
- [Primary, XO code] `checkDiskAttachable`: "on any kind of datastore: local, shared or vSAN all come down to whether the host the VM runs on mounts the datastore … a disk of a snapshot or of a stopped VM can always be opened." — [esxi.mjs](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/vmware-explorer/esxi.mjs)
- [Primary] XO PR #10449, "feat(vmware-explorer): method to list/attach/detail disk to satellites", merged 2026-10-13. — [PR #10449](https://github.com/vatesfr/xen-orchestra/pull/10449)
- [Primary] OpenVDDK's layout: "HotAdd in `hotadd` (transaction, journal, recovery), `vsphere` (session, inventory, proxy reconfigure, access control), `linuxdev` (SCSI discovery, verified device I/O, host safety)". It ships an `openvddk-admin` "recovery utility" and a lab "proxy residue check". "HotAdd is used through vCenter." — [openvddk README](https://github.com/openvddk/openvddk)
- [Primary] OpenVDDK uses `DisableMethods`/`EnableMethods`. These are "vCenter access-control calls that are outside the public WSDL", whose format comes from govmomi's internal code. VDDK uses them to block operations such as vMotion during backup. — [PROVENANCE.md](https://github.com/openvddk/openvddk/blob/main/PROVENANCE.md); [CLEAN-ROOM.md](https://github.com/openvddk/openvddk/blob/main/docs/CLEAN-ROOM.md)
- [Primary, VDDK docs; from sibling note] "The HotAdd backup proxy must be a virtual machine." "The HotAdd proxy must have access to the same datastore as the target virtual machine". It "works only on virtual machines with SCSI disks and is not supported for backing up virtual IDE disks". It works with NFS-backed VMs. — [developer.broadcom.com VDDK Transport Methods](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkDataStruct.5.5.html)
- [Primary] The maci0 OpenVDDK README described "Native `san` and `hotadd` transport drivers with automated host SCSI bus rescanning (`/sys/class/scsi_host/host*/scan`)". — [mirror README](https://github.com/jimmyma-zhanwei/openvddk)
- [Secondary] OpenVDDK HotAdd read 2,488 MiB/s vs 1,441 MiB/s for VDDK on a 200 GiB disk (author's figure). — [cloudnews.tech](https://cloudnews.tech/openvddk-aims-to-speed-up-vmware-to-kvm-migrations-without-relying-on-broadcoms-library/)

### Inferences
**Public-API recipe (untested inference)**
1. Run CreateSnapshot_Task on the source VM.
2. Read `snapshot.config.hardware.device` to get each disk's frozen `backing.fileName`. For a VM with no earlier snapshots, this is the base VMDK.
3. Run ReconfigVM_Task on the proxy: add a VirtualDisk with that existing fileName (no `fileOperation`), `diskMode=independent_nonpersistent`, on a free SCSI slot.
4. Rescan SCSI in the proxy, then identify the device by its UUID or serial. Set `disk.EnableUUID` on the proxy.
5. Read the `QueryChangedDiskAreas` extents (with `snapshot` set to the backup snapshot) from `/dev/sdX`. In C#, use `FileStream`/`RandomAccess` on Linux with O_DIRECT, or `\\.\PhysicalDriveN` on Windows.
6. Run ReconfigVM_Task to remove the device *without* destroying the file.
7. Run RemoveSnapshot_Task.

**Design concerns**
- Choose between independent_nonpersistent (XO's approach, which creates a redo log in the proxy's directory) and whatever VDDK does. VDDK's exact attach mode was not researched.
- Proxy OS hygiene: on Windows, set SAN policy offline/read-only. On Linux, stop LVM, MD and udev auto-activation; OpenVDDK's "host safety" module exists for this reason.
- Crash recovery: keep a persistent journal and a residue sweep that detaches leftover disks.
- Concurrency limits: SCSI slots per proxy (4 controllers × 15 units).
- Use `DisableMethods` (internal API) or accept the risk of vMotion/Storage vMotion during backup.

**Coverage**
- Works on VMFS, NFS and vSAN (the host mounts the datastore). Likely works on vVols, but unverified.
- Does not work for IDE/NVMe disks: VDDK says SCSI only, and NVMe was not researched.
- Encrypted VMs are likely to need the proxy to be encrypted too (not researched).

**Legal risk**
- Low. ReconfigVM_Task, snapshots and CBT are public APIs, and reading a block device is ordinary OS I/O.
- The only grey element is `DisableMethods`/`EnableMethods`, which are outside the public WSDL. Avoiding them removes that grey element.

### Gaps
- I found no primary documentation of exactly how VDDK attaches disks for HotAdd (mode, whether it uses the base disk or a linked-clone child), and whether independent_nonpersistent attachment of a running VM's base disk is officially supported by Broadcom.
- vVols, NVMe controllers and encrypted VMs with VDDK-free HotAdd are unverified.
- XO's satellite-attach feature is new (October 2026). How XO uses it for reads (block device vs NFC) was not fully traced.

---

## 6. Restore paths without VDDK: datastore HTTP PUT, ImportVApp, HotAdd-style write

### Takeaway
VDDK-free restore is possible but asymmetric:
- **Full-disk restores** work through public APIs: `/folder` PUT of descriptor plus flat file, or ImportVApp + HttpNfcLease with stream-optimized POST.
- **Block-level and incremental restores** into an existing disk need random-access writes. The only VDDK-free options are a HotAdd-style *persistent* attach to a proxy VM (public API) or an NFC write implementation.

Of the open-source NFC/VDDK replacements, only Cloudbase's OpenVixDiskLib supports writes. vectura and the Apache-2.0 OpenVDDK are read-only by design.

### Cited Findings
- [Primary] govmomi supports datastore upload with service tickets: `Datastore.Upload`/`UploadFile` go through `uploadTicket` → `ServiceTicket(ctx, path, "PUT")` → `AcquireGenericServiceTicket` with method `httpPut`, then directly to the ESXi host. — [govmomi object/datastore.go](https://github.com/vmware/govmomi/blob/main/object/datastore.go)
- [Primary] For ImportVApp leases, govmomi uploads disks with `POST` and `application/x-vnd.vmware-streamVmdk`; non-disk items use `PUT` + `Overwrite: t`. — [govmomi nfc/lease.go](https://github.com/vmware/govmomi/blob/main/nfc/lease.go)
- [Primary] HttpNfcLeasePullFromUrls_Task (6.7+) "Upgrades current lease from push to pull mode", so the host can pull files from URLs, with privilege VApp.PullFromUrls. If an import lease errors, "all objects created during the import are removed". — [HttpNfcLease API](https://developer.broadcom.com/xapis/vsphere-web-services-api/latest/vim.HttpNfcLease.html)
- [Primary] Read-only implementations:
  - Apache-2.0 OpenVDDK: `VixDiskLib_Write` is "UNSUPPORTED-BY-DESIGN". — [COMPATIBILITY.md](https://github.com/openvddk/openvddk/blob/main/COMPATIBILITY.md)
  - vectura: "Writing to a virtual disk that a hypervisor owns is a different product with a different failure mode, and leaving the write path out means it cannot be reached by mistake". — [vectura scope-and-design-choices.md](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/vmware-explorer/vectura/docs/explanation/scope-and-design-choices.md)
- [Primary] Cloudbase OpenVixDiskLib implements `Write` in read-write mode over NFC (vSphere 8 only). — [cloudbase/OpenVixDiskLib](https://github.com/cloudbase/OpenVixDiskLib)
- [Primary] The XO `attachDisk` design shows that ReconfigVM can attach an existing VMDK to a running VM. XO deliberately uses a non-persistent mode, so writes are discarded. — [esxi.mjs](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/vmware-explorer/esxi.mjs)

### Inferences
- **Full VM restore, lowest risk.**
  - Option A: generate an OVF plus stream-optimized VMDKs in .NET, then ImportVApp and POST each disk; or use PullFromUrls from the backup server.
  - Option B: CreateVM_Task, then PUT a descriptor plus `-flat.vmdk` via `/folder` with a service ticket.

  `/folder` PUT has drawbacks: the session/rate limits from §1 apply, thin provisioning is probably lost (the full flat size is sent), and it is unclear on vSAN/vVols, which have no flat-file namespace. ImportVApp is the more portable choice for vSAN/vVols because the host creates the objects.
- **Incremental or in-place restore** (write back only changed blocks, or "instant"-style restores): create or attach the target VMDK to a proxy VM in *persistent* mode via ReconfigVM_Task, write the blocks to the block device, then detach and reattach to the target VM. All of this uses public APIs. An NFC write client is the network alternative, but it means more reverse-engineered surface and higher data-corruption risk.
- **File-level restore** can reuse the read path: mount the backup image on the backup server. Nothing VDDK-specific is needed.
- Whether `/folder` PUT accepts partial writes (Content-Range) is unknown. Do not rely on it.

### Gaps
- No primary documentation found for `/folder` PUT semantics: thin/thick outcome on VMFS, vSAN/vVols support, maximum size, Content-Range support, and required privileges.
- No ImportVApp throughput figures for 8.0 U3/9.x.
- No source on writing to vSAN/vVols disks through a HotAdd-attached persistent disk from a proxy (expected to work, unverified).

---

## 7. How existing products handle the data path; VDDK-free announcements after August 2026

### Takeaway
- **Migration and open-source tools moved fastest.** Proxmox has always used `/folder`. Xen Orchestra removed VDDK in September 2026 in favour of its own AGPL NFC client plus CBT. virt-v2v added an NFC mode whose plugin is not yet published, and nbdkit deprecated its VDDK plugin. Platform9 uses a proprietary VDDK-independent copy plus storage offload. Cloudbase and V2K Migrate use VixDiskLib replacements.
- **Large backup vendors keep VDDK.** Veeam, Commvault, Veritas and Rubrik are TAP partners and bundle VDDK.
- **No statements found** from Nakivo, Vinchin or Hornetsecurity/Altaro.
- **Xen Orchestra is the closest precedent** for a commercial vendor running a full VDDK-free data path with CBT.

### Cited Findings
**Proxmox VE** [Primary]
- The ESXi importer reads via the ESXi `/folder` HTTP API (FUSE, Range, four connections). The VM should be powered off, live-import is optional, and vSAN and encrypted disks are not supported (details in §1).
- No post-August-2026 code changes found.
- [Secondary] ModernOps summarises it as "no VDDK dependency".

— [Proxmox wiki](https://pve.proxmox.com/wiki/Migrate_to_Proxmox_VE); [esxi.rs](https://git.proxmox.com/?p=pve-esxi-import-tools.git;a=blob;f=src/esxi.rs;hb=HEAD); [ModernOps](https://modernops.com/field-notes/vmware-vddk-removed.html)

**Xen Orchestra / Vates (XCP-ng V2V)** [Primary]
- PR #10384 "feat(v2v): use CBT to discover allocated blocks instead of vddk", merged 2026-09-15. Changelog: "When a VM has Changed Block Tracking enabled on the source host, a migration now asks the host which blocks it has to read — the blocks a disk uses for a full transfer, the blocks written since the previous pass for a delta — instead of scanning the disk through the VDDK."
- PR #10421 "feat(v2v): remove vddk dependency", merged 2026-09-25. Changelog: "Migrations no longer need the VDDK library, `nbdkit` and `nbdinfo`. The disks are read through `vectura`, a single binary shipped with XO … The Broadcom download, the tar.gz upload and the two compile-from-source buttons are gone. ESXi 6.x hosts are no longer supported". A later entry re-added ESXi 6.x import, "read one request at a time", in PR #10590.
- PR #10637, merged 2026-10-27: CBT is now enabled automatically on the source, "so that only the used blocks are read, and only the changed ones for the delta". The source is soft-shutdown via VMware Tools where possible.
- PR #10449, merged 2026-10-13: attach and detach disks to "satellites" (HotAdd-style).
- `#queryChangedDiskAreas` takes `changeId` (`*` or the previous snapshot's changeId) and `snapshotId` ("mandatory when the VM is powered on").

— [XO CHANGELOG.md](https://github.com/vatesfr/xen-orchestra/blob/master/CHANGELOG.md); [CHANGELOG.unreleased.md](https://github.com/vatesfr/xen-orchestra/blob/master/CHANGELOG.unreleased.md); [PR #10421](https://github.com/vatesfr/xen-orchestra/pull/10421); [PR #10384](https://github.com/vatesfr/xen-orchestra/pull/10384); [esxi.mjs](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/vmware-explorer/esxi.mjs)

**virt-v2v / nbdkit (Red Hat upstream)** [Primary]
- `-it nfc` was added on 2026-08-24 via the "nbdkit-nfc-plugin (a free golang reimplementation)". The plugin is not published as of 2026-11-01.
- The nbdkit VDDK plugin was deprecated on 2026-10-17 and will be removed in 1.50.
- The SSH transport (`-i vmx -it ssh`) needs no VDDK, but "is not usable if the guest has snapshots" and the guest must be shut down. `-i vmx` on local or NFS storage works without VDDK.

— [virt-v2v commit](https://github.com/libguestfs/virt-v2v/commit/cf460384b229267b1d68b0e03543c7b457c0b7eb); [nbdkit commit](https://gitlab.com/nbdkit/nbdkit/-/commit/b8284518411a); [virt-v2v-input-vmware(1)](https://libguestfs.org/virt-v2v-input-vmware.1.html)

**Forklift / Red Hat MTV**
- [Primary, snippet-level] A Red Hat KB says VDDK images are no longer publicly downloadable and that Red Hat is "investigating alternatives". — [Red Hat KB 7146995](https://access.redhat.com/node/7146995)
- [Primary, search snippets only] Red Hat Jira tickets say running MTV without VDDK "is unrecommended and may result in significantly lower migration speeds". Storage-offload migrations do not use VDDK, though validation still blocked them in one open ticket. — [Red Hat Jira MTV project (MTV-1196/1308/1782/4061 returned by search)](https://issues.redhat.com/browse/MTV-1782)

**Platform9 vJailbreak** [Secondary, vendor blog, 2026-09-17]
- Three paths: VDDK/NBD; "Accelerated Copy", "a proprietary VDDK-independent transfer path" developed in March 2026 that scales with proxy appliances; and "Storage Assisted" on Pure, NetApp and Hitachi Vantara, claimed "up to 10x faster than network-based transfer".
- The mechanism is not disclosed.

— [Platform9 blog](https://platform9.com/blog/vmware-migration-without-vddk/)

**Cloudbase Coriolis / V2K Migrate / ShapeBlue / NetApp** [Secondary]
- Coriolis uses Cloudbase's open-source clean-room VixDiskLib replacement. NetApp Shift converts VMDKs at the ONTAP storage layer. Veeam and Commvault ship VDDK under Broadcom partner agreements. — [ModernOps](https://modernops.com/field-notes/vmware-vddk-removed.html)
- V2K Migrate uses OpenVDDK (Andrija Panić). — [v2kmigrate.com](https://v2kmigrate.com/)
- ShapeBlue's CloudStack "overlay method" needs shared NFS and CloudStack 4.22.1 or newer. "VMs on VMware vSAN currently have no VDDK-free migration path" (blog claim, predates the XO and NFC implementations). — [byteiota](https://byteiota.com/broadcom-pulls-vmware-vddk-exit-door-bolted/)

**Backup vendors**
- [Secondary] Veeam, Commvault, Veritas and Rubrik are Broadcom Technology Alliance Partners and bundle VDDK in their installers. VMware told *The Register* (10 Sep 2026): "Access to the VDDK is still available through select TAP for its licensed use case", and that VDDK "is not a customer entitlement". — [Integration Plumbers (2026-09-11)](https://www.integrationplumbers.io/resources/vddk-independent-migration-stack)
- [Secondary] Broadcom confirmed the change to TechTarget; ShapeBlue spotted the broken links on 25 Aug and Red Hat published a note on 27 Aug. — [TechTarget](https://www.techtarget.com/it-infrastructure/news/366648768/Broadcom-confirms-change-in-VMware-migration-tool-access)
- [Secondary] Integration Plumbers proposes in-guest agents (ReaR+Borg/restic, UrBackup, Bareos FD) as a VDDK-independent backup stack, with no hypervisor-level data path. — [Integration Plumbers](https://www.integrationplumbers.io/resources/vddk-independent-migration-stack)
- I found nothing (searches returned nothing relevant) on Nakivo, Vinchin, Hornetsecurity/Altaro or Storware announcing VDDK-free VMware data paths after August 2026.

### Inferences
**Overall comparison of VDDK-free data paths (inference from the findings above; not lab-tested)**

| Option | Full backup (running VM) | CBT incremental | Restore | Performance (indicative) | vSAN / vVols | Maturity | Legal/licensing risk |
|---|---|---|---|---|---|---|---|
| Datastore HTTP `/folder` + Range | Probably, by reading the frozen base and delta chain under a snapshot; parsing of deltas needed | Yes in principle (Range reads at CBT offsets) | Full-disk PUT only | Low: about 70–130 MB/s per host, 4 connections, 503 throttling | vSAN conflicting/doubtful; vVols unlikely | Used by Proxmox (cold) and older XO V2V | Lowest (public HTTP interface) |
| HttpNfcLease ExportSnapshot / ImportVApp | Yes (ExportSnapshot, API 5.5+) | No (sequential stream, Range unverified) | Full VM import (new VM) | Unknown; single stream, compressed | Likely, since the host handles storage (unverified) | Public API, long-standing | Lowest |
| HotAdd via ReconfigVM (own implementation) | Yes | Yes (random reads on a block device) | Yes, including block-level, with persistent attach | Highest (OpenVDDK claims about 2.5 GiB/s) | vSAN yes per XO; vVols likely | XO satellite attach (Oct 2026), OpenVDDK beta | Low; DisableMethods is internal |
| NFC client, own clean-room C# | Yes (base under snapshot can be opened) | Yes (random-access reads, at most 1 MiB, up to 32 in flight) | Only if a write path is built (none of the clean-room clients do) | Medium (no LAN data; skipz compression helps) | Yes, since the host disk library resolves storage (inference) | vectura shipped in XO; no 9.x tests | Medium: undocumented protocol, internal `NfcGetVmFiles`; EU protected, US contract risk depends on provenance |
| Apache-2.0 OpenVDDK (drop-in, P/Invoke) | Yes | Yes (reads) | No (read-only) | Claimed better than VDDK | Not stated | v0.7.4, single maintainer, Linux only | Medium: tier-(b) provenance; permissive licence |
| Cloudbase OpenVixDiskLib / AGPL vectura (reuse) | Yes | Cloudbase: no CBT helpers; vectura: yes | Cloudbase only | Unknown | Unknown | Young (Sept/Oct 2026) | Licence unclear (Cloudbase) or AGPL (vectura) |

- **Plausible product architecture:**
  1. A HotAdd-style proxy appliance (Linux) for performance, full and incremental backup, and block-level restore.
  2. A clean-room C# NFC reader as the network fallback for environments without a proxy VM per cluster, or ExportSnapshot as a fully public but full-only fallback.
  3. ImportVApp for full-VM restore where no proxy is available.

  Datastore HTTP is a weak third choice for backup, but useful for small files (.vmx, .nvram, descriptors).
- **XO as a precedent.** XO shows that a commercial vendor is willing to carry the clean-room NFC risk. It is also open source under AGPL and publishes its provenance, which a closed-source product cannot easily mirror. Evaluate with counsel whether to publish protocol notes and provenance logs.

### Gaps
- I found no statements from Nakivo, Vinchin, Hornetsecurity/Altaro or Storware, and no Blocks & Files or The Register coverage of VDDK-free backup approaches (rather than migration).
- The mechanism of Platform9's "Accelerated Copy" is undisclosed.
- No vendor has published independent throughput comparisons of VDDK-free paths on a LAN against VDDK NBDSSL/HotAdd on 8.0 U3/9.x.
- No implementation (vectura, OpenVDDK, Cloudbase, Proxmox) documents testing against vSphere 9.x. Only the vanished maci0 project claimed "ESX 9.1".
