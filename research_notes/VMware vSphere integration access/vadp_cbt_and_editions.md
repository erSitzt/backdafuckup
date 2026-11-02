# VMware vSphere APIs for Data Protection (VADP), snapshot/CBT APIs and the license editions that allow third-party backup (state as of 1 Nov 2026)

Research date: 2026-11-01. All "checked" HTTP status results below come from direct fetches on that date, made without logging in.

## Q1: Is VADP a formal API or a set of capabilities, and is its documentation public without login?

### Takeaway
VADP is not a separate API. It is an umbrella name for capabilities that are documented separately: the vSphere Web Services (SOAP) API methods (CreateSnapshot_Task, ReconfigVM_Task with changeTrackingEnabled, QueryChangedDiskAreas, RemoveSnapshot_Task), VMware Tools/VSS quiescing, and the VDDK library that moves the data. The documentation is still public without login on developer.broadcom.com, both the backup-design chapters of the VDDK guide and the vSphere Web Services API reference up to 9.1.1. The VDDK binaries themselves were removed from public download in August 2026 (see Q5).

### Cited Findings
- The "Designing Backup Solutions" material is now chapter 9 ("Backing Up Virtual Disks in vSphere") of the Virtual Disk API / VDDK programming guide on developer.broadcom.com. It returned HTTP 200 without login on 2026-11-01. The chapter introduction refers to "the vSphere Storage APIs – Data Protection (VADP)" as the subject of the sections that follow. Its subsections are vddkBkupVadp.9.2 (Design and Implementation Overview), 9.3 (Low Level Backup Procedures), 9.4 (Low Level Restore Procedures), 9.5 (Tips and Best Practices) and 9.6 (Windows Backup Implementations). — [Broadcom Developer: Backing Up Virtual Disks in vSphere](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkBkupVadp.9.1.html)
- The guide describes a two-part design. A Java (vSphere API) program contacts the host, creates a temporary snapshot and removes it later. A C/C++ VDDK program moves the disk data from the snapshot to backup media. It recommends that backup applications talk to vCenter Server rather than to individual ESXi hosts, and it says to "use the quiesce flag, but not the memory flag, which is incompatible with quiesce." — [Broadcom Developer: Design and Implementation Overview](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkBkupVadp.9.2.html)
- CBT, per the guide: "This feature, first available in vSphere 4, provides the foundation for incremental (or differential) backup of virtual disks. Your application can back up only changed data as indicated by the QueryChangedDiskAreas method. Virtual machines with virtual hardware version 7 and later support changed block tracking." Such VMs show `changeTrackingSupported` in the VirtualMachine capability field. — [Broadcom Developer: Design and Implementation Overview](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkBkupVadp.9.2.html)
- The low-level procedure shows these concrete calls:
  - `CreateSnapshot_Task(vmMoRef, SnapshotName, SnapshotDescription, memory_files=false, quiesce_filesystem=true)` to take the snapshot.
  - `configSpec.changeTrackingEnabled = new Boolean(true)` applied with `ReconfigVM_Task` to turn on CBT. Powered-on VMs then "must go through a stun-unstun cycle".
  - `queryChangedDiskAreas(SnapshotMoRef, diskDeviceKey, startPosition, changeId)` to read the changed areas. The special ChangeId `"*"` returns only the active (allocated) areas for a baseline backup.
  - Querying changes from before CBT was enabled returns a `FileFault`.
  — [Broadcom Developer: Low Level Backup Procedures](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkBkupVadp.9.3.html)
- Quiescing: the Windows chapter documents quiescing through the "VMware VSS component" in VMware Tools. Windows Server 2008 and later get "Application-consistent quiescing" when conditions are met, for example "Virtual machine must be running on ESXi 4.1 or later" and "The UUID attribute must be enabled." Older or desktop Windows versions get file-system-consistent quiescing only. — [Broadcom Developer: Windows Backup Implementations](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkBkupVadp.9.6.html)
- The vSphere Web Services API reference is public. On 2026-11-01 the VirtualMachine page loaded without login, and its version selector listed "9.1.1 (Latest) 9.1 9.0 …". The VirtualMachine method list includes CreateSnapshot_Task, CreateSnapshotEx_Task, QueryChangedDiskAreas and ReconfigVM_Task. — [Broadcom Developer: vim.VirtualMachine](https://developer.broadcom.com/xapis/vsphere-web-services-api/latest/vim.VirtualMachine.html)
- The API reference for QueryChangedDiskAreas says: "Get a list of areas of a virtual disk belonging to this VM that have been modified since a well-defined point in the past… the result of this function may contain 'false positives'… However, it is guaranteed that no changes will be missed." It lists "Required Privileges VirtualMachine.Provisioning.DiskRandomRead" and "Since vSphere API Release 4.0". — [Broadcom Developer: vim.VirtualMachine](https://developer.broadcom.com/xapis/vsphere-web-services-api/latest/vim.VirtualMachine.html)
- The API defines a license-type fault, `vim.fault.RestrictedVersion`: "Thrown when the caller is not permitted to perform the specified operation due to product versioning restrictions." — [Broadcom Developer: RestrictedVersion](https://developer.broadcom.com/xapis/vsphere-web-services-api/latest/vim.fault.RestrictedVersion.html)
- Broadcom's old KB "FAQ: VMware vSphere Storage APIs – Data Protection" (KB 311197) returned HTTP 404 on 2026-11-01. A search-engine snippet attributed to it says "Data Protection APIs are included with all licensed vSphere editions." I could not read the full text or its date. — [Broadcom KB 311197 (now 404)](https://knowledge.broadcom.com/external/article/311197/faq-vmware-vsphere-storage-apis-data-pr.html)

### Inferences
- In practice "VADP" means vSphere SOAP API methods (snapshot, CBT, quiesce flag), plus VMware Tools/VSS inside the guest, plus VDDK transport modes (NBD/NBDSSL, HotAdd, SAN). There is no separately licensed "VADP API" object or endpoint. This is inferred from how the guide is structured and from the API reference.
- The "latest" VDDK guide chapters do not name the VDDK version they apply to. The text still mentions Windows XP/2000/2003 and ESXi 4.1, and the footer says "Copyright © 2005-2024". The prose has probably been carried forward from older releases with few changes. The procedures are still valid because the API methods are unchanged since API 4.0, but the guide should not be treated as a vSphere 9-specific design document.
- `RestrictedVersion` is probably the fault a free-licensed ESXi host returns for write operations such as CreateSnapshot_Task. I have no source that ties this fault to the free-ESXi restriction (see Gaps).

### Gaps
- I could not find a VDDK 9.x-specific "Designing Backup Solutions" document or the VDDK 9.0/9.1 release notes. The VDDK SDK landing page now returns 404 (see Q5), so version-specific release notes may no longer be public.
- The full text of the old VADP FAQ (KB 311197) could not be retrieved. Whether Broadcom retired it or moved it is unknown.

## Q2: Which current editions/bundles include VADP and third-party backup support, and how did the lineup change in 2024–2026?

### Takeaway
Every paid edition currently sold supports third-party VADP backup: vSphere Standard, vSphere Enterprise Plus, VMware vSphere Foundation (VVF) and VMware Cloud Foundation (VCF). Broadcom no longer lists VADP as a per-edition feature, and no edition tier gates it. The gating line is "paid license vs. free ESXi". Essentials and Essentials Plus are no longer sold: Essentials Plus (VVEP) ended around April 2025 and is absent from Broadcom's October 2026 comparison. Standard and Enterprise Plus are capped at vSphere 8 Update 3, so a customer who wants vSphere/ESXi 9.x must buy VVF or VCF. VVF has been withdrawn in some EMEA countries.

### Cited Findings
- Broadcom's current vSphere Product Line Comparison (Item No. "vSphere9.1_ProductLineComparison 10/26", © 2026) says: "VMware vSphere is available in two standalone editions, VMware vSphere Standard and VMware vSphere Enterprise Plus, and is also included as a component in VMware Cloud Foundation and VMware vSphere Foundation. Note that vSphere Standard and vSphere Enterprise Plus are only available as versions up to the 8 Update 3 release. Currently, vSphere 9.1 features are only available as part of VMware vSphere Foundation 9.1 and VMware Cloud Foundation 9.1." — [Broadcom vSphere Product Line Comparison (PDF)](https://www.vmware.com/docs/vmw-datasheet-vsphere-product-line-comparison)
- The same comparison has columns for Standard, Enterprise Plus and VVF only.
  - It has no row for "vSphere Storage APIs for Data Protection", VADP or CBT.
  - "vSphere Replication", "vCenter File-Based Backup and Restore", vMotion, HA and Storage vMotion are ticked for all three.
  - A "Data Protection and Recovery" row is ticked for VVF only. It sits among VCF Operations features (compliance, capacity and cost management).
  - "Snapshot Manager with Flexible scheduling" and "Immutable Snapshots" are ticked for VVF only, under vSAN.
  - No Essentials or Essentials Plus edition appears.
  — [Broadcom vSphere Product Line Comparison (PDF)](https://www.vmware.com/docs/vmw-datasheet-vsphere-product-line-comparison)
- Broadcom's free-ESXi KB describes the production (paid) license as including "vMotion, HA, DRS, vSphere Replication, and API-based backup and monitoring". The free license is described as "No access to vMotion, DRS, HA, VADP-based backups." — [Broadcom KB 399823](https://knowledge.broadcom.com/external/article/399823/free-version-of-vmware-esxi.html)
- Thomas-Krenn wiki (last edited 9 Sep 2025), under the heading "Update April 2025: VMware vSphere Essentials Plus (VVEP) not available anymore": "Broadcom has discontinued the sale of VVEP. Ongoing contracts remain, but an extension is not possible." Its list of terminated products includes "VMware vSphere Essentials" and "vSphere Free Hypervisor". It also reports that "Broadcom announced at VMware Explore US that it will once again include vSphere Enterprise Plus as a subscription model", and that "VVF is the successor product of vSphere Enterprise & vSphere Enterprise Plus." — [Thomas-Krenn Wiki: VMware license model by Broadcom](https://www.thomas-krenn.com/en/wiki/VMware_license_model_by_Broadcom)
- Secondary sources disagree on when VVEP ended, giving either late 2024 or April 2025. — [Sikich](https://www.sikich.com/insight/broadcoms-vmware-licensing-shake-up-what-changed-with-essentials-plus-kit/); [Thomas-Krenn Wiki](https://www.thomas-krenn.com/en/wiki/VMware_license_model_by_Broadcom); [AceMQ](https://acemq.com/blogs/vsphere-essentials-plus-replacement/)
- Eurokommerz (published 11 Oct 2026, updated 26 Oct 2026, facts as of 23 Oct 2026; it cites Broadcom program documentation and KBs) reports:
  - VVF 9.1 became GA on 5 May 2026, and 9.1.1 has been available since 3 Sep 2026.
  - Standard and Enterprise Plus are capped at 8 Update 3, and no 9.x SKU exists for them.
  - No end-of-sale or end-of-life date is documented for Standard, Enterprise Plus or VVF as editions.
  - vSphere 8.x end of general support is 11 Oct 2027 (KB 459135).
  - VCF/VVF subscriptions include a key for hosts still on vSphere 8.
  - It quotes a Broadcom spokesperson to The Register: "We have not announced any changes to the availability of vSphere Standard in EMEA nor end of support for vSphere Standard."
  — [Eurokommerz: vSphere Standard and VVF end of sale](https://eurokommerz.at/blog/vsphere-standard-and-vvf-end-of-sale/)
- The Register (11 Dec 2025) reported that Broadcom withdrew vSphere Foundation in parts of EMEA. Broadcom's statement was "VVF is no longer available in some EMEA countries". — [The Register](https://www.theregister.com/2025/12/11/vmware_kills_vsphere_foundation_parts_emea/); quote via [Eurokommerz](https://eurokommerz.at/blog/vsphere-standard-and-vvf-end-of-sale/)
- ITDaily (9 Sep 2026), citing The Register (2 Sep 2026) and VMware Explore 2026 comments by Paul Turner (CPO, VCF Division), reports that a new vSphere Standard is "reportedly in the works". Broadcom "has not yet launched any specifications or pricing for the new product." — [ITDaily](https://itdaily.com/blogs/datacenter/broadcom-makes-a-u-turn-on-vsphere-standard-in-an-effort-to-win-back-small-vmware-customers/)
- Veeam Backup & Replication 13 lists support for vSphere/ESXi 9.x (up to 9.1), 8.x and 7.x, with vCenter optional. Its only edition-specific rule is for CDP (I/O-filter-based Continuous Data Protection): "VMware vSphere edition must be VMware vSphere Essentials Kits editions or higher. vCenter Server is required. Standalone ESXi hosts are not supported." — [Veeam Help Center: VMware vSphere platform support (v13)](https://helpcenter.veeam.com/docs/vbr/userguide/platform_support_vm.html)

### Inferences
- Combining the comparison PDF, KB 399823 and Veeam's requirements, the minimum a customer needs today is any paid ESXi license: vSphere Standard (8.0U3 max), Enterprise Plus (8.0U3 max), VVF or VCF. Backup capability is the same across these tiers. No current Broadcom document says that VADP, CBT or snapshot APIs differ between Standard, Enterprise Plus, VVF and VCF.
- Customers still running Essentials or Essentials Plus under existing (non-renewable) terms keep VADP backup, since Veeam's wording "Essentials Kits editions or higher" covers them. They cannot renew, and the old perpetual keys do not license vSphere 9. The vSphere 9 claim comes from Eurokommerz ("Perpetual licence holders can upgrade to 8.x only").
- Veeam's "Essentials Kits" wording reflects the pre-Broadcom lineup. For current purchases, read it as "any paid edition".
- vSAN snapshot features (Snapshot Manager, Immutable Snapshots) are VVF/VCF-only, but they are native vSAN features, not the VADP path that third-party backup tools use.

### Gaps
- No current Broadcom document explicitly lists "vSphere Storage APIs – Data Protection" per edition. The row was dropped from the comparison. The "all licensed editions" statement rests on KB 399823's production/free contrast and on a search snippet of the now-404 KB 311197.
- VCF has no column in the comparison PDF. Its inclusion of all vSphere features is inferred from "included as a component".
- I could not find a primary Broadcom end-of-availability notice for VVEP with an exact date. Dates come from resellers and wikis.
- The status, timing, pricing and core minimum of a possible new vSphere Standard (reported Sep 2026) are unconfirmed by Broadcom.

## Q3: Free ESXi (vSphere Hypervisor): status, read-only API, and whether third-party backup works

### Takeaway
Broadcom discontinued the free vSphere Hypervisor in early 2024 and brought it back with ESXi 8.0 Update 3e. The release is dated 10 Apr 2025, and one source says free downloads only became practical in September 2025. Broadcom's current KB keeps the old restriction: APIs on free ESXi "may only provide read-only information" and there is "No access to … VADP-based backups." Veeam v13 states that free ESXi is not supported. I found no free ESXi 9.x; the free offer appears limited to 8.0U3e.

### Cited Findings
- Broadcom KB 399823, "VMware ESXi 8.0 Update 3e now available as a Free Hypervisor" (free build 24677879; "Updated On" field blank):
  - "Usage of APIs to manage hosts is not supported, and may only provide read-only information."
  - Free ESXi "Cannot be managed by vCenter Server" and has "No access to vMotion, DRS, HA, VADP-based backups."
  - Limits: "Up to 2 physical CPUs supported per host", "8 vCPUs per virtual machine", "No official Broadcom support".
  - "The key difference lies only in the type of license applied after installation, not in the software itself."
  — [Broadcom KB 399823](https://knowledge.broadcom.com/external/article/399823/free-version-of-vmware-esxi.html)
- An ESXi build list dates ESXi 8.0 Update 3e to 10 Apr 2025, build 24674464. This differs from the KB's free build number 24677879. The same source says that from 18 Sep 2025 one could download ESXi 8.0U3e "with a perpetual, free license", which suggests the free download path was not fully available at release. — [TinkerTry](https://tinkertry.com/easy-update-to-latest-esxi)
- Veeam v13: "Free ESXi is not supported. Veeam Backup & Replication leverages vSphere and vStorage APIs that are disabled by VMware in free ESXi." — [Veeam Help Center: VMware vSphere platform support (v13)](https://helpcenter.veeam.com/docs/vbr/userguide/platform_support_vm.html)
- Veeam stopped supporting ESXi Free as far back as 2009, while saying licensed ESXi was fully supported. — [Veeam press release (2009)](https://www.veeam.com/company/press-release/veeam-software-offers-new-essentials-bundle-acceleration-kits-for-vmware-vsphere4.html)
- NAKIVO documents a workaround that does not use VADP. Its KB is old (product version 10.6, last modified 11 Feb 2022) and may be superseded. With free ESXi: "SSH must be enabled on the Free ESXi host", "CBT must be enabled manually for every VM that is backed up", free hosts cannot be replication targets, and VM Verification is unavailable. — [NAKIVO KB: Free ESXi Backup Limitations](https://helpcenter.nakivo.com/Knowledge-Base/Content/Frequently-Asked-Questions/Supported-Platforms/Free-ESXi-Backup-Limitations.htm)
- The Thomas-Krenn wiki (Sep 2025) still lists "vSphere Free Hypervisor" among terminated products. This reflects the 2024 discontinuation and conflicts with the KB 399823 revival. — [Thomas-Krenn Wiki](https://www.thomas-krenn.com/en/wiki/VMware_license_model_by_Broadcom)
- Searches for a free ESXi 9.0/9.1 license found no evidence that one exists. — [search; no primary source found]

### Inferences
- The read-only API limit on free ESXi is still in place as of the current KB. Snapshot creation (CreateSnapshot_Task) and CBT enablement (ReconfigVM_Task) are write operations, so VADP-style backup is not possible through the API on free ESXi. Tools that do support free ESXi (for example NAKIVO historically) work around this through SSH and shell commands such as vim-cmd/vmkfstools. That path is unsupported by Broadcom and fragile.
- Because the free offer is ESXi 8.0U3e only, and vSphere 8 general support ends 11 Oct 2027 (see Q2), free ESXi is a dead end for backup-product planning.

### Gaps
- I found no primary Broadcom source on the exact public availability date of the free 8.0U3e download, or on whether the free license persists in later 8.0U3 patches (3f, 3g and so on).
- The exact error a free host returns when a backup tool calls CreateSnapshot_Task is not documented in what I found. `RestrictedVersion` is likely but unconfirmed.
- No current (2025–2026) NAKIVO, Hornetsecurity/Altaro or Vinchin statement on free ESXi was retrieved.

## Q4: Are CBT, application-consistent quiescing (VSS via VMware Tools) or snapshot APIs restricted in specific editions or in vSphere 8/9?

### Takeaway
I found no edition-specific restrictions on CBT, quiescing or snapshot APIs among the paid editions. The only license-based restriction is the free ESXi license. The constraints that do apply are technical: CBT needs VM hardware version 7 or later and a stun/unstun cycle to enable; application-consistent quiescing needs VMware Tools' VSS component and the UUID setting; QueryChangedDiskAreas needs the DiskRandomRead privilege. I found no documented CBT or snapshot API changes in vSphere 9.x, and backup vendors support 9.0/9.1 using native CBT.

### Cited Findings
- CBT requires "virtual hardware version 7 and later" and is advertised through `changeTrackingSupported`. — [Broadcom Developer: Design and Implementation Overview](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkBkupVadp.9.2.html)
- Enabling CBT on a powered-on VM requires a stun-unstun cycle (power cycle or snapshot create/delete). Change data from before CBT was enabled is unavailable (FileFault). — [Broadcom Developer: Low Level Backup Procedures](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkBkupVadp.9.3.html)
- QueryChangedDiskAreas requires the "VirtualMachine.Provisioning.DiskRandomRead" privilege. The API reference documents it unchanged in 9.1.1. — [Broadcom Developer: vim.VirtualMachine](https://developer.broadcom.com/xapis/vsphere-web-services-api/latest/vim.VirtualMachine.html)
- Application-consistent quiescing goes through the VMware VSS component (VMware Tools) on Windows Server 2008 and later. Conditions include "ESXi 4.1 or later" and the UUID attribute being enabled. — [Broadcom Developer: Windows Backup Implementations](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkBkupVadp.9.6.html)
- The quiesce and memory snapshot flags are mutually incompatible. — [Broadcom Developer: Design and Implementation Overview](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkBkupVadp.9.2.html)
- Broadcom's 2026 edition comparison has no CBT, snapshot or quiescing row. Differences between Standard, Enterprise Plus and VVF are in DRS, Host Profiles, VM encryption, vSAN, Kubernetes and Operations features. — [Broadcom vSphere Product Line Comparison (PDF)](https://www.vmware.com/docs/vmw-datasheet-vsphere-product-line-comparison)
- Veeam: if a VM had active snapshots during a backup and one of them is deleted before the next run, CBT is not used and the whole VM is read. VMs below the required virtual hardware version fall back to Veeam's own filtering. — [Veeam Help Center: Changed Block Tracking](https://helpcenter.veeam.com/docs/backup/vsphere/changed_block_tracking.html)
- NAKIVO announced vSphere 9 support in v11.0.4 (Sept 2025), including incremental backups using VMware's native CBT. — [NAKIVO press release v11.0.4](https://di1.nakivo.com/company/press-releases/nakivo-launches-v11-0-4/)
- Veeam v13's requirements page lists ESXi/vCenter "9.x (up to 9.1)". — [Veeam Help Center (v13)](https://helpcenter.veeam.com/docs/vbr/userguide/platform_support_vm.html). An earlier Veeam forum post said 9.1.x was not yet supported and was planned for v13.1, so 9.1 support probably arrived in a v13.x update. — [Veeam forum](https://forums.veeam.com/viewtopic.php?p=569590)
- Veeam CDP (a VAIO I/O filter, not VADP snapshots) needs "Essentials Kits editions or higher" plus vCenter. This is the only edition-based backup rule Veeam states. — [Veeam Help Center (v13)](https://helpcenter.veeam.com/docs/vbr/userguide/platform_support_vm.html)

### Inferences
- For a third-party backup product, the restrictions to design around are technical (VM hardware version, VMware Tools/VSS state, privileges, existing snapshots, vCenter vs. standalone host), not edition-based, provided the host has a paid license.

### Gaps
- I found no Broadcom vSphere 9.0/9.1 release-note item on CBT, snapshot or quiescing behavior. I did not check 9.x release notes line by line, so minor changes or deprecations (for example in VMware Tools VSS providers) cannot be ruled out.
- No Broadcom statement was found confirming that VAIO/I-O filter APIs (used for CDP) are available in every current edition.

## Q5: Does a backup vendor need a special API entitlement, or does the customer's vSphere license alone decide what works?

### Takeaway
On the customer side, the host license alone decides what works. Any paid ESXi license (Standard, Enterprise Plus, VVF, VCF) enables the snapshot and CBT API calls, and free ESXi blocks them (read-only API). On the vendor side, the situation changed in August 2026. Broadcom removed public VDDK downloads without announcement, stated that "The VDDK is not a customer entitlement", and said VDDK remains available "through select TAP partners for its licensed use case" (backup and recovery). The API documentation is still public, but a vendor that needs the VDDK data-transport library now in practice needs a Broadcom Technology Alliance Partner (TAP) relationship.

### Cited Findings
- The VDDK SDK pages returned HTTP 404 on 2026-11-01, without login: `developer.broadcom.com/sdks/vmware-virtual-disk-development-kit-vddk/latest` and `/9.0`. — [Broadcom Developer VDDK page (404)](https://developer.broadcom.com/sdks/vmware-virtual-disk-development-kit-vddk/latest)
- It's FOSS (9 Sep 2026) reports:
  - Broadcom took down the public download pages for VDDK 8 and 9 and the VDDK landing page in August 2026, with "no formal announcement".
  - A Broadcom Customer Care reply said "the Virtual Disk Development Kit (VDDK) is no longer available for use or download" and directed customers to "authorized technology alliance partners" for backup and recovery.
  - "Existing copies will still be functional".
  - VDDK is not redistributable under Broadcom's SDK License Agreement.
  - Red Hat MTV, Azure Migrate and ShapeBlue/CloudStack users were affected.
  — [It's FOSS](https://itsfoss.com/news/broadcom-removes-vmware-vddk-downloads/)
- TechTarget (9 Sep 2026) quotes a Broadcom spokesperson:
  - "The ability for individual organizations to download VDDK recently changed."
  - "The approved use case for VDDK per the SDK license has always been for backup and recovery solutions" (from TAP partners).
  - The change "merely re-enforces those terms".
  - Access is "still available through select TAP partners for its licensed use case".
  - "The VDDK is not a customer entitlement."
  - Analyst Mike Leone noted that anyone with a free developer account could previously download it. IDC's Matthew Flug said it "certainly increases the potential of vendor lock-in."
  — [TechTarget](https://www.techtarget.com/it-infrastructure/news/366648768/Broadcom-confirms-change-in-VMware-migration-tool-access)
- The API documentation (vSphere Web Services API 9.1.1 reference and the VDDK guide's backup chapters) remained publicly reachable without login on 2026-11-01. — [Broadcom Developer: vim.VirtualMachine](https://developer.broadcom.com/xapis/vsphere-web-services-api/latest/vim.VirtualMachine.html); [Broadcom Developer: Backing Up Virtual Disks](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkBkupVadp.9.1.html)
- On the customer side, KB 399823 ties "API-based backup" to the production license and excludes "VADP-based backups" from the free license. — [Broadcom KB 399823](https://knowledge.broadcom.com/external/article/399823/free-version-of-vmware-esxi.html)
- Veeam ties its free-ESXi exclusion to the APIs that "are disabled by VMware in free ESXi". It names no vendor-side entitlement. — [Veeam Help Center (v13)](https://helpcenter.veeam.com/docs/vbr/userguide/platform_support_vm.html)
- The API privilege model is per vCenter/ESXi user role (for example DiskRandomRead for QueryChangedDiskAreas), not a vendor license key. — [Broadcom Developer: vim.VirtualMachine](https://developer.broadcom.com/xapis/vsphere-web-services-api/latest/vim.VirtualMachine.html)

### Inferences
- Snapshot and CBT orchestration through the SOAP API needs no vendor-specific entitlement. Any client with sufficient vCenter/ESXi privileges on a paid-licensed host can call CreateSnapshot_Task and QueryChangedDiskAreas, and the documentation is public.
- Efficient data movement with VDDK (NBD/NBDSSL, HotAdd, SAN transports) now effectively requires TAP membership or an existing VDDK copy, under SDK license terms that Broadcom says restrict use to backup/recovery. New or small vendors, and open-source projects, face a barrier that did not exist before August 2026.
- Without VDDK, a backup tool would need other ways to read snapshot disk data, such as datastore HTTP file access or NFC. Their suitability and license compliance were not researched (see Gaps).
- The removal was unannounced, so Broadcom's policy could change again or be clarified. Re-verify before relying on it.

### Gaps
- I did not obtain the public terms of Broadcom's Technology Alliance Program (TAP) for data protection partners: cost, eligibility, and whether VDDK access requires a specific TAP tier or certification.
- I did not find whether licensed VCF/VVF customers can still get VDDK through the Broadcom Support Portal (one forum post claimed this, unverified), or whether Broadcom published a formal KB on the change after September 2026.
- I did not research alternatives to VDDK for disk data access (datastore HTTP GET, NFC, vSphere 9 APIs) or their legal standing under Broadcom's terms.
