# Legitimate vSphere (ESXi + vCenter) test-lab access for a small backup-software vendor (state as of 2026-11-01)

Source tags used below: **[Primary]** = Broadcom/VMware/VMUG official page, KB, techdocs, or source code inspected directly; **[Secondary]** = trade press / consultancy / reseller; **[Hearsay]** = forum, Reddit, community posts, single unverified blog. "Code-verified" = I cloned and grepped the govmomi repo myself (commit f04ca79, 2026-10-30).

**Headline finding beyond the original questions:** Broadcom removed the public VDDK download around 25 Aug 2026. VDDK is now reportedly distributed only to selected Technology Alliance Program (TAP) members (and possibly to customers with an active vSphere entitlement via the support portal). For a VM backup vendor this makes **TAP membership the critical path**, separate from how you license ESXi and vCenter. See Q4.

---

## Q1. Evaluation licenses (60-day / 90-day): existence, how to obtain, API/VADP coverage, reuse for CI

### Takeaway
Built-in evaluation mode still exists. It lasts 60 days on vSphere 8 and **90 days on VCF/VVF 9.x**, and it unlocks the full feature set (equivalent to Enterprise Plus on v8), so CBT, snapshots and VADP/VDDK all work. The blocker is getting the **binaries**: Broadcom downloads are tied to an entitlement, and the evaluation clock cannot legitimately be extended. Repeatedly reinstalling to reset it is technically possible, but it is a gray area against "for the period limited by the License Key" terms. It is not a sound legal basis for long-lived CI.

### Cited Findings
- [Primary] vSphere 8 docs: a newly installed vCenter/ESXi runs in evaluation mode for up to 60 days, and "the evaluation mode provides the set of features equal to a vSphere Enterprise Plus license". The vCenter clock starts at install whether or not a license is assigned. The ESXi clock starts at first power-on. You can switch back to evaluation mode only within the original 60-day window. — [docs.vmware.com vSphere 8 licensing](https://docs.vmware.com/en/VMware-vSphere/8.0/vsphere-vcenter-esxi-management/GUID-82C20FE0-306E-448D-A181-C4A822E664A8.html); [ESXi eval](https://docs.vmware.com/en/VMware-vSphere/8.0/vsphere-esxi-upgrade/GUID-17862A54-C1D4-47A9-88AA-2A1A32602BC6.html)
- [Primary] VCF 9.0: "The evaluation mode length of time has increased from 60 days to 90 days in VCF 9.0." An ESX host is in evaluation for up to 90 days from first boot. VCF Operations has its own 90-day clock and must be registered in the VCF Business Services console within it. If a vCenter's 90-day eval expires, all hosts added to that vCenter are disconnected. vCenter upgraded to 9.1 runs in eval for up to 90 days. — [VCF 9.0 Licensing blog (2025-06-24)](https://blogs.vmware.com/cloud-foundation/2025/06/24/licensing-in-vmware-cloud-foundation-9-0/); [VCF 9.1 Licensing Model techdocs](https://techdocs.broadcom.com/us/en/vmware-cis/vcf/vcf-9-0-and-later/9-1/licensing/licensing-overview/licensing-model.html); [VCF 9.0 what's new vSphere](https://techdocs.broadcom.com/us/en/vmware-cis/vcf/vcf-9-0-and-later/9-0/release-notes/vmware-cloud-foundation-90-release-notes/platform-whats-new/whats-new-vsphere.html)
- [Primary] KB 437242: evaluation mode is up to 90 days. Licensing in v9 uses license **files** (activation code via VCF Business Services console, connected or disconnected mode) instead of the old 25-character keys. The KB says nothing on extending or resetting the eval. — [Broadcom KB 437242](https://knowledge.broadcom.com/external/article/437242/getting-started-with-vmware-cloud-founda.html)
- [Primary] VCF/VVF 9.0 binaries are on a separate "VCF and VVF" download page in the Broadcom Support Portal, not the legacy vSphere component pages (released 17 Jun 2025). — [Broadcom KB 401497](https://knowledge.broadcom.com/external/article/401497)
- [Hearsay] A Broadcom community reply says evaluation copies are offered via the support portal's "Trials & Betas" section, but only "to active Broadcom customers". Other users report a "not entitled" message on the download icon. — [Broadcom Community thread](https://community.broadcom.com/vmware-cloud-foundation/question/esxi-download-as-tiral-edition-of-60-days); [vSphere 7 Trial thread](https://community.broadcom.com/vmware-cloud-foundation/discussion/vsphere-7-trial)
- [Secondary] A third-party newsletter claims you can pick vSphere, then an ESXi version, then "Download Trial" for a 60-day ISO. This is unconfirmed by Broadcom docs. — [utclisolutions newsletter](https://newsletter.utclisolutions.com/p/how-to-download-the-new-vmware-esxi-trial-under-broadcom)
- [Primary] William Lam (Broadcom) notes that users without a support entitlement could not generate the **download token** required for the VCF 9 offline depot. He suggested VMUG users run VCF 9.0 in the "in-product 90 day evaluation period" while licensing was sorted out. — [williamlam.com, Jul 2025 (updated 12/08/25)](https://williamlam.com/2025/07/how-to-deploy-vvf-vcf-9-0-using-vmug-advantage-vcp-vcf-certification-entitlement.html)
- [Primary, but older VMware EULA, possibly superseded] VMware EULA evaluation clause: "Your use of the Software is only permitted in a non-production environment and for the period limited by the License Key… an Evaluation License… is provided 'AS-IS' without indemnification, support or warranty". — [VMware EULA (Carahsoft copy)](https://static.carahsoft.com/concrete/files/4316/7528/9001/VMware_EULA.pdf)
- [Primary] Current Broadcom Specific Program Documents (SPDs) for vSphere/VVF/VCF refer to an "embedded evaluation mode" usable, e.g., as a Migration License "for a maximum period allowed by that feature". — [VVF SPD May 2026](https://ftpdocs.broadcom.com/cadocs/0/VVF_SPD_May2026.pdf); [vSphere Standard SPD Feb 2026](https://ftpdocs.broadcom.com/cadocs/0/VMware_vSphere_Standard_SPD_February2026.pdf) (search-result summary only; I did not read the full PDFs)
- [Secondary, unverified] A Medium post on VCF 9.1 licensing claims that if the first eval expires before any license is assigned, recovery generally means a reinstall. — [Medium, Lubomir Tobek](https://medium.com/@lubomir-tobek/inside-vcf-9-1-licensing-how-the-new-model-actually-works-441d7d917513)
- [Primary] William Lam maintains automated nested VCF/VVF 9.1 lab deployment scripts (May 2026). These are the standard way people stand up eval-mode labs. — [williamlam.com VCF 9.1 nested lab (2026-05)](https://williamlam.com/2026/05/vcf-9-1-automated-vmware-cloud-foundation-vcf-vmware-vsphere-foundation-vvf-nested-lab-deployment.html); [lamw/vcf-automated-lab-deployment](https://github.com/lamw/vcf-automated-lab-deployment)

### Inferences
- Eval mode covers full API/VADP: v8 eval equals Enterprise Plus, which includes vCenter, CBT, snapshots and the VADP transports (NBD/NBDSSL, HotAdd, SAN). VCF/VVF 9 eval gives the full product within the deployed bundle.
- For **CI**, "rebuild-from-scratch every run" with nested ESXi + VCSA (each fresh install gets a new 60/90-day clock) works technically. But it uses evaluation software beyond "evaluation" intent. A vendor should treat eval only as a short bootstrap (first 90 days) while TAP NFRs or paid licenses come through.
- Getting **ISOs/OVAs** is the practical bottleneck for a vendor with no Broadcom entitlement. Free ESXi ISO is downloadable with a free account (Q2), but vCenter/VCF bundles appear to need an entitlement or TAP access.

### Gaps
- I found no current Broadcom-published "vSphere trial" landing page for non-customers, and no explicit current Broadcom EULA clause on re-installing to reset evaluation. The current Broadcom End User Agreement text was not retrieved.
- Unverified whether non-customers with a free Broadcom account can download vCenter 8 or VCF 9 ISOs/OVAs today.

---

## Q2. Free ESXi (vSphere Hypervisor): availability, limits, usefulness

### Takeaway
Free ESXi came back with **8.0 Update 3e (April 2025)**, downloadable with a free Broadcom account. It **cannot be managed by vCenter**, and the API is **read-only**. Broadcom explicitly lists "No access to … VADP-based backups". So it is useless as a backup **target under test**, but useful as a no-cost **L0 host to run nested ESXi + vCenter in eval/NFR mode**, and for read-only inventory tests. No free ESXi 9.x appears to exist.

### Cited Findings
- [Primary] Broadcom KB 399823 (ESXi 8.0 U3e, build 24677879) lists these free-version limits: "Cannot be managed by vCenter Server"; "No access to vMotion, DRS, HA, VADP-based backups"; "Usage of APIs to manage hosts is not supported, and may only provide read-only information"; "Up to 2 physical CPUs supported per host"; "8 vCPUs per virtual machine"; "No official Broadcom support". The intended use is listed as "Home labs" and "Development/testing environments". The license key is embedded in the download. — [Broadcom KB 399823](https://knowledge.broadcom.com/external/article/399823/vmware-esxi-80-update-3e-now-available-a.html)
- [Primary] Download path: Broadcom Support Portal → My Downloads → "Free Software Downloads available HERE" → "VMware vSphere Hypervisor" → accept T&Cs. — [Broadcom KB 399823](https://knowledge.broadcom.com/external/article/399823/vmware-esxi-80-update-3e-now-available-a.html); [ESXi 8.0U3e release notes](https://techdocs.broadcom.com/us/en/vmware-cis/vsphere/vsphere/8-0/release-notes/esxi-update-and-patch-release-notes/vsphere-esxi-80u3e-release-notes.html)
- [Secondary] The free edition was discontinued Feb 2024 and reinstated quietly via the 8.0U3e release notes in April 2025. — [heise](https://www.heise.de/en/news/Comeback-at-Broadcom-Update-3e-makes-VMware-vSphere-8-available-for-free-again-10351023.html); [Virtualization Review 2025-04-18](https://virtualizationreview.com/articles/2025/04/18/esxi-is-free-again.aspx); [ServeTheHome](https://www.servethehome.com/broadcom-vmware-esxi-8-0u3e-now-has-a-free-version/)
- [Secondary] In practice, PowerCLI "get" commands work and "set" commands do not. — [vInfrastructure blog, Apr 2025](https://vinfrastructure.it/2025/04/esxi-free-is-back/)
- [Secondary] Stackscale (2026-09-28) says free ESXi is limited to non-production use, no vCenter, max 8 vCPU per VM, no support. — [Stackscale](https://www.stackscale.com/blog/vmware-licensing-2026/)
- [Secondary, unverified] One source states free ESXi is **not offered for 9.0 or 9.1** (citing a May 2026 vSphere 9.1 FAQ). — summarized in search results from [acemq ESXi licensing](https://acemq.com/blogs/esxi-licensing-explained/)

### Inferences
- A free host can't run CBT queries, create snapshots via API, or open VDDK transports. This makes it unsuitable for exercising the backup data path. Its only test value is negative-path testing (detecting "free/unsupported host" and failing gracefully) and read-only inventory parsing.
- Most practical use: install free ESXi 8.0U3e on a physical lab box (2-socket limit) and run nested ESXi VMs (≤8 vCPU each) plus a VCSA on it, with the nested layer in eval or NFR mode (see Q6). Whether this layering complies with the free license terms is not stated. The terms only say "Home labs" and "Development/testing environments".

### Gaps
- I found no Broadcom primary statement on free ESXi 9.x (absence of evidence only).
- The full click-through T&Cs for the free hypervisor (commercial dev use allowed?) were not retrieved.

---

## Q3. VMUG Advantage / EVALExperience (changes 2024–2026, price, commercial use)

### Takeaway
EVALExperience (broad eval keys for $200/yr) **ended at the end of 2024**. Since 2025, VMUG Advantage ($210/yr) gives VCF licenses **only to members who pass VCP-VCF**. Separately, Broadcom gives 32-core vSphere Standard for 1 year to VCP-VVF/VCP-VCF passers. All of these are **personal-use, non-commercial home-lab licenses**. They are **not a legitimate basis for a company's commercial product development/QA**.

### Cited Findings
- [Primary] VMUG Advantage price: "$210 USD per year". Benefits include $125 off VMware certification fees. "VMware Cloud Foundation (VCF) License – Available to members who pass the VCP-VCF certification". Personal-use VCF licenses are "Only available to Advantage Members that are VMware Certified". vDefend and Avi personal-use licenses come after certification. — [vmug.com/advantage](https://vmug.com/advantage)
- [Primary] Broadcom blog (2025-04-14): passing VCP-VVF Admin or VCP-VCF Admin/Architect/Support gives **vSphere Standard, 32 cores, 1 year**. Passing VCP-VCF **plus** VMUG Advantage gives "free personal-use VCF licenses for up to three years". The VMUG membership email must match the Broadcom certification account. Licenses are requested via the Broadcom Certification License Portal (4–6 h after approval). — [VMware VCF blog: Free Home-Lab Licenses for VCPs](https://blogs.vmware.com/cloud-foundation/2025/04/14/free-home-lab-licenses-for-vmware-certified-professionals/)
- [Primary, via search summary of VMUG FAQ PDF, Mar 2025] VCP-VCF + Advantage gives a **128-core, 1-year personal-use, non-production** VCF license without support, renewable annually up to 3 years while membership is active. VCP-DCV/VCAP holders are not eligible. — [VMUG VCP-VCF License FAQ Mar 2025](https://vmug.com/wp-content/uploads/2025/03/VCP-VCF-License-FAQ-March-2025_3.20.pdf); [VMUG FAQ.pdf](https://www.vmug.com/wp-content/uploads/2025/03/FAQ.pdf) (direct fetch returned 404; content known only from search snippets)
- [Primary, not fetched] VMUG press release on the EVALExperience license end (Nov 2024) and an "EvalExperience Update FAQ 11_5_2025" exist. — [VMUG press release PDF](https://vmug.com/wp-content/uploads/2024/11/VMUG_EvalExperience-License_End_Press_Release_Announcement_VCP_Final.pdf); [EvalExperience update FAQ](https://vmug.com/wp-content/uploads/2024/11/EvalExperience-Update-VMUG-Advantage-FAQ-11_5_2025.pdf) (both 404 on fetch)
- [Primary] William Lam: the entitlement is "non-commercial VMware Cloud Foundation (VCF) licenses" for personal use. As of **12/08/25**, VMUG + VCP-VCF users get VCF 5.x and 9.x licensing and **download tokens** via the BSP "alpine-certificate" page. Before that, a metadata workaround was needed because members lacked support entitlements. — [williamlam.com](https://williamlam.com/2025/07/how-to-deploy-vvf-vcf-9-0-using-vmug-advantage-vcp-vcf-certification-entitlement.html)
- [Primary, older] Historic EVALExperience terms: "Use of the Software is only permitted in a non-production environment and for the period limited by the License Key"; licenses are "for personal use in a non-production environment". — search summary of VMUG materials; [vbrownbag 2015 (historic)](https://vbrownbag.com/2015/01/homelabers-assemble-vmware-evalexperience/)
- [Secondary] The Broadcom VCP path to a 3-year lab costs ≈ $325 (membership + discounted exam), per a 2026 home-lab guide. — [vmwaremadesimple 2026](https://vmwaremadesimple.com/articles/how-to-build-a-vmware-home-lab-on-a-budget.html)

### Inferences
- "Personal use" + "non-commercial" excludes using these keys in a company's product-development/QA lab or CI. An individual engineer's home lab used to learn the product is the furthest these terms stretch, and the vendor should not rely on it. This conclusion is strong but not a quoted contract clause, since the current EULA text was not retrieved.
- The VCP exam requirement also makes it a per-person credential, not an organizational license.

### Gaps
- Current (2026) VMUG Advantage EULA wording on commercial use was not retrieved (PDFs returned 404). Whether anything changed after the 12/08/25 update is unconfirmed.

---

## Q4. Partner NFR / development licenses (Broadcom partner programs) — and VDDK access

### Takeaway
For a backup ISV the correct program is the **VMware Technology Alliance Partner (TAP) program** (tap.broadcom.com). It offers **NFR licenses "with support" for interoperability development, testing and certification**, plus gated SDKs. Broadcom's NFR KB explicitly routes "adjacent offerings such as backup" to TAP rather than the reseller (BAP) NFR track. Since ~25 Aug 2026, TAP is also reportedly the **only route to VDDK**. Joining TAP is effectively mandatory for a VADP-based backup product.

### Cited Findings
- [Primary] TAP portal benefits: "Access to Not-for-Resale (NFR) licenses with support"; certification/interoperability programs and compatibility listings; "public and gated Software Development Kits (SDKs)". TAP NFRs are for "interoperability development, testing, and certification". Broadcom changed authentication, so users may need to activate accounts. — [tap.broadcom.com](https://tap.broadcom.com/)
- [Primary, via search snippet of TAP NFR guide; page unreachable on fetch] "TAP members will receive NFR licenses solely for the purpose of developing, testing, and supporting integrated/interoperable partner products" and they can't be used in production. Managing NFRs and downloading software requires an unexpired TAP membership. — [TAP NFR guide](https://vmware-alliances.my.salesforce-sites.com/tap/tap_nfr_guide) (DNS failure on fetch)
- [Primary] Broadcom KB 373980 (BAP reseller NFRs): only for BAP Pinnacle/Premier/Select resellers, requested by Broadcom staff, **1-year** duration, **not auto-renewed** (renew within 30 days of expiry), no Broadcom Software Maintenance, production use "strictly prohibited". It explicitly excludes "adjacent offerings such as backup", which are referred to TAP, and describes separate TAP NFRs for VCF requested through the TAP portal. — [Broadcom KB 373980](https://knowledge.broadcom.com/external/article/373980/vmware-not-for-resale-license-keys.html)
- [Primary, older] VMware TAP Journey guide: members must post at least one solution listing on VMware Marketplace within the first 12 months unless waived. — [VMware TAP Partner Journey](https://www.vmware.com/docs/vmware-infg-tech-alliance-partner-final) (pre-/early-Broadcom; may be superseded)
- [Unverified, search-snippet only] An April 2024 TAP partner communication reportedly says the **$1,500 annual TAP fee was eliminated** and NFR support is tied to each NFR license. I could not decode the PDF myself. — [TSANet PartnerComm 4-12-2024](https://www.tsanet.org/wp-content/uploads/2024/04/PartnerComm-4-12-2024.pdf)
- [Secondary, multiple consistent] Broadcom removed public VDDK 8.0/9.0 download pages around **25 Aug 2026** without announcement. Broadcom told press (9 Sep 2026) that "the ability for individual organizations to download VDDK recently changed". It said VDDK remains available for its licensed purpose (backup and recovery) through selected TAP members, and that public downloads were never a formal entitlement. An EU antitrust inquiry was reportedly opened 11 Sep 2026. — [It's FOSS](https://itsfoss.com/news/broadcom-removes-vmware-vddk-downloads/); [heise](https://www.heise.de/en/news/Broadcom-makes-VMware-exit-difficult-11461334.html); [Platform9](https://platform9.com/blog/vddk-no-longer-available/); [Virtualization Howto 2026-09](https://www.virtualizationhowto.com/2026/09/leaving-vmware-just-got-harder-after-broadcom-pulled-vddk-downloads/); [Zadara 2026-09-29](https://www.zadara.com/blog/2026/09/29/broadcom-vddk-restriction-vmware-migration/); [Hystax](https://hystax.com/broadcom-vddk-removed-vmware-migration-impact/)
- [Secondary] Hystax reports that customers with a current vSphere entitlement can still find VDDK in the Broadcom support portal, and TAP members can request it. — [Hystax](https://hystax.com/broadcom-vddk-removed-vmware-migration-impact/)
- [Secondary] Broadcom ended legacy VMware partner programs in favor of invite-only BAP from Feb 2024. — [TechRadar](https://www.techradar.com/pro/broadcom-is-ending-vmwares-channel-program); [GraphOn](https://www.graphon.com/blog/broadcom-cancels-vmware-partner-agreements)

### Inferences
- The TAP application is the single most important action. It covers (a) NFR ESXi/vCenter/VCF licenses legally usable for dev/QA/CI, (b) the VDDK download, (c) the compatibility listing that customers look for. Because NFRs are "with support", it may also give a support channel for VADP bugs.
- TAP being "selected"/curated means acceptance of a small vendor is not guaranteed. Have a fallback (eval bootstrap + paid minimal subscription), but note that a paid subscription alone may not give VDDK redistribution rights.
- **VDDK redistribution** (shipping VDDK libraries inside a commercial product) is a separate licensing question from download access. Its current terms post-Aug-2026 were not established here.

### Gaps
- Current TAP fee/tier structure, NFR quantities/cores per member, NFR duration, and acceptance criteria for new small ISVs: not publicly documented in what I could access (TAP guide and NFR guide require login or were unreachable).
- Whether TAP currently accepts new backup vendors, given that "selected members" have VDDK access.
- Current VDDK EULA/redistribution terms.

---

## Q5. Cheapest paid route: lowest edition with full backup API support, core minimums, terms, prices (2025–2026)

### Takeaway
No public price list exists. Reported list prices are about **$50/core/yr for vSphere Standard (VVS)**, **$135/core/yr for vSphere Foundation (VVF)** and **$350/core/yr for VCF**, with a **16-core-per-CPU minimum**. Whether a **72-core-per-order minimum** still applies is **disputed**. Availability is the bigger problem: vSphere Standard reportedly went end-of-sale 31 Jul 2025 (a refreshed Standard is pending, unpriced as of Oct 2026), VVF was withdrawn in some EMEA countries in Dec 2025, and Essentials/Essentials Plus are gone. The realistic floor for one single-socket 16-core host is roughly **$800/yr (VVS, if a reseller can still sell it) to $2,160/yr (VVF)**. If the 72-core order minimum is enforced, it becomes roughly **$3,600–$9,720/yr**.

### Cited Findings
- [Secondary] Reported list prices per core per year: vSphere Standard $50, VVF $135, VCF $350 (attributed to Veeam Community and Rimini Street, checked 20 Oct 2026). AceMQ's own 2026 quotes: VVF $117–196 (1-yr), $100–160 (3-yr); VCF ~$344 (1-yr), $257–348 (3-yr). 1/3/5-year terms are offered, and longer terms are cheaper per year. Article updated 2026-10-27. — [AceMQ VVF vs VCF](https://acemq.com/blogs/vmware-cloud-foundation-vs-vsphere-foundation/)
- [Secondary] Indicative vSphere Standard list ~ $55/core/yr. — [C4C Group](https://www.c4cgroup.co.uk/guides/vmware-renewal-calculator/)
- [Secondary] A reseller list shows vSphere Standard at 51.25 EUR net/core/yr as of Sept 2026. — [everywan](https://everywan.com/en/blog/vmware-vsphere-standard-announcement-that-does-not-exist)
- [Primary-reported] **72-core minimum**: an Arrow (distributor) email reported by The Register said that from 10 Apr 2025 the minimum rises "from 16 to 72 cores per command line". VMware did not confirm. Late renewals reportedly carry a 20% penalty on the renewed first-year price. — [The Register 2025-03-28](https://www.theregister.com/2025/03/28/arrow_vmware_licensing_change/)
- **Conflict on 72-core status:** AceMQ (Oct 2026) says "72 cores minimum per order, enforced since April 2025" — [AceMQ](https://acemq.com/blogs/vmware-cloud-foundation-vs-vsphere-foundation/). Stackscale (Sep 2026) says it was announced but "withdrawn about a month later, so it is not in force" — [Stackscale](https://www.stackscale.com/blog/vmware-licensing-2026/). C4C and Glaeser IT also say it was dropped — [C4C](https://www.c4cgroup.co.uk/guides/vmware-renewal-calculator/); [Glaeser IT](https://glaeser-it.com/en/ratgeber/vmware-broadcom-lizenzaenderungen-kmu). **Unresolved.**
- [Secondary] vSphere Standard "went end of sale on 31 July 2025". Standard and Enterprise Plus lines reach EOL in Oct 2027 (vSphere 8 EOGS 11 Oct 2027). Broadcom executives said in Sept 2026 that an updated vSphere Standard is coming, with no price or date published as of 22 Oct 2026. — [AceMQ](https://acemq.com/blogs/vmware-cloud-foundation-vs-vsphere-foundation/); [Stackscale 2026-09-28](https://www.stackscale.com/blog/vmware-licensing-2026/)
- [Secondary] The "new vSphere Standard" was signalled only in executive press interviews at Explore Las Vegas (2 Sep 2026), with no press release. At Explore on Tour Frankfurt (13–14 Oct 2026) there was "no pricing, no core-count cap, no GA date". — [everywan](https://everywan.com/en/blog/vmware-vsphere-standard-announcement-that-does-not-exist); [zsvirt Virtualization Watch 004](https://zsvirt.io/blog/virtwatch-weekly-issue-004)
- **Conflict on Standard availability:** in Dec 2025 a Broadcom representative told The Register that vSphere Standard "remains fully available across EMEA today", contradicting the "end of sale 31 Jul 2025" claim. — [The Register 2025-12-11](https://www.theregister.com/2025/12/11/vmware_kills_vsphere_foundation_parts_emea/). A **vSphere Standard SPD dated February 2026** exists — [Broadcom SPD](https://ftpdocs.broadcom.com/cadocs/0/VMware_vSphere_Standard_SPD_February2026.pdf). Availability is likely region- and reseller-dependent.
- [Primary-reported] Broadcom confirmed VVF is "no longer available in some EMEA countries, but is still available in most" (Dec 2025). Customers are told to check with local partners, and countries were not named. — [The Register 2025-12-11](https://www.theregister.com/2025/12/11/vmware_kills_vsphere_foundation_parts_emea/); [Techzine](https://techzine.eu/news/infrastructure/137261/broadcom-discontinues-vmware-vsphere-foundation-in-parts-of-emea); [BornCity (DE)](https://borncity.com/blog/?p=319226)
- [Primary] vSphere Essentials Plus: perpetual license discontinued and not usable with vSphere 9.0. Sale of VVEP discontinued. — [Broadcom KB 409233](https://knowledge.broadcom.com/external/article/409233/vsphere-essentials-plus-license-compatib.html)
- [Secondary] vSphere 9 features ship only in VVF and VCF bundles. Standard/Enterprise Plus are limited to 8.0 U3. — [Stackscale](https://www.stackscale.com/blog/vmware-licensing-2026/); [eurokommerz](https://eurokommerz.at/blog/vmware-renewal-standard-vvf-vcf-which-tier/)
- [Secondary] Broadcom publishes no public pricing. Quotes come only via partners. — [V2 Cloud](https://v2cloud.com/blog/vmware-vsphere-licensing-and-costs); [Redress Compliance](https://redresscompliance.com/broadcom-vmware-pricing-report-2026)

### Inferences
- Edition with backup APIs: historically all paid editions (Essentials and up) included VADP/CBT. vSphere Standard (with vCenter Standard) should therefore be the cheapest edition with full backup API coverage. I did not verify this against the Feb 2026 Standard SPD feature list.
- A paid **vSphere Standard 8** test lab caps testing at vSphere 8.0U3. Testing against **vSphere 9** (needed for customers on VVF/VCF 9) requires a VVF or VCF subscription, NFR, or eval.
- Rough lab cost math (my calculation, from the third-party prices above): 1 host × 1 socket × 16 cores = 16 cores. VVS: 16 × $50 = **~$800/yr**. VVF: 16 × $135 = **~$2,160/yr**. If 72-core order minimum: VVS ~$3,600/yr, VVF ~$9,720/yr, VCF ~$25,200/yr. A nested lab on one licensed physical host can host many nested ESXi hosts, but each nested ESXi also needs licensed cores (see Q6).
- For a German vendor (German press sources), VVF availability must be confirmed with a local reseller given the EMEA withdrawal.

### Gaps
- Minimum subscription term: sources mention 1-, 3- and 5-year terms, but no authoritative statement on a 1-year minimum for new small customers.
- No Broadcom primary price list. All prices are third-party.
- Whether a nested ESXi host's licensed cores count the vCPUs assigned to it (16-core minimum per nested "CPU") was not verified for the v9 license-file model.

---

## Q6. Nested ESXi labs (ESXi-on-ESXi, KVM, Hyper-V, Proxmox) and simulators (vcsim)

### Takeaway
**Nested ESXi on ESXi** is the well-trodden, Broadcom-employee-maintained path (William Lam's Nested ESX appliance and VCF/VVF 9.1 lab scripts). It gives real ESXi and vCenter for CBT/snapshot/VDDK NBD/NBDSSL/HotAdd testing, but SAN transport and realistic I/O performance are not representative. **KVM/Proxmox** works with caveats (CPU type host, vmxnet3, past AMD kernel bugs). **Hyper-V** is practically unusable for modern ESXi (no NIC driver). **vcsim** (govmomi) simulates the vSphere SOAP control plane well, including snapshot create/revert/remove, the CBT enable flag and ExportSnapshot via HttpNfcLease. It does **not** implement `QueryChangedDiskAreas` or any real VDDK data path, so it suits unit/integration tests of orchestration logic only.

### Cited Findings
- [Primary] William Lam refreshed the **Nested ESX 9.1 virtual appliance** and the **VCF/VVF 9.1 fleet deployment script** (May 2026). The appliance no longer ships with a default password, and appliance downloads are re-hosted on the new Flings site. — [williamlam.com 2026-05](https://williamlam.com/2026/05/vcf-9-1-automated-vmware-cloud-foundation-vcf-vmware-vsphere-foundation-vvf-nested-lab-deployment.html); [Nested ESXi virtual appliance page](https://williamlam.com/Nested-virtualization/Nested-esxi-virtual-appliance)
- [Primary] Broadcom Flings license: internal testing/evaluation, "You must not use the Flings Offering in a production environment". — [Flings License Agreement](https://support.broadcom.com/web/ecx/support-content-notification/-/external/content/ReleaseAnnouncements/0/25729)
- [Hearsay] **Proxmox/KVM**: Proxmox 8 initially broke nested ESXi 8 on AMD (KVM did not properly expose the FLUSHBYASID CPU flag). The fix landed in proxmox-kernel-6.2.16-19. The nested NIC is typically vmxnet3. This is a 2023 thread; current state for ESXi 9 is unverified. — [Proxmox forum thread](https://forum.proxmox.com/threads/proxmox-8-breaks-nested-virtualization.130509/)
- [Hearsay] **Hyper-V**: older ESXi (≤6.0) worked only with the Legacy Network Adapter + net-tulip VIB. Later ESXi versions find no NIC ("no Hyper-V network driver in later versions"). Workarounds such as USB NICs and custom ISOs report PSODs. Hyper-V officially supports only nested Hyper-V. — [VMware community thread](https://communities.vmware.com/message/2895179); [turbogeek guide](https://turbogeek.co.uk/?p=2358); [InfoSec Institute forum](https://community.infosecinstitute.com/discussion/comment/1006431)
- [Code-verified] vcsim ("A vCenter and ESXi API based simulator… intended for testing consumers of the API"). It is usable from any language, ships as a Docker image `vmware/vcsim` and release binaries, and has an `-esx` flag for standalone ESX simulation and `-load` for loading a saved inventory. — [govmomi vcsim README](https://github.com/vmware/govmomi/blob/main/vcsim/README.md)
- [Code-verified] Implemented in `simulator/`: `CreateSnapshot_Task`, `CreateSnapshotEx_Task`, `RemoveSnapshot_Task`, `RevertToSnapshot_Task`, `RevertToCurrentSnapshot_Task`, `RemoveAllSnapshots_Task` ([virtual_machine.go](https://github.com/vmware/govmomi/blob/main/simulator/virtual_machine.go), [snapshot.go](https://github.com/vmware/govmomi/blob/main/simulator/snapshot.go)); `ExportSnapshot` returning an `HttpNfcLease` with HTTP download URLs under `/nfc/` ([snapshot.go](https://github.com/vmware/govmomi/blob/main/simulator/snapshot.go), [http_nfc_lease.go](https://github.com/vmware/govmomi/blob/main/simulator/http_nfc_lease.go)); `ReconfigVM` applies `changeTrackingEnabled` to `vm.Config` (virtual_machine.go ~L372); `AcquireCloneTicket` / `AcquireGenericServiceTicket` stubs ([session_manager.go](https://github.com/vmware/govmomi/blob/main/simulator/session_manager.go)).
- [Code-verified] **`QueryChangedDiskAreas` is NOT implemented** in the simulator. It appears only in client bindings (`vim25/methods`, `vim25/types`, `object/virtual_machine.go`). No simulator code sets a disk backing `changeId`. Unimplemented methods return a `MethodNotFound` fault (simulator.go L240). `ExportVm` was not found in the simulator either. — [govmomi repo](https://github.com/vmware/govmomi) (commit f04ca79, 2026-10-30)
- [Primary] CBT API semantics a real lab must exercise: the change interval runs from `changeId` to the passed snapshot; false positives are allowed and no misses are guaranteed. Pitfalls include CBT enabled while snapshots already exist (FileFault "Unknown change epoch" with `changeId="*"`) — fix by enabling CBT with no snapshots, then snapshotting. — [vSphere API VirtualMachine](https://developer.broadcom.com/xapis/vsphere-web-services-api/latest/vim.VirtualMachine.html); [Broadcom KB 343616](https://knowledge.broadcom.com/external/article/343616/querychangeddiskareas-api-with-changeid.html); [Broadcom KB 320557](https://knowledge.broadcom.com/external/article?articleNumber=320557)

### Inferences
- vcsim covers inventory discovery, auth/session, task/event handling, snapshot lifecycle orchestration, CBT-enable reconfig and error injection. It cannot test CBT correctness, VDDK NBD/NBDSSL/HotAdd/SAN reads, or VDDK/disklib behavior. Fake `QueryChangedDiskAreas` responses would need either a contribution to vcsim (it is extensible via custom handlers in Go) or a mock layer in the product.
- A nested ESXi lab (on a licensed or eval physical ESXi host, or on Proxmox/KVM) supports NBD/NBDSSL and **HotAdd** (backup proxy VM on the nested host). **SAN transport** needs real shared FC/iSCSI LUN access and is better tested on physical hosts with iSCSI. Performance numbers from nested labs are not representative.
- Each nested ESXi still needs a license (eval, NFR or paid). Nesting reduces hardware, not licensing.

### Gaps
- Current official Broadcom support statement on nested ESXi (historically "not supported for production") was not retrieved this session.
- No 2025–2026 primary or authoritative source on ESXi 9.x nested on Proxmox/KVM/Hyper-V.

---

## Q7. Hosted labs (VMware Hands-on Labs and others) for third-party development

### Takeaway
VMware Hands-on Labs (HOL) are still free. Since **1 Sep 2025** they require a free **Broadcom Support account with a corporate email** (Gmail, Yahoo and ISP addresses rejected). They are guided, time-limited learning labs, and I found nothing indicating they allow uploading and running third-party product builds. Treat them as unsuitable for development/CI. I found no other legitimate hosted "vSphere sandbox for ISVs" offering.

### Cited Findings
- [Secondary, reporting HOL notice] From Monday 1 Sep 2025, 9:00 AM PT, "all users must sign in with a free Broadcom Support account". "Gmail, Yahoo, and other personal ISP accounts will no longer be accepted." — [vInfrastructure 2025-09-01](https://vinfrastructure.it/2025/09/01/)
- [Secondary] HOL is described as a free/complimentary platform. — [arrayasolutions](https://www.arrayasolutions.com/vmware-free-hands-on-labs-virtualization-education/); [sunucun (2026, low authority)](https://sunucun.com.tr/blog/what-are-vmware-hands-on-labs)

### Inferences
- HOL environments are pre-built, session-time-limited and intended for training. Installing a backup appliance or proxy and pulling VDDK data out of them is almost certainly outside intended use, and likely blocked technically (no arbitrary uploads or egress). This is not verified from HOL terms.
- Hosted alternatives (VMware-based hosted private cloud from cloud providers, bare-metal rental with bring-your-own license) still need a valid license. They solve hardware, not licensing.

### Gaps
- I did not retrieve current HOL Terms of Use, so restrictions on third-party software and session limits are unverified.
- I did not research hourly-billed hosted vSphere offerings from cloud providers or their licensing pass-through (e.g., provider-supplied VCF via VCSP), or whether such providers permit ISV QA workloads.
