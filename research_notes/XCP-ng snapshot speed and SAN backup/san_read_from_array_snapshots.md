# Backup of XCP-ng VMs by reading LVHD SR LUNs from storage-array snapshots ("backup from storage snapshots" / "direct SAN access") — state as of November 2026

Scope: XCP-ng 8.3 pool, shared `lvmoiscsi` SRs (SMAPIv1 LVHD), HPE Alletra Storage MP B10000, own open-source backup product (only public/documented APIs and formats, open-source code allowed, nothing from commercial binaries). Research date: 2026-11-10.

Source-code evidence below comes from shallow clones made on 2026-11-10 of:
- `xcp-ng/sm` branch `3.2.12-8.3-cowutil-qcow2` (last commit 2026-09-25; the QCOW2-capable 8.3 branch, in which `LVHDSR.py` is renamed `LVMSR.py` and `lvhdutil.py` becomes `lvmcowutil.py`) and branch `3.2.12-8.3` (last commit 2026-06-26, still `LVHDSR.py`, no QCOW2),
- `xapi-project/sm` `master` (last commit 2026-08-19, upstream XenServer, code moved to `libs/sm/`),
- `xapi-project/blktap` `master` (last commit 2026-09-08),
- `LTRData/discutils` default branch (last commit 2026-11-08).

---

## 1. Has anyone implemented "backup from array snapshots / direct SAN" for XenServer/XCP-ng? (commercial products' public docs, StorageLink history)

### Takeaway
I found no current product that reads XenServer/XCP-ng LVHD LUNs from array snapshots on a proxy. Every product with public docs reads disk data through the hypervisor: XAPI export (XVA/VHD), NBD with CBT, or hot-adding snapshot VDIs to a proxy or worker VM on the pool. The only array-offload feature Citrix ever shipped was StorageLink / Integrated StorageLink, removed in XenServer 6.5. Even StorageLink was a different model: one array LUN per VDI, with array-side snapshots, not parsing LVHD from a snapshot.

### Cited Findings
- **Veeam (Plug-in for XCP-ng/XenServer, VBR 13.1).** Official help center: workers are Linux-based VMs that run on the Xen host and move data between the backup server and repositories. The plug-in reaches storage, networks and VMs through the pool coordinator. It requires B&R 13.1.0.411+ and XCP-ng 8.3+ — [Veeam: Solution Architecture (Xen)](https://helpcenter.veeam.com/docs/vbr/userguide/xen_infrastructure_components.html); [Veeam: Xen System Requirements](https://helpcenter.veeam.com/docs/vbr/userguide/xen_system_requirements.html). (The WebFetch of these pages returned only the navigation menu. These facts come from the search engine's rendering of the official pages.)
- Veeam workers move Xen data via hot-add, per two independent blog reviews of the beta. One review says "exotic" block storage such as Ceph needs an R&D appsettings key — [vladan.fr, beta review](https://www.vladan.fr/veeam-backup-and-replication-plug-in-for-xcp-ng-enters-public-beta-powering-up-xen-hypervisor-backups/); [StarWind blog](https://www.starwindsoftware.com/blog/veeam-xcp-ng-vmware-backup-alternative/).
- Veeam's XCP-ng 12.3.2 beta forum post lists the supported SR types (ext, nfs, lvm, lvmoiscsi, lvmofc, lvmohba) and says incremental backup uses CBT — [Veeam forum: Plug-in for XCP-ng 12.3.2 beta2](https://forums.veeam.com/rhv-olvm-sc-hypercore-xcp-ng-hpe-vme-f62/veeam-plug-in-for-xcp-ng-12-3-2-beta2-t100399.html).
- Veeam's "Storage System Snapshot Integration" (Backup from Storage Snapshots, including HPE Alletra MP B10000/Primera/3PAR) supports VMware vSphere as the only virtualization platform, per the search engine's summary of the current and 13.0.2 pages. I could not retrieve the verbatim sentence, because the page body is buried behind a ~960k-character menu — [Veeam: Storage System Snapshot Integration](https://helpcenter.veeam.com/docs/backup/storage/storage_integration.html); [Veeam 13.0.2 archive](https://helpcenter.veeam.com/archive/vbr/13.0.2/userguide/storage_integration.html). A third-party write-up based on B&R 11a likewise says BfSS works only for vSphere, not Hyper-V — [samuraj-cz.com](https://www.samuraj-cz.com/en/article/veeam-backup-replication-backup-from-storage-snapshot/).
- Veeam's Proxmox VE plug-in (the sibling architecture) uses "Hot-Add and NBD transport modes" for workers — [Veeam: PVE limitations](https://helpcenter.veeam.com/docs/vbr/userguide/pve_limitations.html).
- **Storware Backup & Recovery (ex-vProtect) 7.4, XCP-ng/Citrix.** Two strategies only:
  - "Single image" (XVA full plus VHD delta exported via the XenServer API).
  - "Changed-Block Tracking": full exported as RAW via the API; incrementals are "each disk queried for changed blocks and which are exported over NBD", with NBD on 10809/tcp.
  
  The XCP-ng page mentions no disk-attachment or SAN strategy — [Storware docs: XCP-ng](https://docs.storware.eu/backup-and-recovery-7.4/protecting-virtual-machines/virtual-machines/xcp-ng). Storware's known-issues page notes that NBD must be exposed by the pool master — [Storware known issues](https://storware.gitbook.io/storware-vprotect/known-software-issues-and-limitations).
- **Commvault (Citrix Xen VSA).**
  - The VSA is installed on a VM on a XenServer and serves as a proxy. Jobs go to a proxy that "has access to the VM storage repository".
  - "A Xen proxy can have a maximum of 16 disks attached, including VM disks included in the operation". This implies snapshot VDIs are hot-attached to the proxy VM.
  - Quiesced (VSS) snapshot first, falling back to a disk-only snapshot.
  - No IntelliSnap/array-snapshot mode is mentioned for Xen.
  
  Sources: [Commvault: Backups for Citrix Xen](https://documentation.commvault.com/11.42/commcell-console/backups_for_citrix_xen.html); [Commvault: Citrix Hypervisor (XenServer)](https://documentation.commvault.com/11.42/commcell-console/citrix_hypervisor_xenserver.html).
- **Xen Orchestra (Vates).** Delta backups use XAPI snapshots, with NBD+CBT when enabled and a fallback to VHD stream export. With CBT, the snapshot is removed via `VDI.data_destroy` (Vates co-founder on the forum) — [XCP-ng forum post](https://xcp-ng.org/forum/post/92295) (search summary); [XenServer CBT guide](https://docs.citrix.com/en-us/xenserver/8/developer/changed-block-tracking-guide/getting-started).
- **NAKIVO, HYCU, Bacula, Trilio, Alike/Quorum, Unitrends.** My searches found no public documentation of any SAN/array-snapshot path for XenServer/XCP-ng. For NAKIVO, only a forum remark that XCP-ng support was "planned" turned up — [XCP-ng forum](https://xcp-ng.org/forum/post/76378) (search summary). See Gaps.
- **Citrix StorageLink.**
  - The StorageLink Gateway originally ran in a Windows VM. In XenServer 6.0 (2011) it was integrated ("Integrated StorageLink", iSL) so that the hypervisor could use "the data replication, de-duplication, snapshot, and cloning features built into storage arrays" — [The Register, 2011-10-03](https://www.theregister.co.uk/2011/10/03/citrix_xenserver_6?page=2).
  - It was removed: "StorageLink technology has been removed from XenServer version 6.5 and higher… applications, code, or usage that depend on StorageLink technology do not function". Migration of VDIs off StorageLink SRs is via CTX141433 — [Citrix XenCenter 8.2: Storage upgrade](https://docs.citrix.com/en-us/xencenter/8-2/storage-upgrade).
  - A Citrix KB covers reattaching a forgotten iSL SR — [CTX135734](https://support.citrix.com/external/article/135734/how-to-reattach-a-forgotten-integrated-s.html).
- A generic precedent exists outside Xen: a patent covers "Backup using storage array LUN level snapshot" (VMware context) — [US8566542B1](https://patents.google.com/patent/US8566542).

### Inferences
- The proposed design would be, as far as public information shows, the first "backup from storage snapshot" implementation for XCP-ng LVHD. No vendor documentation exists to copy behaviour from, and no vendor support precedent covers it.
- Veeam supports BfSS for vSphere only, and its Xen plug-in uses worker VMs with hot-add. Even the vendor with the most mature array integrations chose not to do this for Xen. That is consistent with the effort and risk described in sections 3 and 6.
- StorageLink was array-offloaded VDI management: one array volume per VDI, snapshots by the array. Its removal in 6.5 (2015) shows Citrix abandoned per-array adapters. No official reason was found (see Gaps). Its likely closest modern equivalent is the SMAPIv3 vendor plugins on the Vates roadmap (see section 6).

### Gaps
- I found no official Citrix statement giving the reason StorageLink was removed in 6.5. The 6.2 deprecation notice was not retrieved either.
- Veeam's verbatim "vSphere only" sentence and the Xen worker transport description could not be read in full: help center pages return ~960k characters of navigation.
- NAKIVO, HYCU, Bacula, Trilio, Alike/Quorum and Unitrends: no primary documentation found on how they read XCP-ng disks. Absence of evidence is not proof that none reads SR LUNs directly, but none advertises it.

---

## 2. Is the LVHD on-disk layout documented, how stable has it been, and what is changing (QCOW2 on LVM, SMAPIv3)?

### Takeaway
Official docs describe LVHD only at a high level: one VHD inside one LV per VDI, thick-provisioned. The exact naming, journal and rename protocol exists only in the LGPL-2.1 `sm` source. It has been stable in substance since XenServer 5.5 (2009) through XCP-ng 8.3 and current upstream XenServer `sm`.

Since the QCOW2 GA on 2026-05-05, an `lvmoiscsi` SR can contain three image types side by side: `VHD-<uuid>` (VHD), `QCOW2-<uuid>` (QCOW2, automatically used for disks >2040 GiB) and `LV-<uuid>` (raw). A reader must handle VHD and QCOW2 chains, including mixed chains with raw parents. The `sm` code is being refactored (LVHD→"LVM"/"cowutil") and SMAPIv3 is on the roadmap, so the layout is an implementation detail, not a contract.

### Cited Findings
**Official documentation (high-level only)**
- XenServer 8 docs: for LVM-based VHD, "the underlying logical volume container is sized to the VDI's virtual size", and unused space on the copy-on-write instance is reclaimed at snapshot or clone. The local-storage page says "A VDI is implemented in VHD format in an LVM logical volume of the specified size" — [XenServer 8: Storage](https://docs.xenserver.com/en-us/xenserver/8/storage); [XenServer 8: Local storage](https://docs.xenserver.com/en-us/xenserver/8/storage/local).
- XenServer 9 docs: "Logical volume-based VHD on a LUN is the default XenServer block-based storage… VDIs are represented as volumes within the volume manager and stored in VHD format to allow thin provisioning of reference nodes on snapshot and clone" — [XenServer 9: Storage](https://docs.xenserver.com/en-us/xenserver/9/storage).
- XenCenter docs: "since XenServer 5.5, LVM storage repositories have used Microsoft's VHD technology", and the LVM→LVHD upgrade cannot be undone — [XenCenter: Storage upgrade](https://docs.xenserver.com/en-us/xencenter/current-release/storage-upgrade.html).
- Vates blog (2022-06-02): SMAPIv1 "is really managing everything regarding your data", writes VHD "inside the logical volume", is thick on block storage (thin LVM would need cLVM-like locking), and is limited to 2 TiB by VHD's 32-bit sector offsets. It gives no VG/LV naming — [XCP-ng blog: Understanding the storage stack](https://xcp-ng.org/blog/2022/06/02/understanding-the-storage-stack-in-xcp-ng/).
- The `sm` README contains no layout or design documentation — [xapi-project/sm README](https://github.com/xapi-project/sm/blob/master/README.md).

**Open-source code (the only precise description of the layout)**
- **Naming.** `VG_PREFIX = "VG_XenStorage-"` and VG location `/dev` — [constants.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/constants.py). `LV_PREFIX = {RAW: "LV-", VHD: "VHD-", QCOW2: "QCOW2-"}` — [lvmcowutil.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/lvmcowutil.py). SR metadata LV `MDVOLUME_NAME = "MGT"`, created with 4 MiB — [LVMSR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/LVMSR.py).
- **CBT logs** are LVs named `"%s.%s" % (vdi_uuid, CBTLOG_TAG)` with `CBTLOG_TAG = "cbtlog"`, i.e. `<uuid>.cbtlog`. CBT block size is 64 KiB — [LVMSR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/LVMSR.py); [constants.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/constants.py).
- **Journals** are small (4 MiB) LVs tagged `journaler`, named `<type>_<id>_<value>`. The types are:
  - `clone` and `leaf` — [journaler.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/journaler.py)
  - `relink`, `coalesce` and `zero` — [cleanup.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/cleanup.py)
  - `inflate` — [lvmcowutil.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/lvmcowutil.py)
- **Temporary rename prefix.** During leaf-coalesce the leaf LV is temporarily renamed with `TMP_RENAME_PREFIX = "OLD_"` — [cleanup.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/cleanup.py).
- **PV and VG creation.** The PV is created on the whole LUN device: `pvcreate -ff -y --metadatasize 10M <dev>`, then `vgcreate --metadatasize 10M`. Code also exists to "add any additional devs into the VG", so a VG can span several devices. The pool master uses a separate LVM config dir `/etc/lvm/master`; slaves use `/etc/lvm` — [lvutil.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/lvutil.py).
- **XAPI snapshot of an LVHD VDI.** The original LV is renamed to a new base UUID ("self becomes the 'base vdi'", label "base copy", `read_only = True`). New leaf LVs are then created for the original VDI UUID and for the snapshot UUID, with a `clone` journal around the operation — [LVMSR.py, `_snapshot`](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/LVMSR.py).
- **VHD parent links** are set by `vhd-util modify -p <parentPath> -n <path>` (`-m` for a raw parent) — [vhdutil.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/vhdutil.py). **QCOW2 parent links** are set by `qemu-img rebase -u -f qcow2 -F <qcow2|raw> -b <parentPath>`. QCOW2 helpers use `/usr/bin/qemu-img` and `/opt/xensource/libexec/qcow2_helper` — [qcow2util.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/qcow2util.py).
- **Licences.** `sm` (both xapi-project and xcp-ng) is LGPL-2.1 — [xapi-project/sm LICENSE](https://github.com/xapi-project/sm/blob/master/LICENSE).

**Stability across versions**
- Upstream XenServer `sm` (master, Aug 2026, restructured into `libs/sm/`) still has `LVHDSR`, `JRN_LEAF = "leaf"` and `TMP_RENAME_PREFIX = "OLD_"`. The semantics are the same as XCP-ng's — [xapi-project/sm cleanup.py](https://github.com/xapi-project/sm/blob/master/libs/sm/cleanup.py).
- XCP-ng's QCOW2 branch renames the driver modules (`LVHDSR`→`LVMSR`, `lvhdutil`→`lvmcowutil`, a `cowutil` abstraction) but keeps the VG/LV prefixes and adds `QCOW2-` — [xcp-ng/sm branch 3.2.12-8.3-cowutil-qcow2](https://github.com/xcp-ng/sm/tree/3.2.12-8.3-cowutil-qcow2/drivers).

**QCOW2 on LVM (XCP-ng 8.3)**
- "Support for QCOW2 was added to XCP-ng on 2026-05-05 via its regular updates". Supported SR types are "EXT, NFS, LVM, LVMoISCSI, LVMoHBA, …" (not XOSTOR, not SMB).
- "A SR can have QCOW2 and VHD co-existing". VHD stays the default, and a QCOW2 disk is created automatically when a VDI >2040 GiB is requested. The limit is 16,381 GiB (up to 64 TiB is possible later). Default `preferred-image-formats` is `vhd,qcow2`.
- Sources: [XCP-ng docs: QCOW2 FAQ](https://docs.xcp-ng.org/storage/qcow2_faq/); [XCP-ng forum: RC2 for QCOW2](https://xcp-ng.org/forum/topic/12070/second-and-final-release-candidate-for-qcow2-image-format-support); [XCP-ng forum: removing the 2TiB limit](https://xcp-ng.org/forum/topic/10308/dedicated-thread-removing-the-2tib-limit-with-qcow2-volumes).
- Vates roadmap:
  - Done: "QCOW2 GA in XCP-ng 8.3 (May26)".
  - NOW: "Live Coalesce", "Live VDI Resize", "Performance improvement for XCP-ng 8.3 (tapdisk)", "XCP-ng 9 modern platform development".
  - NEXT: "SMAPIv3 Design", "Trim/discard support QCOW2".
  - LATER: "PureStorage integration support", "NetApp integration support".
  
  The roadmap is not a commitment — [Vates product roadmap](https://docs.vates.tech/product-roadmap/).

**SMAPIv3**
- SMAPIv3 splits the volume plugin from the datapath plugin. Its datapath can be raw, VHD or qcow2. Vates intends to use storage vendors' APIs "like snapshots" — [XCP-ng blog 2022](https://xcp-ng.org/blog/2022/06/02/understanding-the-storage-stack-in-xcp-ng/).
- The first SMAPIv3 driver (ZFS) was a preview in April 2024 — [XCP-ng blog 2024-04-19](https://xcp-ng.org/blog/2024/04/19/first-smapiv3-driver-is-available-in-preview/).

### Inferences
- **Two-tier documentation.** The public docs say only "VHD inside a thick LV per VDI". Everything a reader needs is defined only by LGPL code: names, journal LVs, the rename protocol, the hidden flag, parent links, QCOW2 LV prefix and CBT log LVs. Under the product policy ("public, documented APIs and formats; open-source code is fine") this is permissible, since it is open source. But it is not a supported interface, and it can change in any `sm` update without notice. The QCOW2 refactor in 2026 is a live example: module renames, a new prefix, new on-host coalesce paths.
- The core identifiers (`VG_XenStorage-`, `VHD-`, `LV-`, `MGT`, `OLD_`, journal types) have been stable for ~15 years and are shared with upstream XenServer. A reader built on them would probably keep working on 8.3, but it needs a version gate and self-checks.
- **Formats a reader must handle on 8.3:**
  - VHD dynamic/differencing (with blktap extensions, see section 4)
  - QCOW2 v3 with backing files, possibly with subclusters (an `xcp-ng/sm` branch `abs-qcow2util-support-subclusters` exists) or compression (branch `eva/qcow2-helper-compression`) in future
  - raw `LV-` volumes
  - mixed chains (QCOW2 child on raw parent is allowed by `qemu-img rebase -F raw`)
- SMAPIv3 SRs (future) will not use this layout at all, so the approach would have to be re-done per SMAPIv3 driver.

### Gaps
- No official on-disk layout specification of LVHD exists on docs.xenserver.com, docs.xcp-ng.org or xapi-project.github.io; I found none.
- I could not confirm from the shipped RPM which `xcp-ng/sm` branch is in the production 8.3 QCOW2 update. The `3.2.12-8.3` branch (June 2026) has no QCOW2 code, while `3.2.12-8.3-cowutil-qcow2` (Sept 2026) does.
- CBT support for QCOW2 VDIs is not documented in the QCOW2 FAQ. The code has a comment about bigger CBT LVs "with big QCOW2", suggesting it is supported.

---

## 3. Consistency risks when reading a crash-consistent array snapshot of a live LVHD SR

### Takeaway
LVHD is designed to be crash-safe on the host. LVM commits metadata atomically, and the `sm` GC and SR operations use journal LVs so that `cleanup.py` can undo or finish interrupted operations on the next run. An array snapshot is a frozen "power-loss" image of that state, so it may contain half-finished GC, clone, inflate or rename operations.

A read-only reader cannot run the recovery, so it must detect and virtually apply the `sm` recovery rules, or abort and retry. With a short XAPI snapshot taken before the array snapshot, the data to back up sits in a read-only base VHD plus an empty snapshot leaf. That greatly reduces exposure to tapdisk writes, though not to GC on older ancestors. With a VSS-only variant (no XAPI snapshot), the reader must parse an actively written leaf. The trailing footer of that leaf is deliberately invalid while it is open, CBT cannot be used, and leaf-coalesce/rename races are not excluded.

### Cited Findings
- **LVM metadata is atomic.** The metadata area is a ring buffer: "New metadata is appended to the old metadata and then the pointer to the start of it is updated" (write new version, then update pointer), and each copy carries `seqno` — [libvslvm: LVM format documentation (J. Metz, GFDL-1.3)](https://github.com/libyal/libvslvm/blob/main/documentation/Logical%20Volume%20Manager%20(LVM)%20format.asciidoc).
- **Leaf-coalesce** (`_doCoalesceLeaf`, "Must be called in an offline/atomic context"), in order:
  1. creates journal `leaf_<child>_<parent>`;
  2. coalesces the leaf into its parent, either offline or on the host via tapdisk (`_coalesceCowImageOnHost`) when the leaf is attached elsewhere;
  3. renames the leaf LV to `OLD_<child>`;
  4. renames the parent LV to `<child>`;
  5. updates the VDI records;
  6. deletes the old leaf only if space is needed;
  7. removes the journal.
  
  — [cleanup.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/cleanup.py)
- **Recovery of an interrupted leaf-coalesce** (`LVMSR._handleInterruptedCoalesceLeaf`): for each `leaf` journal, if an LV with the parent's UUID or the child's UUID still exists (any prefix), it runs UNDO. UNDO renames parent and child back, then deflates the parent, fully inflates the child, resets the hidden flags and sets the parent LV read-only. Otherwise it runs FINISH: inflate the child fully and forget the parent — [cleanup.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/cleanup.py).
- **Non-leaf coalesce:**
  1. creates a `coalesce` journal;
  2. coalesces the image, offline or "online" on the host where the leaf is attached (`_doCoalesceOnHost`);
  3. removes the journal;
  4. creates a `relink` journal, relinks the children under the SR lock, removes the `relink` journal;
  5. deletes the coalesced VDI.
  
  On restart, an existing `relink` journal means "Coalesce apparently already done: skipping" — [cleanup.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/cleanup.py).
- **Inflate** creates journal `inflate_<vdi>_<oldLVsize>`, extends the LV, zeroes the region where the new footer will go, sets the image's physical size (moving the footer), then removes the journal. **Deflate** has "no undo necessary": it sets the physical size first, then shrinks the LV — [lvmcowutil.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/lvmcowutil.py).
- **XAPI snapshot/clone** renames the VDI's LV to a fresh base UUID under a `clone_<orig>_<base>_<clone>` journal — [LVMSR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/LVMSR.py).
- **tapdisk footer-kill.** When opening a VHD read-write in "strict" mode, tapdisk overwrites the last 512 bytes (the primary footer) with 0xC7 bytes (`vhd_kill_footer`). It rewrites the footer on close only "if we killed it on open… or we've written data". On every resume after pause, which happens around XAPI snapshot operations, tapdisk re-opens with `TD_OPEN_STRICT` — [blktap block-vhd.c](https://github.com/xapi-project/blktap/blob/master/drivers/block-vhd.c); [blktap tapdisk-vbd.c](https://github.com/xapi-project/blktap/blob/master/drivers/tapdisk-vbd.c).
- **tapdisk block allocation** is transactional. `update_bat` locks the BAT, reserves the new block, writes a zeroed bitmap (`schedule_zero_bm_write`) and only then flags the BAT update — [blktap block-vhd.c](https://github.com/xapi-project/blktap/blob/master/drivers/block-vhd.c).
- **CBT and crashes.** If a host or SR crashes, XenServer disables CBT for all VDIs on that host or SR, and a new full baseline is required — [XenServer CBT guide (search summary)](https://docs.xenserver.com/en-us/xenserver/developer/changed-block-tracking-guide/troubleshoot).
- **list_changed_blocks.** The newer VDI "cannot be attached to a VM at the time this comparison is made". Both snapshots must be taken after CBT was enabled and belong to "the same set of incremental backups". The bitmap is base64, 1 bit per 64 kB — [XenServer CBT: list changed blocks](https://docs.xenserver.com/en-us/xenserver/developer/changed-block-tracking-guide/list-changed-blocks).
- **data_destroy** deletes the data of a snapshot and keeps the CBT metadata; the VDI type becomes `cbt_metadata`. It fails with VDI_MISSING while the snapshot is still attached to a VM snapshot through a VBD, so the VM snapshot must be removed first — [XenServer CBT: deleting snapshot](https://docs.citrix.com/en-us/xenserver/8/developer/changed-block-tracking-guide/deleting-snapshot).
- Vates roadmap lists "Live Coalesce" as in progress (NOW) — [Vates roadmap](https://docs.vates.tech/product-roadmap/).

### Inferences
**Recommended sequence and why it is mostly safe**

The sequence: XAPI `VM.snapshot` (optionally quiesced via the guest agent / VSS) → array snapshot → destroy the VM snapshot record and `VDI.data_destroy` the snapshot VDIs → back up later from the array copy.

- At array-snapshot time, the backup data lives in the snapshot leaf `VHD-<snapUuid>`, which is nearly empty and not opened by any tapdisk, plus `VHD-<baseUuid>`, renamed read-only seconds earlier, plus older ancestors. tapdisk only writes the VM's new leaf. So the footer-kill and in-flight BAT updates do not affect the chain being read.
- The remaining exposure is GC still running on older ancestors from previous cycles, or a parallel snapshot/clone of another VM on the same SR. Both are journaled. Non-leaf coalesce is content-preserving as seen through the child, because child blocks override the parent while it is being filled. The rename steps are recoverable using the journal LVs, which are present in the array snapshot.
- The `relink` window is benign: both old and new parent LVs exist until the journal is removed.
- An interrupted `inflate` means the trailing footer may be zero, so the reader must use the footer copy at offset 0.
- The torn-write risk on the 1024-byte VHD dynamic header during relink, if the array does not guarantee multi-sector atomicity, is inferred, not documented.

**CBT and the VSS-only variant**
- CBT requires XAPI snapshots, because `list_changed_blocks` compares snapshot VDIs and the newer one must not be attached. The "VSS freeze only" variant therefore cannot use CBT. It would need full reads every time, or its own diffing such as hash comparison or a stored VHD BAT comparison.
- The VSS-only variant also reads the live leaf, whose trailing footer is overwritten with 0xC7 and whose last BAT updates may be in flight. It would be crash-consistent at best, and guest-consistent only if the VSS freeze window covers the array snapshot.

**What a read-only reader must replay or detect**
- Pick the LVM metadata copy with the highest valid `seqno`, with checksums verified.
- List journal LVs (tag `journaler`, names `<type>_<id>_<val>`). If any journal references a VDI in the chain being read, either apply `sm`'s UNDO/FINISH decision rules virtually (map names without writing) or abort and retry the cycle later. Retrying is simpler and safer.
- Resolve `OLD_` LVs.
- Identify each image by its on-disk UUID (VHD footer UUID / QCOW2 header), and follow parents by parent UUID in the VHD header (`prt_uuid`) rather than only by name. Verify that each child's `prt_uuid` equals the parent's footer UUID; tapdisk itself checks this.
- Use the VHD footer copy at offset 0, never the trailing one.
- Cross-check against XAPI facts captured just before the array snapshot: the snapshot VDI UUID, its `sm-config:vhd-parent`, virtual size, and the list of VDIs/LVs. Fail closed on any mismatch.
- Detect a SR spanning multiple PVs (VG with more than one PV). It needs an array consistency-group snapshot of all LUNs.
- After a host/SR crash CBT is reset, so detect `cbt_enabled=false` or a missing previous `cbt_metadata` VDI and force a full backup.

### Gaps
- No Citrix/Vates documentation describes the crash-consistency guarantees of LVHD as a whole, or the write ordering of `vhd-util coalesce` (data versus BAT updates in the parent). The safety argument for reading mid-coalesce parents is my inference from code structure.
- There is no documented XAPI call to check or pause the SR garbage collector before an array snapshot. Internally, `sm` uses a lock type `gc_active` and a `gc_running` file on the master.
- Whether HPE Alletra MP B10000 snapshots preserve write ordering across multipath paths from several pool hosts was not investigated. A single-LUN snapshot is point-in-time by definition, but this is not verified from HPE docs.

---

## 4. Reading VHD, QCOW2 and LVM2: public specs and open-source readers usable from .NET

### Takeaway
All three formats have public specifications.
- **VHD:** Microsoft specification under the Open Specification Promise since October 2006.
- **QCOW2:** QEMU's interoperability spec (GPL-2 manual).
- **LVM2:** no official on-disk spec from sourceware. The best public description is Joachim Metz's libvslvm documentation (GFDL), plus the LVM2 source (GPL/LGPL).

For .NET, LTRData.DiscUtils (MIT) has pure-managed `DiscUtils.Lvm` (LVM2 PV/VG/LV, linear and striped segments, metadata CRCs) and `DiscUtils.Vhd` (dynamic and differencing VHD with parent locators). It has no QCOW2 support. libvhd/vhd-util (blktap, BSD-3-Clause) is the reference for Xen's VHD extensions.

### Cited Findings
- **VHD.** Microsoft added the VHD Image Format specification to the Open Specification Promise in October 2006 — [Microsoft blog: VHD released under OSP](https://learn.microsoft.com/en-us/archive/blogs/virtual_pc_guy/virtual-hard-disk-format-released-under-open-specification-promise); [ADTmag 2006-10-23](https://adtmag.com/Articles/2006/10/23/Microsoft-Adds-VHD-Format-to-List-of-LicenseFree-Technologies.aspx); [Microsoft blog: VHD spec freely available](https://learn.microsoft.com/en-us/archive/blogs/volkerw/vhd-spec-now-freely-available). (The VHDX spec is separately under OSP — [Microsoft Download 34750](https://www.microsoft.com/en-pk/download/details.aspx?id=34750).)
- **Xen extensions to VHD.** blktap's footer struct uses one reserved byte after "saved" as `hidden` ("tapdisk-specific field: is this vdi hidden?"). libvhd also implements a "batmap" extension, identified by `VHD_BATMAP_COOKIE` — [blktap include/vhd.h](https://github.com/xapi-project/blktap/blob/master/include/vhd.h); [blktap vhd/lib/libvhd.c](https://github.com/xapi-project/blktap/blob/master/vhd/lib/libvhd.c).
- **libvhd parent locators.** libvhd writes parent locators with platform codes MacX, W2ku and W2ru, plus the header `prt_name` (unicode parent name). Relative paths are computed via `relative_path_to` — [libvhd.c](https://github.com/xapi-project/blktap/blob/master/vhd/lib/libvhd.c).
- **blktap licence.** "All files in this package (barring exceptions noted below) are licensed under the following 3-clause BSD license", copyright Citrix 2016 — [blktap LICENSE](https://github.com/xapi-project/blktap/blob/master/LICENSE).
- **QCOW2 spec.** Header with backing file offset/size (name ≤1023 bytes, not NUL-terminated), v2/v3 headers, and incompatible feature bits (dirty, corrupt, external data file, compression type, extended L2/subclusters). "An implementation must fail to open an image if an unknown bit is set." Compression is deflate or zstd. Licence notice: "QEMU and this manual are released under the GNU General Public License, version 2" — [QEMU docs: qcow2 format](https://www.qemu.org/docs/master/interop/qcow2.html).
- **LVM2 on-disk format** (libvslvm document by Joachim Metz, GFDL-1.3): label "LABELONE" in one of the first four sectors (normally the second); PV header with data and metadata area descriptors; metadata area header with up to 4 raw location descriptors (offset, size, CRC-32, flag RAW_LOCN_IGNORED); ASCII text metadata with VG/PV/LV/segment sections; `seqno` — [libvslvm LVM format doc](https://github.com/libyal/libvslvm/blob/main/documentation/Logical%20Volume%20Manager%20(LVM)%20format.asciidoc).
- **LTRData.DiscUtils:**
  - "a .NET library for reading and writing ISO files and virtual machine disk files, with no native code"; MIT licence (copyright Kenneth Bell 2008-2011, Quamotion 2014)
  - library projects include `DiscUtils.Lvm`, `DiscUtils.Vhd`, `DiscUtils.Vhdx`, `DiscUtils.Vmdk`, `DiscUtils.Xva`, `DiscUtils.Iscsi` and others; active (last commit 2026-11-08)
  
  — [LTRData/discutils](https://github.com/LTRData/discutils); [NuGet LTRData.DiscUtils](https://www.nuget.org/packages/LTRData.DiscUtils/) (licence and project list verified in the clone).
  - `DiscUtils.Lvm` parses segment types including "striped" (linear LVs are striped with one stripe in LVM2 text metadata), plus mirror/raid/snapshot types. It verifies PV label and metadata CRCs — [DiscUtils.Lvm source](https://github.com/LTRData/discutils/tree/master/Library/DiscUtils.Lvm).
  - `DiscUtils.Vhd` implements parent locators (W2ru/W2ku) and opens differencing chains through a `FileLocator` — [DiscUtils.Vhd source](https://github.com/LTRData/discutils/tree/master/Library/DiscUtils.Vhd).
  - No QCOW2 implementation exists in the library: a grep for "qcow" found nothing.
- **sm reads QCOW2 headers itself.** XCP-ng's `sm` parses QCOW2 headers in Python (`_read_qcow2_backingfile`) and otherwise relies on `qemu-img` and a `qcow2_helper` — [qcow2util.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/qcow2util.py).

### Inferences
- **Licensing fits the policy.** The VHD spec is OSP and QCOW2/LVM descriptions are public. MIT DiscUtils and BSD libvhd can be used or studied. GPL QEMU and GPL/LGPL LVM2 code can be read but should not be copied into an MIT/Apache product without considering the licence. Implementing from the spec documents is clean.
- **DiscUtils fits partly.** It can parse the LVM2 VG on the snapshot LUN and expose each LV as a stream, and its VHD reader can walk differencing chains. Three adaptations are needed:
  - a custom `FileLocator` that resolves Xen parent names (`VHD-<uuid>` / device paths in the locators) to LV streams in the same VG;
  - tolerance for the 0xC7-killed trailing footer by using the footer copy at offset 0. I did not verify whether DiscUtils does this or prefers the trailing footer; check `DiskImageFile` before relying on it;
  - ignoring the batmap/hidden extensions, which are harmless to a spec-conformant reader since they sit in reserved/unused areas.
- **QCOW2 needs its own reader.** On .NET this means a new, spec-based implementation: L1/L2 tables, backing chains, optional subclusters/compression, refusing unknown incompatible bits. On a Linux proxy, `qemu-img`/`qemu-nbd` (GPL-2, used as an external tool) can read QCOW2 chains. QEMU's own VHD ("vpc") driver is not suitable for Xen differencing chains (unverified here, see Gaps).
- **Alternative to parsing LVM on Windows:** a Linux proxy with LVM tools in read-only mode, plus `vhd-util read`/blktap (BSD) for VHD and `qemu-img` for QCOW2. That is more moving parts on the proxy, but it uses the reference implementations.

### Gaps
- I did not confirm whether DiscUtils.Vhd falls back to the head footer copy when the trailing footer is invalid.
- I did not confirm whether QEMU's `vpc` driver supports differencing VHDs. My recollection is no, but it was not verified in this session.
- No official LVM2 on-disk format spec from sourceware/Red Hat was found. The libvslvm document is a third-party reverse-engineering description; it is public and fine to use.

---

## 5. Safety of attaching a snapshot LUN with an LVM VG to a Windows or Linux proxy

### Takeaway
The main hazards:
- the proxy writes to the snapshot, either by Windows "Initialize disk"/signature writes or by Linux LVM auto-activation, repair or `vgimportclone`;
- the proxy sees or touches the live production LUN;
- LVM gets confused by duplicate PV/VG UUIDs.

Mitigations: export array snapshots to the proxy host read-only at the array level, set Windows SAN policy to OfflineAll and keep disks offline/read-only, and parse LVM in user space rather than activating it on Linux. On Linux also exclude the devices via the LVM devices file or filters, and never run `vgimportclone`, because it rewrites UUIDs. Zone and mask the proxy so it can never see the live LUN.

### Cited Findings
- **Windows SAN policy.** `san policy=OfflineAll` makes "all newly discovered disks, except the startup disk, offline and read-only by default". When a disk is offline "the disk layout can be read, but no volume devices are surfaced through Plug and Play… no file system can be mounted". Default on Windows Server is OfflineShared for SAN disks. Microsoft warns against OnlineAll on shared disks unless the server is clustered — [Microsoft: diskpart san](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/san).
- **sm puts the PV on the whole LUN** (`pvcreate -ff -y --metadatasize 10M <dev>`, no partition table), so Windows sees an "unknown, not initialized" disk — [lvutil.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/lvutil.py). The PV label sits in one of the first four sectors (normally sector 1), followed by the metadata area — [libvslvm LVM format doc](https://github.com/libyal/libvslvm/blob/main/documentation/Logical%20Volume%20Manager%20(LVM)%20format.asciidoc).
- **vgimportclone** "imports a VG from duplicated PVs, e.g. created by a hardware snapshot of existing PVs". It "renames the VG associated with the specified PV(s) and changes the associated VG and PV UUIDs", i.e. it writes to the PVs — [vgimportclone(8)](https://www.mankier.com/8/vgimportclone); [Ubuntu manpage](https://manpages.ubuntu.com/manpages/resolute/man8/vgimportclone.8.html).
- **LVM devices file.** `/etc/lvm/devices/system.devices`, managed with `lvmdevices`, lists the devices LVM may use, keyed by hardware IDs such as WWID. `use_devicesfile=0` or a missing file disables it, and `--devicesfile ""` overrides it per command — [lvmdevices(8)](https://man7.org/linux/man-pages/man8/lvmdevices.8.html). RHEL 10 documents device-visibility limiting and duplicate-PV warnings with cloned PVs — [RHEL 10: Limiting LVM device visibility and usage](https://docs.redhat.com/en/documentation/red_hat_enterprise_linux/10/html/configuring_and_managing_logical_volumes/limiting-lvm-device-visibility-and-usage).
- **HPE B10000 export behaviour.**
  - An HPE community user reports that exporting a B10000 snapshot offered only "read only".
  - Veeam's HPE integration docs say Veeam creates exports of type "Host" or "Host set", and that "Hosts and port" and "Port" export types are not supported for backup from storage snapshots.
  
  — [HPE Community thread](https://community.hpe.com/t5/hpe-3par-storeserv-storage/alletra-b10000-gt-restore-snapshot-vvset-is-remote-replication/m-p/7239167); [Veeam: HPE Alletra MP B10000/9000/Primera/3PAR limitations](https://helpcenter.veeam.com/docs/vbr/userguide/storage_limitations_primera.html) (search summary).
- **Veeam licensing note.** The storage system licence must include Virtual Copy for snapshot integration — [Veeam HPE limitations (search summary)](https://helpcenter.veeam.com/docs/vbr/userguide/storage_limitations_primera.html).

### Inferences
**Windows proxy**
- Set `san policy=OfflineAll` before the first attach.
- Present the snapshot only as a read-only export (Virtual Copy exported read-only to a host object containing only the proxy initiator).
- Open the disk as a raw device (`\\.\PhysicalDriveN`) without bringing it online. Raw reads of offline disks are possible for administrators.
- Never let an operator "Initialize" the disk. GPT initialisation writes sectors 0–33 and the backup GPT at the end of the disk, which would destroy the PV label and the start of the LVM metadata on a writable copy.
- Array-level read-only export is the real safety net, because Windows attributes can be cleared by mistake.

**Linux proxy**
- Do not let udev/`lvm2-pvscan` auto-activate the VG. Use a devices file that lists only the proxy's own disks (default on RHEL 9+), or a `global_filter` rejecting the iSCSI snapshot devices, or disable `event_activation`/`auto_activation_volume_list`.
- Parse LVM metadata in user space (DiscUtils or own code), or use `--readonly` LVM reporting commands only.
- Never run `vgimportclone`, `vgchange --refresh`, `pvscan --cache` with metadata repair, or similar write paths against snapshot devices.
- If two snapshots of the same SR, or the live LUN and a snapshot, are visible at the same time, LVM sees duplicate PV UUIDs and VG names. Present only one snapshot per SR at a time, and never the live LUN.

**Live LUN isolation**
- Use a dedicated host object / initiator for the proxy on the array, with no access to production LUNs.
- On iSCSI, use a separate target portal group or VLAN, and CHAP per host.
- In code, verify the volume WWN/serial of the attached disk against the expected snapshot WWN from the array API before reading.
- If the proxy is a VM on the same XCP-ng pool, it must not use the pool's iSCSI sessions. An external physical or VM proxy with its own initiator is cleaner.

### Gaps
- I found no official HPE Alletra MP B10000 document confirming read-only snapshot export semantics. The HPE Virtual Copy / WSAPI (REST) admin guide should be checked.
- The default LVM devices-file behaviour on Debian/Ubuntu versus RHEL was not verified in this session.

---

## 6. What does the hypervisor vendor (Vates/XCP-ng, Citrix/XenServer) say?

### Takeaway
I found no Vates or Citrix statement that endorses or supports third-party tools reading SR LUNs directly. Vates' public stance favours storage-agnostic, hypervisor-driven snapshots and backups via XAPI, and it plans vendor-specific array offload via storage-vendor APIs (Pure Storage/Everpure and NetApp on the roadmap; HPE is not mentioned) under a future SMAPIv3 design. An LVHD-parsing backup product would therefore be unsupported by the hypervisor vendor and exposed to changes in `sm`.

### Cited Findings
- **olivierlambert (Vates CEO) on the XCP-ng forum.** By default "everything is done by XCP-ng… The good side is that regardless the storage vendor, it will work". "We have plans this year to start with some storage vendors to offload snaps and other things". "We do not want to use SSH for that though (XO never use SSH)." The thread is undated in the fetched view; topic ID 10239 suggests late 2024/2025 — [XCP-ng forum: synchronize metadata backup with SAN/NAS snapshots](https://xcp-ng.org/forum/topic/10239/how-to-synchronize-metadata-backup-with-san-nas-snaphots-backup).
- **Vates storage-ecosystem page** (updated 2026-07-26):
  - "If your NAS or SAN speaks NFS or iSCSI, it works, whether or not its vendor appears anywhere in our documentation". iSCSI/HBA arrays need "no vendor-specific driver, plugin, or certification".
  - "Vates joined the Everpure [Pure Storage] Technology Alliance Program, with integration work on the roadmap."
  - No mention of array snapshot offload or third-party direct LUN access.
  
  — [Vates docs: Storage ecosystem](https://docs.vates.tech/compatible-solutions/storage-ecosystem/)
- **Vates roadmap:**
  - Storage LATER: "PureStorage integration support", "NetApp integration support", "SMAPIv3 - Ceph Driver".
  - Storage NEXT: "SMAPIv3 Design".
  - Xen Orchestra LATER: "Purestorage/Netapp integration".
  - No HPE item.
  - Roadmaps "are not commitments" — [Vates product roadmap](https://docs.vates.tech/product-roadmap/).
- **XCP-ng blog (2022).** Vates intends to work with storage manufacturers to "directly leverage some feature in their API like snapshots", like VMware does, via SMAPIv3 — [XCP-ng blog 2022-06-02](https://xcp-ng.org/blog/2022/06/02/understanding-the-storage-stack-in-xcp-ng/).
- **Citrix** removed StorageLink (array integration) in XenServer 6.5 — [Citrix XenCenter 8.2: Storage upgrade](https://docs.citrix.com/en-us/xencenter/8-2/storage-upgrade).
- **CBT licensing.** XenServer (Citrix) requires a Premium Edition licence for CBT, while an older guide said Enterprise. On XCP-ng, CBT is available without that licence (Xen Orchestra and Veeam use it) — [XenServer CBT guide (search summary)](https://docs.xenserver.com/en-us/xenserver/developer/changed-block-tracking-guide/appendices).

### Inferences
- **Support.** Any issue in a customer's environment that touches the SR (e.g. a proxy accidentally writing to a live LUN, or confusion caused by duplicate VGs) would fall outside Vates support. The product must make it impossible by design: read-only exports and WWN checks.
- **Future direction.** When Vates ships SMAPIv3 array plugins (Pure/NetApp first), each VDI will likely be its own array volume. Array snapshots of such VDIs would contain raw guest disks with no LVHD parsing, which is a much cleaner basis for BfSS. That is not available for HPE B10000 and not on the roadmap for HPE.
- A respectful option within the product policy: ask Vates on the forum whether they consider the LVHD on-disk layout (prefixes, journal LVs) a stable interface, and whether a documented API to pause GC per SR exists or is planned.

### Gaps
- I found no forum or blog statement by Vates staff specifically about third-party tools reading LVHD LUNs, or about array-snapshot-based backups of LVHD SRs.
- I found no public statement on HPE Alletra integration plans by Vates.
- A Pure Storage community blog reportedly describes an experimental SMAPIv3 FlashArray plugin for XCP-ng (one array volume per VDI, array-side snapshots). This came only from a search snippet and was not verified.

---

## 7. Overall verdict, risk list and mitigations

### Takeaway
**Verdict: technically feasible but not supportable as a default backup mode in Nov 2026.**

It is technically feasible as an optional, advanced "SAN mode" for LVHD (and QCOW2-on-LVM) SRs. It requires the XAPI snapshot plus CBT variant (not VSS-only), a strictly read-only, self-validating reader, and an automatic fallback to the XAPI/NBD path.

It is not supportable as a default, for four reasons:
- it relies on an undocumented-but-open-source on-disk layout that is actively changing (QCOW2 refactor, Live Coalesce, SMAPIv3);
- no backup vendor ships it for Xen;
- neither Vates nor Citrix endorses it;
- it saves only the read path. The XAPI snapshot, thick-provisioning space overhead and GC/coalesce work remain.

If the goal is faster or less intrusive snapshots, this design does not remove XAPI snapshot cost. It mainly shortens snapshot lifetime and moves read I/O off the hosts.

### Cited Findings
- No product found reads XCP-ng LVHD LUNs from array snapshots. Veeam (workers/hot-add), Storware (XVA or CBT+NBD), Commvault (proxy VM with attached disks) and Xen Orchestra (NBD+CBT) all read through the hypervisor — see section 1 sources ([Veeam Xen architecture](https://helpcenter.veeam.com/docs/vbr/userguide/xen_infrastructure_components.html), [Storware XCP-ng](https://docs.storware.eu/backup-and-recovery-7.4/protecting-virtual-machines/virtual-machines/xcp-ng), [Commvault Citrix Xen](https://documentation.commvault.com/11.42/commcell-console/backups_for_citrix_xen.html)).
- The layout is defined only in LGPL code and is being refactored (QCOW2 branch, `LV_PREFIX` now includes `QCOW2-`) — [lvmcowutil.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/lvmcowutil.py); [Vates roadmap](https://docs.vates.tech/product-roadmap/).
- CBT needs XAPI snapshots, and the compared newer VDI must not be attached — [XenServer CBT list_changed_blocks](https://docs.xenserver.com/en-us/xenserver/developer/changed-block-tracking-guide/list-changed-blocks). The snapshot can be reduced to CBT metadata with `VDI.data_destroy` — [XenServer CBT deleting snapshot](https://docs.citrix.com/en-us/xenserver/8/developer/changed-block-tracking-guide/deleting-snapshot).
- Thick LVHD snapshot space: on thick SRs you need space for the base copy, the snapshot and the active disk, the same as for VHD — [XCP-ng QCOW2 FAQ](https://docs.xcp-ng.org/storage/qcow2_faq/); [XCP-ng blog 2022](https://xcp-ng.org/blog/2022/06/02/understanding-the-storage-stack-in-xcp-ng/).

### Inferences — risk list with mitigations

| # | Risk | Likelihood / impact | Mitigation |
|---|------|--------------------|------------|
| R1 | **Layout change in `sm`** (prefix, journal semantics, new image format, module refactor, Live Coalesce, XCP-ng 9 / SMAPIv3) silently breaks the reader or makes it misread chains | Medium / High | Gate on the `sm` package version (read via XAPI `host.software_version` / installed updates) with an allow-list. Self-validate every chain (UUID linkage, sizes, checksums) and compare with XAPI facts captured at snapshot time. Fall back automatically to NBD+CBT on any anomaly. Keep a regression test suite against the real SR type per XCP-ng update. |
| R2 | **Mid-GC state captured** (leaf-coalesce rename, relink, inflate, clone journal present) | Low–Medium / High (wrong or missing data) | Before the array snapshot, check via XAPI that no SR-level task is running for the SR. After the snapshot, list journal LVs (`journaler` tag) and `OLD_*` LVs. If any concerns the chain being read, discard the array snapshot and retry. Optionally replicate sm's UNDO/FINISH decision rules virtually. Resolve parents by on-disk UUIDs. |
| R3 | **Reading a live leaf** (VSS-only variant): trailing footer killed (0xC7), in-flight BAT updates, no CBT | High / Medium | Do not use the VSS-only variant for VHD/QCOW2 SRs. Always take a short XAPI snapshot (quiesce via the guest tools if needed) so that backup data sits in read-only base images. If it is ever used, read the footer copy at offset 0 and accept crash consistency only. |
| R4 | **CBT reset** after host/SR crash, or a broken incremental base (snapshot destroyed instead of data-destroyed) | Medium / Medium | Keep `cbt_metadata` VDIs via `VDI.data_destroy`. Before each incremental, verify that the previous metadata VDI exists and `cbt_enabled` is true, otherwise run a full. Periodic synthetic or verification fulls. |
| R5 | **QCOW2 VDIs (>2040 GiB or SR preference qcow2)** not readable by DiscUtils | Certain if present / High | Implement a spec-based QCOW2 reader (refuse unknown incompatible bits, support backing chains; subclusters/compression later), or use `qemu-img`/`qemu-nbd` on a Linux proxy. Otherwise route QCOW2 VDIs to the NBD path. |
| R6 | **Proxy writes to the snapshot or the live LUN** (Windows initialise/signature, Linux LVM autoactivation/repair, `vgimportclone`) | Low with controls / Catastrophic on live LUN | Read-only array export to a dedicated proxy host object. Windows `san policy=OfflineAll`, disks stay offline/read-only, raw-device reads only. Linux devices file/filters excluding snapshot devices, no LVM activation, user-space parsing. WWN/serial check against the array API before any open. Proxy never zoned or masked to production LUNs. |
| R7 | **Multi-PV SR** (VG spanning several LUNs) snapshotted non-atomically | Low / High | Detect PV count from LVM metadata. Require an HPE consistency group (volume set) snapshot, or refuse. |
| R8 | **Duplicate VG/PV UUIDs** when several snapshots of the same SR are attached at once | Medium / Low–Medium | Attach one snapshot per SR at a time, or never hand the devices to LVM tools (pure user-space parsing). |
| R9 | **Space and performance on thick LVHD:** the XAPI snapshot still inflates a new leaf to full virtual size, and GC/coalesce load after each cycle is unchanged | Certain / Medium | Keep XAPI snapshot lifetime short (seconds). Monitor SR free space before snapshot. Throttle per SR so GC can finish (avoid long chains, which XO also guards against). |
| R10 | **Supportability:** no vendor endorsement, no precedent product | Certain / Medium | Ship as opt-in "SAN mode (experimental)", default to NBD+CBT. Document the dependency on `sm` internals. Ask Vates publicly whether the layout and journal semantics may be relied on, and whether a GC pause/state API exists or is planned. |
| R11 | **HPE side:** snapshot API, export semantics, licensing (Virtual Copy) | Medium / Medium | Use HPE's documented WSAPI/REST only. Verify the Virtual Copy licence. Create read-only snapshots, export to host objects (not "port" exports), and always clean up exports and snapshots (tag them). |
| R12 | **Policy fit:** using `sm` internals is "open source", not a "documented API" | Certain / Low–Medium | Reimplement from public specs (VHD OSP, QCOW2 spec, libvslvm doc) and read LGPL/BSD code only for reference. Do not copy GPL/LGPL code into the product unless licence-compatible. Clearly label the LVHD layout dependency as an implementation detail. |

**Bottom line for the product roadmap:** prioritise the supported path (XAPI snapshot, NBD + CBT via `VDI.list_changed_blocks`/`data_destroy`). Treat "LVHD SAN mode" as an optional accelerator only if host-side read throughput is the proven bottleneck. Re-evaluate when Vates ships SMAPIv3 vendor plugins with one array volume per VDI, which would make array-snapshot backup clean, though none exists for HPE today.

### Gaps
- There is no measured data on how much faster SAN reads would be than NBD export from XCP-ng 8.3 hosts. That needs a benchmark in the test lab.
- There is no public XAPI API to query or pause the `sm` garbage collector per SR. R2's mitigation therefore relies on post-hoc detection via journal LVs.
- HPE Alletra MP B10000 WSAPI/Virtual Copy documentation (read-only export, consistency groups/volume sets) was not retrieved in this session.
