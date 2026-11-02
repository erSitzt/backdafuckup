# VMware VDDK access, licensing and transport modes (state as of 2026-11-01)

> Researcher note: the single most important finding is that **Broadcom withdrew public VDDK downloads around 25 August 2026** and now says VDDK is available only to "select TAP partners" for backup and recovery. Everything below should be read in that light. Several VDDK documentation pages on techdocs.broadcom.com also return 404 now (checked by the researcher on 2026-11-01), so some primary sources could only be seen through search-engine snippets. Those are marked "(snippet only)".

## 1. Where is VDDK downloaded today, and what access does it need? Did this change after the Broadcom acquisition?

### Takeaway
There is no public VDDK download any more. Since about 25 Aug 2026 the developer.broadcom.com VDDK pages return 404. Broadcom says VDDK "is not a customer entitlement" and is available only "through select TAP partners for its licensed use case" (backup and recovery). A new commercial backup vendor therefore needs a Broadcom Technology Alliance Program (TAP) relationship, and specifically one that Broadcom grants VDDK access to, before it can legally obtain VDDK at all.

### Cited Findings
**Removal (primary checks plus trade press)**
- Researcher's own HTTP check on 2026-11-01: `https://developer.broadcom.com/sdks/vmware-virtual-disk-development-kit-vddk/latest`, `/9.0` and `/8.0` all return **404**. Control pages on the same site (`https://developer.broadcom.com/sdks`, `/sdks/vsphere-management-sdk/latest`) return 200, so this is not a site-wide outage or bot block. — [developer.broadcom.com VDDK latest](https://developer.broadcom.com/sdks/vmware-virtual-disk-development-kit-vddk/latest); [developer.broadcom.com VDDK 9.0](https://developer.broadcom.com/sdks/vmware-virtual-disk-development-kit-vddk/9.0)
- ShapeBlue checked on 25 Aug 2026 from Europe and through US, Brazilian, Finnish and Japanese exit nodes. Every VDDK path returned "The page you requested couldn't be found. We might have removed the page during a website redesign": `/sdks/vmware-virtual-disk-development-kit-vddk/`, `/latest/`, `/8.0`, `/9.0`. — [ShapeBlue](https://www.shapeblue.com/broadcom-vddk-download-vmware-to-kvm/)
- ShapeBlue found no Broadcom announcement, deprecation notice, retention policy, archive or stated alternative route. — [ShapeBlue](https://www.shapeblue.com/broadcom-vddk-download-vmware-to-kvm/)
- Problems with access started before the removal. In Feb 2024 the VDDK 8.0.2.1 release notes appeared without a download link, which a Broadcom representative restored five months later. In Sep 2024 download buttons returned JSON errors, and users speculated that access depended on licence tier and country. In Nov 2025 the 8.x download stopped working and only 9.x remained. Releases 6.x and older were no longer available. — [ShapeBlue](https://www.shapeblue.com/broadcom-vddk-download-vmware-to-kvm/) (based on Broadcom community forum threads, so forum-level evidence)

**Broadcom's official position (on record via the press)**
- TechTarget, 9 Sep 2026, quoting Broadcom: "The ability for individual organizations to download VDDK recently changed", "The approved use case for VDDK per the SDK [Software Development Kit] license has always been for backup and recovery", "Access to the VDDK is still available through select TAP partners for its licensed use case", "The VDDK is not a customer entitlement. It is a software development kit covered by its own developer license". — [TechTarget](https://www.techtarget.com/it-infrastructure/news/366648768/Broadcom-confirms-change-in-VMware-migration-tool-access)
- heise (22 Sep 2026), citing Broadcom's confirmation to The Register: VDDK stays available through selected TAP members for backup and recovery. Customers should use approved partner solutions. Broadcom calls it an "independently licensed SDK" that was never part of a software purchase. There is no transition date, grace period or non-partner route, no notice in the VDDK technical docs, and nothing said about the rights to copies that already exist. — [heise](https://www.heise.de/en/news/Broadcom-makes-VMware-exit-difficult-11461334.html)
- A Broadcom Customer Care reply, as quoted: "the Virtual Disk Development Kit (VDDK) is no longer available for use or download". Broadcom "continues to maintain APIs and SDKs" for "authorized technology alliance partners to build backup and recovery software solutions". — [Zadara blog](https://www.zadara.com/blog/2026/09/29/broadcom-vddk-restriction-vmware-migration/) (vendor blog quoting a support reply); [itsfoss](https://itsfoss.com/news/broadcom-removes-vmware-vddk-downloads/)
- According to VMware/Broadcom as reported by Techzine, migration to other platforms is "not a licensed use". — [Techzine](https://www.techzine.eu/news/devops/144228/vmware-restricts-vddk-for-migrations/)

**Conflicting reports about an entitlement or support-portal route**
- itsfoss says customers with a current vSphere entitlement "can still locate the SDK through the Broadcom support portal" and that TAP members can request it. — [itsfoss](https://itsfoss.com/news/broadcom-removes-vmware-vddk-downloads/). **This conflicts with** Broadcom's own on-record "VDDK is not a customer entitlement" — [TechTarget](https://www.techtarget.com/it-infrastructure/news/366648768/Broadcom-confirms-change-in-VMware-migration-tool-access).
- A Nutanix community user reports finding VDDK on support.broadcom.com under My Downloads, after switching to VMware Cloud Foundation, in the vSphere 7.x SDKs section. This is a single user report and may predate the removal. — [Nutanix community](https://next.nutanix.com/move-application-migration-19/vsphere-vddk-download-for-nutanix-move-45855)
- ShapeBlue's FAQ tells customers with active entitlements to check the Broadcom Support Portal and to ask their account team, in writing, about availability, archival policy and what happens when the entitlement lapses. — [ShapeBlue](https://www.shapeblue.com/broadcom-vddk-download-vmware-to-kvm/)

**Ecosystem fallout (relevant as precedent)**
- Red Hat published a support article around 27 Aug 2026. It says VDDK images are no longer available through the public download paths that the Migration Toolkit for Virtualization used, that Red Hat cannot distribute VDDK because it is proprietary, and that customers should contact Broadcom. — [Zadara blog](https://www.zadara.com/blog/2026/09/29/broadcom-vddk-restriction-vmware-migration/) (secondary; the Red Hat KB itself was not fetched)
- Microsoft Azure Migrate moved its VMware-source guidance from agentless to agent-based migration. Nutanix Move users report they cannot obtain VDDK. Platform9 vJailbreak now "depends on already holding a copy of the library". — [Zadara blog](https://www.zadara.com/blog/2026/09/29/broadcom-vddk-restriction-vmware-migration/)

**TAP program (the remaining access route)**
- TAP FAQ: "There is no fee associated with the VMware TAP membership." Joining works through Register on tap.broadcom.com, then selecting Technology Alliance Program. The TAP Support Team reviews the application. "Only TAP members may join Certification/Integration programs", and those "generally require vetting by VMware, usually in the form of a questionnaire". Members can request NFR (Not-for-Resale) development licences. The FAQ does not mention VDDK, SDK access or redistribution. — [tap.broadcom.com/help](https://tap.broadcom.com/help)
- Broadcom KB 385486 says TAP "provides members with the tools and resources necessary to build and deliver solutions" and links to the TAP portal. It lists no tiers, fees or SDK and VDDK benefits. — [Broadcom KB 385486](https://knowledge.broadcom.com/external/article/385486/the-technology-alliance-partner-tap.html)

### Inferences
- For a new commercial backup vendor, the practical gate is a Broadcom TAP membership plus Broadcom granting VDDK access. Broadcom's wording, "select TAP partners", implies that TAP membership alone may not be enough. Backup and recovery is the use case Broadcom says it still licenses, so a backup product is the most favourable case, but it is still at Broadcom's discretion.
- Downloading VDDK from mirrors, Docker images or a customer's copy is not a legal route for a commercial product. The licence (Section 2) grants copies only "for your own internal use". See question 2.
- Plan VMware support as dependent on a business-development track with Broadcom: apply to TAP early and ask in writing for VDDK access and a redistribution agreement. Also design a fallback that does not need VDDK (see question 5).

### Gaps
- No Broadcom primary page (KB, TAP portal page or developer-portal notice) about the VDDK change was found. All Broadcom statements come through TechTarget, The Register (via heise and Techzine) and quoted support replies.
- It is unknown which TAP tier or criteria qualify as a "select TAP partner" for VDDK, how long approval takes, and whether a new or small vendor without a shipping product is accepted.
- It is unknown whether a free Broadcom account login was required for the developer-portal download before Aug 2026. ShapeBlue's Sep 2024 forum evidence only suggests tier- or country-dependent behaviour.
- It is unclear whether support.broadcom.com still offers VDDK to entitled vSphere customers. The reports conflict, and Broadcom's on-record statement points to no.

## 2. Current VDDK licence terms: may a commercial vendor redistribute the VDDK libraries? Conditions?

### Takeaway
VDDK binaries may not be redistributed under the default SDK licence. The VMware/Broadcom VDDK documentation says redistribution requires TAP membership and a separately signed **VDDK redistribution agreement**. The generic Broadcom "Software Development Kit License Agreement" (v.01222026) allows only internal-use copies to build "Developer Software". It allows redistribution only of code designated "distributable", and only under a licence that protects Broadcom. Either party may terminate it "for any reason at any time".

### Cited Findings
**Redistribution rule in the VDDK documentation (primary, still online)**
- From the VDDK programming guide intro hosted on developer.broadcom.com (6.0-era content, footer "Copyright © 2005-2024 Broadcom"): "To qualify for VDDK redistribution, you must be in the VMware TAP program at Select level or above, and sign a redistribution agreement. Contact your VMware alliance manager to request the VDDK redistribution agreement. VMware would like to know how you use the VDDK, in what products you plan to redistribute it, your company name, and your contact information." — [developer.broadcom.com VDDK intro, "Redistributing VDDK Components"](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkIntro.3.5.html)
- The vSphere 8.0 VDDK programming guide version of the same page reportedly says "**Standard** level or above" plus a signed redistribution agreement (snippet only; the page now 301-redirects to a techdocs URL that returns 404). **This conflicts with** the "Select level" wording above. — [docs.vmware.com VDDK 8.0 guide, Redistributing VDDK Components](https://docs.vmware.com/en/VMware-vSphere/8.0/vsphere-vddk-programming-guide/GUID-282C600E-D986-4592-9206-70BF60DBF684.html)
- VMware's own (now archived) Go wrapper repo says VDDK is "free to use for personal and internal use. Redistribution requires a no-fee license, please contact VMware to obtain the license." (snippet only; VMware-era) — [github.com/vmware-archive/virtual-disks](https://github.com/vmware-archive/virtual-disks)

**Broadcom SDK License Agreement (primary; PDF retrieved and read in full; version string "v. 01222026", document dated Jan 2026, "Copyright © 2026 Broadcom")** — [docs.broadcom.com: vmware-software-development-kit-license-agreement](https://docs.broadcom.com/doc/vmware-software-development-kit-license-agreement)
- Licensor: "CA, Inc. ("Broadcom")". Acceptance: "By downloading, installing, or using the Software, you ... agree to be bound".
- §1 says the Software is "intended to be used to execute supplied commands and utility applications and to create scripts that interact with the Broadcom Offerings."
- §2, use rights: "you may download and make a reasonable number of copies of the Software for your own internal use solely for the purpose of creating software that communicates with Broadcom Offerings (your software, "Developer Software")."
- §2, distribution: "Some code may be designated as distributable code and/or modifiable code. ... You are permitted to re-distribute the distributable code and the modified or derivative works of the modifiable code only as part of your Developer Software for non-commercial or commercial use; provided that you shall only distribute such code subject to a license agreement that protects Broadcom's and its licensors' interests consistent with the terms contained in this Agreement."
- §2, open source: OSS components are licensed under `open_source_licenses.txt`, other accompanying materials, or https://legaldocs.broadcom.com.
- §3 restrictions. You will not "(iii) modify, create derivative works of, reverse engineer, reverse compile, or disassemble the Software except as expressly permitted in Section 2" or "(iv) distribute, sell, lease, rent, lend, or sublicense any part of the Software to any third party except as expressly permitted in Section 2". Nor will you use it to "(a) circumvent any technical restrictions of Broadcom Offerings or violate any additional licensing terms applicable to Broadcom Offerings". The restrictions do not apply where they contradict mandatory local law, "including ... law implementing the EC Software Directive".
- §4: "You may not remove, delete or modify any copyright statements in the Software."
- §5, trademarks: you may not use Broadcom's marks in a way "that suggests your Developer Software is certified or endorsed by Broadcom".
- §6: no support entitlement, even for customers who bought Broadcom support.
- §7: "You or Broadcom each may terminate this Agreement for any reason at any time. You agree, upon termination, to destroy all copies of the Software".
- §9: liability is capped at US $1,000. §10: you indemnify Broadcom for "the distribution or use of your Developer Software by you or anyone else". §13: California law, no assignment.
- Before the removal, the developer-portal VDDK page said that by downloading you agree to "Broadcom's Software Development Kit License Agreement" (snippet only). — [developer.broadcom.com VDDK 8.0 page (now 404)](https://developer.broadcom.com/sdks/vmware-virtual-disk-development-kit-vddk/8.0)

**What vendors and open-source projects actually do**
- Commvault ships VDDK inside its Virtual Server Agent package (`Base\VMware\VDDK\VDDK801\bin`, `VDDK800`, `VDDK703` ... `VDDK602`): "includes the certified VDDK version(s) that can be used for backups." — [Commvault docs 11.42](https://documentation.commvault.com/11.42/commcell-console/vddk_support_for_virtual_server_agent_with_vmware_01.html). ShapeBlue's correction notes that partners with a signed redistribution agreement do ship VDDK, citing Commvault. — [ShapeBlue](https://www.shapeblue.com/broadcom-vddk-download-vmware-to-kvm/)
- Veeam bundles VDDK libraries in its Backup Transport install folder and updates them periodically (older blog). — [virtualtothecore](https://www.virtualtothecore.com/vddk-libraries-version-im-using-veeam-backup-replication)
- nbdkit "requires VMware's proprietary VDDK library that you must download yourself". It now also states "The VDDK library was withdrawn by VMware." — [nbdkit-vddk-plugin(1)](https://libguestfs.org/nbdkit-vddk-plugin.1.html)
- Red Hat cannot distribute VDDK because it is proprietary Broadcom software. — [Zadara blog](https://www.zadara.com/blog/2026/09/29/broadcom-vddk-restriction-vmware-migration/). ShapeBlue says no Linux distribution or container image bundles VDDK and that mirroring "would shift legal exposure to whoever hosts them". — [ShapeBlue](https://www.shapeblue.com/broadcom-vddk-download-vmware-to-kvm/)

### Inferences
- Open-source projects make users download VDDK themselves because the default licence grants internal-use copies only (§2) and forbids distribution (§3(iv)). Only vendors with a signed redistribution agreement (Veeam, Commvault and others) can bundle it.
- Under the default licence, a commercial vendor would have to make each customer obtain VDDK. Broadcom has now cut off that route for customers, so bundling under a redistribution agreement is effectively the only viable path for a new product.
- §7 (termination at will) means VDDK access is revocable. This is a real business-continuity risk for a product that depends on it.
- The generic SDK licence text does not itself restrict use to "backup and recovery". Broadcom's claim that this is "the approved use case per the SDK license" presumably rests on the VDDK-specific licence or the redistribution agreement, which were not found publicly. Treat backup-only as the binding constraint.
- If you redistribute, expect at least these conditions from the generic licence: keep copyright notices (§4), make your own EULA protect Broadcom (§2), no endorsement claims (§5), no modification or reverse engineering (§3), and ship `open_source_licenses.txt` obligations for VDDK's bundled OSS (OpenSSL, libcurl, zlib and others). The OSS list is per the 8.0 guide's open-source modules page — [docs.vmware.com VDDK OSS modules](https://docs.vmware.com/en/VMware-vSphere/8.0/vsphere-vddk-programming-guide/GUID-FCD400EB-C07D-4C39-82C1-60AF33573B93.html) (snippet only).

### Gaps
- The VDDK-specific EULA shipped inside the VDDK tarball/zip was not available. It could not be confirmed that the docs.broadcom.com SDK licence PDF is word-for-word the licence that governs VDDK 9.x. Broadcom calls VDDK "covered by its own developer license".
- The text of the VDDK redistribution agreement is not public. Its fees, attribution or logo rules, certification requirements and audit clauses are unknown.
- The current TAP tier names (Standard vs Select) and whether tiers still exist under Broadcom are unclear. The docs conflict, and the TAP FAQ does not describe tiers. Older VMware-era TAP blog posts (2018–2021) mentioned a program fee, but the current FAQ says there is none. The exact older post was not verified.

## 3. Current VDDK versions (8.x / 9.x), supported ESXi/vCenter, and platform support

### Takeaway
Current lines are VDDK 8.0.x (latest 8.0.3, June 2024) and 9.x (9.0.0.0 June 2025, 9.0.1.0 Sep 2025, and a 9.1 that pairs with vSphere 9.1 from May 2026). Since VDDK 5.0 the VDDK version tracks the vSphere version. Builds exist for Linux and Windows on x86-64 only. Tested proxy OSes for 9.1 are Windows Server 2022/2025 and RHEL 9.x, SLES, Oracle Linux, CentOS Stream 9 and Photon.

### Cited Findings
- "Since VDDK 5.0, the VDDK version number matches the vSphere version number" (paraphrase of the intro page). — [developer.broadcom.com VDDK intro](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkIntro.3.5.html)
- VDDK 8.0.3: "VDDK 8.0.3 with ESXi 8.0 Update 3 | 25 June 2024 GA | VDDK build 23950268". It supports vSphere 8.0 U3, adds SAN transport for NVMe-oF on a Linux proxy, and fixes NBD traffic not binding to a separate vmk on the same subnet (snippet only; the URL now redirects to a techdocs 404). — [VDDK 8.0.3 release notes](https://docs.vmware.com/en/VMware-vSphere/8.0/rn/vddk-803-release-notes.html)
- VDDK 9.0.0.0 was released in June 2025, and nbdkit added support for it. — [AlmaLinux nbdkit commit](https://git.almalinux.org/rpms/nbdkit/commit/58b3d4e63e5ab71fdb666c68048e735928ca09c7). nbdkit's latest tested version is "VDDK 9.0.1.0 (released Sep 2025)" and its minimum is VDDK 6.7. — [nbdkit-vddk-plugin(1)](https://libguestfs.org/nbdkit-vddk-plugin.1.html)
- VDDK 9.0.1 is certified for vSphere 9.0. IBM Storage Protect for VE 8.2.1 moved from VDDK 8.0.3 to 9.0.1 and supports vSphere 9.0, 8.0 and 7.0. — [IBM community blog, Apr 2026](https://community.ibm.com/community/user/blogs/pradeep-sharma/2026/04/20/ibm-storage-protect-for-ve-821-vmware9) (vendor community post)
- VDDK 9.1 release notes, on techdocs and now 404 (snippet only). "VDDK 9.0.1 was tested with and supports Windows Server 2025 as a backup proxy." The tested HotAdd proxy OSes are Windows Server 2022 and 2025, CentOS Stream 9, Oracle Linux 8.9, RHEL 9.1/9.2/9.3, SLES 12 SP5 and 15 SP5, and Photon 4 and 5. Ubuntu is not listed, and "backup vendors may support additional operating systems". — [VDDK 9.1 release notes](https://techdocs.broadcom.com/us/en/vmware-cis/vsphere/vsphere-sdks-tools/9-1/release-notes/virtual-disk-development-kit/virtual-disk-development-kit-91-release-notes.html)
- The older VDDK 8.0.2 list includes RHEL 7.9 to 9.0 and Windows Server 2016 to 2022 (snippet only). — [VDDK 8.0.2b release notes](https://vdc-repo.vmware.com/vmwb-repository/dcr-public/47f6919c-a87c-488b-8594-310ec497c8b1/31cc1678-bf87-4732-abe2-116f76b6a851/VDDK-802b-ReleaseNotes.html)
- vSphere 9.1 was released on 12 May 2026 (secondary). — [Aliyun developer article](https://developer.aliyun.com/article/1735535); [vcdx181.com](https://vcdx181.com/2026/05/19/vmware-cloud-foundation-9-1-now-available-key/)
- The old VDDK 9.0 download page listed one Linux build and one Windows build and described VDDK as "a collection of C libraries, code samples, utilities, and documentation" (snippet only; now 404). — [developer.broadcom.com VDDK 9.0](https://developer.broadcom.com/sdks/vmware-virtual-disk-development-kit-vddk/9.0)
- nbdkit: "This plugin is only supported on the x86-64 architecture." VDDK 8.0.2 and later fails to load `libssl.so.3` on RHEL 8 (workaround: use 8.0.1). VDDK 7 and later cannot open "Independent"-mode disks. VDDK must not go on a system path such as /usr/lib because it bundles conflicting libraries; use `libdir=` instead. — [nbdkit-vddk-plugin(1)](https://libguestfs.org/nbdkit-vddk-plugin.1.html)
- Commvault rules: a single machine "can only support one active VDDK version". Once a proxy loads a newer VDDK it cannot revert. Commvault used VDDK 7.0.2 rather than 7.0.3 on Windows because of "NAS transport mode issues". — [Commvault docs 11.42](https://documentation.commvault.com/11.42/commcell-console/vddk_support_for_virtual_server_agent_with_vmware_01.html)
- Red Hat MTV issue: VDDK 9 had been released while MTV 2.10 documentation still mentioned and tested VDDK 8, which shows lag in partner adoption. — [Red Hat MTV-4363](https://issues.redhat.com/browse/MTV-4363)
- Veeam: support for vSphere 9 reportedly starts with VBR 12.3.1 (build 12.3.1.1139). A Veeam forum reply said vSphere 9.1 support was planned for v13.1. — [Veeam forum: vSphere 9 compatibility](https://forums.veeam.com/vmware-vsphere-f24/vsphere-9-compatibility-t102605.html); [Veeam KB2443](https://www.veeam.com/kb2443) (KB not fully read)

### Inferences
- A .NET 10 backend needs separate native packages for win-x64 and linux-x64. There is no ARM64 VDDK, so an ARM-based proxy or appliance cannot use VDDK.
- Pick the proxy OS carefully: RHEL 9 or Windows Server 2022/2025 are the safe choices. Ubuntu or Debian would be "vendor-supported" rather than VMware-tested, and RHEL 8 has libssl issues with VDDK 8.0.2 and later.
- Expect to ship and test several VDDK versions side by side, as Commvault does, and to run one VDDK version per process. Separate worker processes per VDDK version fit this constraint.

### Gaps
- The official VDDK 9.0 and 9.1 compatibility matrices (which ESXi and vCenter versions each VDDK supports backward) could not be read. The techdocs pages return 404, and the IBM compatibility guide needs a login.
- The exact release date of VDDK 9.1 and any later 9.x build are not confirmed. One search snippet attributed to Commvault mapped "vCenter 9.x → VDDK 9.1.0, 8.x → VDDK 8.0.3", but the Commvault page actually fetched (11.42) does not show it.

## 4. Calling vixDiskLib from .NET: P/Invoke, wrappers, open-source users

### Takeaway
VDDK is a C library (vixDiskLib, plus vixMntapi for mounting). No maintained .NET wrapper exists. The only one found is an abandoned VB.NET P/Invoke wrapper for VDDK 1.0 on the VMware forums. The main open-source consumers are C (nbdkit-vddk-plugin, used by virt-v2v, MTV/Forklift, CloudStack and ShapeBlue) and Go (VMware's archived `virtual-disks`). A .NET vendor would write its own P/Invoke layer from the VDDK headers.

### Cited Findings
- A VMware community thread asking for a VDDK .NET wrapper points to a VDDK 1.0 wrapper written in VB with a `_native` P/Invoke module ("Any incomplete p/invokes marked with "n/a" are incomplete"). Others report it breaking on VDDK 1.1, for example `VixDiskLib_Init` rejecting null arguments. — [VMware communities: VDDK .NET wrapper](https://communities.vmware.com/t5/Virtual-Disk-Development-Kit/VDDK-NET-wrapper/m-p/2067613)
- A second thread asking for an up-to-date wrapper found only an old wrapper that covers VixDiskLib, not VixMntapi. — [VMware communities](https://communities.vmware.com/message/2333290)
- `VixDiskLib_Init()` has been superseded by `VixDiskLib_InitEx()`. — [VDDK API docs (vixDiskLib functions)](https://vdc-download.vmware.com/vmwb-repository/dcr-public/6335f27c-c6e9-4804-95b0-ea9449958403/c7798a8b-4c73-41d9-84e8-db5453de7b17/doc/vddkFunctions.6.3.html)
- The core API calls include `VixDiskLib_ConnectEx()` and `VixDiskLib_ListTransportModes()`. If SAN is the only transport specified and it is not available, `ConnectEx` succeeds but the first `VixDiskLib_Open()` fails. — [developer.broadcom.com Advanced Transport APIs](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkFunctions.6.11.html)
- nbdkit-vddk-plugin takes `libdir=` (the unpacked VDDK directory), `transports=`, `thumbprint=` (SHA1), `port=` (default 443) and `nfchostport=` (default 902). VDDK uses `libdir` to load its own plugins, and a wrong path gives "reduced functionality". — [nbdkit-vddk-plugin(1)](https://libguestfs.org/nbdkit-vddk-plugin.1.html)
- virt-v2v supports VDDK as an input transport, which is "usually the fastest and most flexible". ShapeBlue Migrate's changed-block-tracking path reads through nbdkit's VDDK plugin. — [ShapeBlue](https://www.shapeblue.com/broadcom-vddk-download-vmware-to-kvm/)
- VMware's own Go library for VDDK is `vmware-archive/virtual-disks`, which is archived. — [GitHub vmware-archive/virtual-disks](https://github.com/vmware-archive/virtual-disks)
- Clean-room replacements: "OpenVDDK" is described as a GPL-3.0, C-ABI-compatible drop-in replacement, "promising but not production-proven", without vSAN coverage. — [byteiota](https://byteiota.com/broadcom-pulls-vmware-vddk-exit-door-bolted/) (blog, unverified). Cloudbase describes "OpenVixDiskLib", an open-source VixDiskLib replacement. — [LinkedIn post](https://it.linkedin.com/in/rosariospiezia) (unverified)

### Inferences
- The recommended .NET approach is `[LibraryImport]` (source-generated P/Invoke) with `NativeLibrary.SetDllImportResolver`. Resolve `vixDiskLib.dll` or `libvixDiskLib.so` from a per-version VDDK directory, never a system path. Marshal the C structs (`VixDiskLibConnectParams`, `VixDiskLibInfo`) with `[StructLayout(LayoutKind.Sequential)]`. Wrap `VixDiskLibConnection` and `VixDiskLibHandle` in SafeHandles.
- Host VDDK out-of-process, in a native or .NET worker per job, with IPC to the main service. This isolates crashes, allows one VDDK version per process (the Commvault rule), and avoids library conflicts such as VDDK's bundled OpenSSL clashing with .NET's.
- An alternative is to run nbdkit with the VDDK plugin as a sidecar and have .NET speak the NBD protocol to it. This removes most P/Invoke work on Linux but still needs VDDK, and nbdkit is x86-64/Linux-centric.
- GPL-3.0 clean-room reimplementations (OpenVDDK) are probably unusable when linked into a proprietary commercial product, and their reverse engineering may raise licence questions. Do not treat them as a production path without legal review.

### Gaps
- No public, maintained C# or .NET VDDK wrapper (NuGet or GitHub) was found. A direct GitHub code search for "VixDiskLib" in C# was not run because the GitHub MCP was unavailable.
- The exact header and struct layouts for VDDK 9.x were not verified, because the package is not publicly downloadable.

## 5. Transport modes NBD, NBDSSL, HotAdd, SAN (and NFS?): requirements, ports, restrictions, deprecations

### Takeaway
VDDK has four network or proxy transports: NBD, NBDSSL, HotAdd and SAN, plus "file" for local access. The default order is san:hotadd:nbdssl:nbd. NBD and NBDSSL use NFC to ESXi on TCP 902, with vCenter or ESXi API on 443. HotAdd needs the proxy to be a VM with access to the source datastores. SAN needs a physical proxy with raw LUN access and does not work with vSAN or vVols. No evidence was found of a dedicated "NFS transport mode" or of plain NBD being removed. NBDSSL is required for encrypted VMs.

### Cited Findings
**General and defaults**
- The documentation lists five access methods: local file, NBD, NBDSSL, SAN and HotAdd. NFS appears only in the HotAdd section ("HotAdd transport also works with virtual machines stored on NFS partitions"). — [developer.broadcom.com Virtual Disk Transport Methods (6.0-era)](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkDataStruct.5.5.html)
- "default transport priority over the network is san:hotadd:nbdssl:nbd assuming all are available". SAN is preferred even when `ConnectEx` is passed NULL, if SAN storage is reachable. NFC timeout and log settings do not affect SAN or HotAdd. — [developer.broadcom.com Advanced Transport APIs](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkFunctions.6.11.html)

**NBD / NBDSSL**
- NBD uses the NFC (network file copy) protocol and is "the fall-back when other transport methods are unavailable". "NBD is faster and consumes fewer resources than NBDSSL". NBDSSL "uses SSL to encrypt all data passed over the TCP connection", and "VMware recommends encryption for sensitive information, even on a private network". The sum of NFC connection buffers to one ESXi host "cannot exceed 32MB" (vSphere 5/6), and "vCenter Server imposes a limit of 52 connections". In VDDK 6.0 and later, "both SSL certificate verification and SSL thumbprint checking are mandatory and cannot be disabled". — [developer.broadcom.com Transport Methods](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkDataStruct.5.5.html)
- Ports: `port=` defaults to 443 on vCenter or ESXi, and `nfchostport=` defaults to 902 for the NFC connection to ESXi. A SHA1 thumbprint is required for remote connections. — [nbdkit-vddk-plugin(1)](https://libguestfs.org/nbdkit-vddk-plugin.1.html)
- VDDK 8.0.2: "for datacenters with encrypted disks, NBDSSL transport mode is required", and clusters must enable vSphere HA so that all hosts have the crypto key ID. ESXi 8.0 U2 and VDDK 8.0.2 also make NFC server log-level changes harder "for security reasons", requiring Config Store JSON edits (snippet only). — [VDDK 8.0.2 release notes](https://docs.vmware.com/en/VMware-vSphere/8.0/rn/vddk-802-release-notes.html)
- VDDK 9.1: "In VDDK 9.1, indirect connections to ESX hosts are allowed", and the doc sentence "in NBD mode, VDDK supports only direct connections to ESX" was removed. "VMware Cloud on AWS does not support NBD or NBDSSL", with no plans to support it (snippet only). — [VDDK 9.1 release notes](https://techdocs.broadcom.com/us/en/vmware-cis/vsphere/vsphere-sdks-tools/9-1/release-notes/virtual-disk-development-kit/virtual-disk-development-kit-91-release-notes.html)
- VDDK 8.0.3 fixed NBD traffic not binding to a separate vmk adapter on the same subnet. Dedicated backup vmk networks are a supported pattern (snippet only). — [VDDK 8.0.3 release notes](https://docs.vmware.com/en/VMware-vSphere/8.0/rn/vddk-803-release-notes.html)
- Commvault describes NBDSSL as NBD with the proxy-to-ESX data stream encrypted. — [Commvault: NBD and NBDSSL transport](https://documentation.commvault.com/v11/commcell-console/nbd_and_nbdssl_transport_for_vmware.html)

**HotAdd**
- "The HotAdd backup proxy must be a virtual machine." "The HotAdd proxy must have access to the same datastore as the target virtual machine". It "works only on virtual machines with SCSI disks and is not supported for backing up virtual IDE disks". It works with NFS-backed VMs. — [developer.broadcom.com Transport Methods](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkDataStruct.5.5.html)
- Commvault: the ESX host that runs the proxy must have access to all datastores of the VM. With VDDK 6.5 and later, VMware recommends a paravirtual SCSI (PVSCSI) controller on the proxy. — [Commvault: Configuring Transport Modes](https://documentation.commvault.com/v11/commcell-console/configuring_transport_modes_for_vmware.html)
- The 8.0 guide describes HotAdd as the appliance model, where backup runs inside a VM, and SAN as applying only to physical machines. — [docs.vmware.com VDDK 8.0 guide, transport page](https://docs.vmware.com/en/VMware-vSphere/8.0/vsphere-vddk-programming-guide/GUID-83F766E8-27A6-4132-876F-96EC05ECD094.html) (snippet only)

**SAN**
- "A SAN backup proxy must be a physical machine." It requires access to SAN storage and "must be able to access the LUN as a raw device". It is "not supported for backup or restore of virtual machines residing on VVol datastores", and vSAN "does not support SAN transport". — [developer.broadcom.com Transport Methods](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkDataStruct.5.5.html)
- Commvault: "SAN transport mode cannot be used if the proxy computer is a virtual machine." — [Commvault: Configuring Transport Modes](https://documentation.commvault.com/v11/commcell-console/configuring_transport_modes_for_vmware.html)
- VDDK 8.0.3 adds SAN transport over NVMe-oF for Linux backup proxies (snippet only). — [VDDK 8.0.3 release notes](https://docs.vmware.com/en/VMware-vSphere/8.0/rn/vddk-803-release-notes.html)

**"NFS" or "NAS" mode**
- No source describes a VDDK "NFS transport mode" for vSphere 8 or later. Commvault mentions "NAS transport mode issues" in VDDK 7.0.3 without defining the term. — [Commvault docs 11.42](https://documentation.commvault.com/11.42/commcell-console/vddk_support_for_virtual_server_agent_with_vmware_01.html)

**Paths that avoid VDDK (relevant if VDDK access is denied)**
- The Proxmox ESXi import wizard adds ESXi or vCenter as storage, reads the flat VMDK files directly (the source VM must be powered off) and fetches boot data on demand. It was tested with ESXi 6.5 to 8.0 and has vSAN limitations. — [Proxmox forum announcement](https://forum.proxmox.com/threads/new-import-wizard-available-for-migrating-vmware-esxi-based-virtual-machines.144023/post-647740); [computingforgeeks](https://computingforgeeks.com/migrate-vmware-esxi-to-proxmox-ve/). Zadara lists Proxmox's native import utility, OVF export, shared-datastore overlay and in-guest replication as unaffected by the VDDK removal. — [Zadara blog](https://www.zadara.com/blog/2026/09/29/broadcom-vddk-restriction-vmware-migration/)
- ShapeBlue's overlay method (qcow2 overlay, no VMware library) requires the source datastore and destination on the same NFS export. ShapeBlue is extending its read paths that do not need VDDK. — [ShapeBlue](https://www.shapeblue.com/broadcom-vddk-download-vmware-to-kvm/)

### Inferences
- For an on-prem product, the minimum viable VMware support is NBDSSL from any proxy, which needs TCP 443 to vCenter/ESXi and 902 to every ESXi host. HotAdd, from a proxy VM per cluster or datastore set, is the performance tier. SAN is a niche option for physical proxies on FC, iSCSI or NVMe-oF LUNs, and does not cover vSAN or vVols.
- Default to NBDSSL rather than NBD. NBDSSL is required for encrypted VMs, and plain NBD exposes the backup stream on the network.
- The VDDK-free approach (reading VMDKs over the ESXi datastore HTTP interface, or OVF export) is not a substitute for incremental backup. It lacks efficient changed-block reads from snapshots at the VDDK performance level, and Proxmox's approach requires powered-off VMs. Treat it as a fallback or migration path only (inference, not tested).

### Gaps
- No primary source confirms or denies that plain (non-SSL) NBD is deprecated or blocked in ESXi or VDDK 9.x. No such deprecation was found.
- Licensing requirements for each transport (which vSphere, VVF or VCF editions include the vSphere Storage APIs for Data Protection, and whether HotAdd needs a specific edition) were not researched.
- The current NFC limits (connection and buffer caps) for ESXi 8 and 9 were not found. The 32 MB and 52-connection numbers are vSphere 5/6-era.

## 6. Are the VDDK programming guides and API references publicly accessible without login?

### Takeaway
Only partly, and less so than before. The old VDDK API reference and programming-guide pages under developer.broadcom.com/xapis/virtual-disk-api/latest/ are still public without login (checked 2026-11-01), but their content is 6.0-era. The vSphere 8.0 and 9.x VDDK programming guides and the 9.1 release notes on techdocs.broadcom.com now return 404.

### Cited Findings
- Researcher's HTTP check on 2026-11-01:
  - 200, no login: [developer.broadcom.com/xapis/virtual-disk-api/latest/](https://developer.broadcom.com/xapis/virtual-disk-api/latest/) and [.../vddkIntro.3.5.html](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkIntro.3.5.html).
  - 404: `.../xapis/virtual-disk-api/9.0/` and `.../8.0/`; the [techdocs VDDK 8.0 Programming Guide](https://techdocs.broadcom.com/us/en/vmware-cis/vsphere/vsphere-sdks-tools/8-0/virtual-disk-development-kit-programming-guide.html); the [techdocs VDDK 9.0 Programming Guide](https://techdocs.broadcom.com/us/en/vmware-cis/vsphere/vsphere-sdks-tools/9-0/virtual-disk-development-kit-programming-guide.html); the [VDDK 9.1 release notes](https://techdocs.broadcom.com/us/en/vmware-cis/vsphere/vsphere-sdks-tools/9-1/release-notes/virtual-disk-development-kit/virtual-disk-development-kit-91-release-notes.html); and the VDDK 8.0 U3 release notes (docs.vmware.com redirects to a techdocs URL that returns 404).
  - 200: the techdocs index pages `vsphere-sdks-tools/8-0.html` and `9-0.html`, so the VDDK-specific pages are what was removed.
- The `latest` API reference content is 6.0-era. Image paths reference `API_VDA_002/6.0`, and the text says "In VDDK 6.0 both SSL certificate verification and SSL thumbprint checking are mandatory". — [developer.broadcom.com Transport Methods](https://developer.broadcom.com/xapis/virtual-disk-api/latest/vddkDataStruct.5.5.html)
- ShapeBlue (25 Aug 2026): the Programming Guide page `installing-the-vddk-package.html` returns 404 but still appears in search results. — [ShapeBlue](https://www.shapeblue.com/broadcom-vddk-download-vmware-to-kvm/)
- heise notes that Broadcom added no public notice to the VDDK technical documentation. — [heise](https://www.heise.de/en/news/Broadcom-makes-VMware-exit-difficult-11461334.html)
- Legacy VMware-era copies on vdc-download.vmware.com and vdc-repo.vmware.com returned HTTP 522 or 401 when fetched on 2026-11-01. — [vdc-repo Redistributing VDDK Components](https://vdc-repo.vmware.com/vmwb-repository/dcr-public/8f96698a-0e7b-4d67-bb6c-d18a1d101540/ef536a47-27cd-481a-90ef-76b38e75353c/doc/GUID-282C600E-D986-4592-9206-70BF60DBF684.html)

### Inferences
- The current 8.x and 9.x VDDK programming guide appears to have been withdrawn from public view along with the binaries, or moved behind the TAP or partner portal. Developers without TAP access must rely on the 6.0-era public reference, vendor docs (Commvault and others) and open-source code (nbdkit) for API behaviour.
- The VDDK package itself traditionally ships its documentation and headers. Once access is granted through TAP, documentation will come with the SDK.

### Gaps
- It is unknown whether the 8.x and 9.x guides are now available inside the TAP portal or on support.broadcom.com behind a login. No source confirms where they moved.
- It could not be confirmed whether the techdocs 404s are deliberate removals tied to the VDDK restriction or a site restructuring. The timing and the selective scope (index pages remain) suggest deliberate removal, but that is an inference.
