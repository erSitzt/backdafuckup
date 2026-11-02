# References for XCP-ng Backup Foundation

There is no existing code in this repository. The references are external documentation, plus reference material in `research/` used only to understand behaviour.

## Official documentation (primary sources)

### XAPI (XenServer / XCP-ng toolstack API)
- **Location:**
  - https://xapi-project.github.io/xen-api/ (class reference)
  - https://xapi-project.github.io/new-docs/xen-api/wire-protocol/ (JSON-RPC)
- **Relevance:** the control plane for every hypervisor operation.
- **Key patterns:**
  - `POST /jsonrpc`
  - `session.login_with_password`
  - `Async.*` methods plus `task` / `event.from`
  - `VM.snapshot`
  - the VDI CBT methods
  - HTTP handlers: `/export_metadata`, `/import_raw_vdi`, `/import_metadata`

### XenServer changed block tracking / NBD developer guide
- **Location:** https://docs.xenserver.com/en-us/xenserver/developer/changed-block-tracking-guide/
- **Relevance:** the documented end-to-end flow for incremental backup and restore.
- **Key patterns:**
  - Enabling NBD: `network.add_purpose("nbd")`.
  - `VDI.get_nbd_info`, with TLS verification against the returned certificate.
  - `list_changed_blocks` returns a bitmap with one bit per 64 KiB block.
  - `data_destroy` turns a snapshot into `cbt_metadata`.
  - Limits: at most 16 NBD connections, and the client must not run in dom0.
  - Restore: `VDI.create`, then `import_raw_vdi`, then `import_metadata` with a VDI mapping.

### xs-cbt-samples (BSD-3-Clause)
- **Location:** https://github.com/xenserver/xs-cbt-samples
- **Relevance:** sample code for CBT backup and restore. Use it to verify our understanding, for example the bitmap bit order.

### NBD protocol specification (public domain)
- **Location:** https://github.com/NetworkBlockDevice/nbd/blob/master/doc/proto.md
- **Relevance:** the basis for our own NBD client.
- **Key patterns:** fixed-newstyle handshake, `NBD_OPT_STARTTLS`, `NBD_OPT_GO`, `NBD_CMD_READ`, simple replies.

### nbdkit / qemu-nbd
- **Location:** https://libguestfs.org/nbdkit.1.html, https://www.qemu.org/docs/master/tools/qemu-nbd.html
- **Relevance:** test servers for our NBD client in CI. The `pattern` plugin gives verifiable data, the `delay` and `error` filters simulate a slow or failing server, and both support TLS.

### VHD format specification (Microsoft Open Specification Promise)
- **Location:** https://www.microsoft.com/download/details.aspx?id=23850
- **Relevance:** not needed for this spec. Needed later for the sparse `format=vhd` restore stream and for the storage-snapshot path, where VHDs are read from LVM.

## Reference material in this repository (behaviour only, never shipped)

### Veeam Xen plugin configuration
- **Location:** `research/Xen/Service/appsettings.json`
- **Relevance:** proven defaults from a production product.
- **Key patterns:**
  - XAPI retries 10× at 10 s intervals, except on create calls.
  - Task polling every 2 s, with a 15 min timeout.
  - One backup job session at a time.
  - Restore runs parallel readers and writers with 4 MiB regions.
  - Finished sessions are cached for 31 days.
  - Veeam uses Linux worker VMs per pool with hot-added snapshot VDIs. We use NBD instead.
- **Rule:** read the configuration and text files only. Never decompile the binaries, never copy files from there, never commit them.

### HPE Alletra MP B10000 WS-APIv3 OpenAPI spec
- **Location:** `research/HPE-Alletra/ws-open-api-2.5.0.json`. The public copy is at developer.hpe.com/ws/api-spec; the reference array serves v3 2.5.50 under `/docs`.
- **Relevance:** the future storage-snapshot path. This spec covers it only through the `ISnapshotLifecycleHook` and `IDiskTransport` extension points.

### Research reports
- **Location:** `reports/VMware vSphere integration access.md`
- **Relevance:** background for the integration policy, which allows only public APIs.
