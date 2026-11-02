# Broadcom/VMware Technology Partner Programs and Certification for Third-Party vSphere Backup Vendors (state as of Nov 2026)

Research date: 2026-11-01. Many Broadcom TAP portal pages are login-gated, and several KB/doc URLs returned 404 when fetched, so some points rest on public landing/FAQ pages and press coverage. Older or possibly superseded material is flagged **[OLD]**.

**Bottom line for the report writer:** Joining the VMware Technology Alliance Program (TAP) is free and works by self-registration. It is separate from the invitation-only Broadcom Advantage Partner Program, which covers resellers and cloud providers. Until August 2026, TAP was effectively optional for building a VADP backup product, because the VDDK could be downloaded publicly. Since late Aug/Sep 2026, Broadcom has pulled public VDDK downloads and says VDDK access is "still available through select TAP [partners] for its licensed use case ... backup and recovery." In practice, TAP membership, and apparently selection or approval within TAP, is now the only Broadcom-sanctioned way to get VDDK. A signed redistribution agreement is needed to ship VDDK inside a product. Compatibility-guide certification and listing are still optional/marketing items, as far as public sources show.

---

## 1. What happened to VMware TAP and "VMware Ready" after the acquisition; what is the current program; is it invite-only?

### Takeaway
TAP was not abolished. It still runs as the "VMware Technology Alliance Program (TAP)" on a Broadcom-hosted portal (tap.broadcom.com) and is open to self-registration, though applications are reviewed by a TAP Support Team. The invitation-only changes in 2024–2025 hit the channel programs: resellers moved to the Broadcom Advantage Partner Program, and the VCSP program was cut back. They did not hit TAP. I found no source showing that "VMware Ready" was formally retired. The public TAP pages now point to the "VCF Solutions Catalog" and the Broadcom Compatibility Guide (BCG) and no longer mention VMware Ready.

### Cited Findings
- The TAP portal is still live under Broadcom as "Technology Alliance Program (TAP)", run by "VMware by Broadcom". It describes TAP as marking "the beginning of the VMware journey for software and hardware vendors." — [tap.broadcom.com](https://tap.broadcom.com/)
- Resources listed on the TAP portal: Certification/Interoperability Programs; public and gated SDKs; hardware/software certifications; the **VCF Solutions Catalog** (for validated third-party and open-source solutions); VMware by Broadcom Communities; a "Membership Agreement" page describing the current VMware TAP Agreement; and a Broadcom-employee TAP portal. The landing page does not mention VMware Ready. — [tap.broadcom.com](https://tap.broadcom.com/)
- TAP portal notices: Broadcom changed its authentication technology, so users must activate accounts and complete the Broadcom Profile transition. Footer copyright reads 2005–2024. A maintenance notice reads "Saturday, January 24, 2024". — [tap.broadcom.com](https://tap.broadcom.com/)
- Broadcom KB 385486, "The Technology Alliance Program (TAP)": TAP "provides members with the tools and resources necessary to build and deliver solutions". Members can develop, test, integrate and certify interoperability with current VMware products. The KB directs users to the TAP Portal at tap.broadcom.com, and to KB 386537 for TAP user registration on the Broadcom Support Portal. — [Broadcom KB 385486](https://knowledge.broadcom.com/external/article/385486)
- Joining TAP: register at tap.broadcom.com and build a profile selecting Technology Alliance Program. After submission you get basic portal access, and an application goes to the TAP Support Team, "who may contact you with questions or next steps." If your email domain matches an existing TAP member, you are added as a user and no application is needed. — [TAP Help/FAQ](https://tap.broadcom.com/help)
- Broadcom ended VMware's channel partner programs. Statement to The Register: "effective February 5, 2024, Broadcom will be transitioning VMware's partner programs to the invitation-only Broadcom Advantage Partner Program." **[2024, channel/reseller scope, not TAP]** — [The Register, 10 Jan 2024](https://www.theregister.com/2024/01/10/broadcom_ends_vmware_partner_program/)
- Broadcom describes the Advantage Partner Program as a private, invitation-only, evergreen program covering its CA, Symantec and VMware divisions. Its tiers are Pinnacle, Premier and Select, and partner performance is evaluated twice a year (May and November). — [Broadcom partner news](https://news.broadcom.com/partners/broadcom-advantage-partner-program-roadmap-success); [ChannelWeb 2025](https://www.channelweb.co.uk/news-network/2025/broadcom-s-vmware-partner-program-revamp)
- As of mid-2025, Broadcom dropped the "Registered" tier, leaving Pinnacle/Premier/Select. The VCSP program was consolidated on 15 July 2025: non-invited partners were told they would not be renewed, the old program ended 31 Oct 2025, and the new invite-only VCSP took effect 1 Nov 2025. **[Channel/VCSP, not TAP]** — [The Register, 16 Jul 2025](https://www.theregister.com/2025/07/16/vmware_reboots_partner_program_again/?td=rt-3a); [Channel Insider, Oct 2025](https://www.channelinsider.com/channel-business/channel-analysis/broadcom-vmware-vcsp-oct-2025/); [Virtualization Review, 23 Sep 2025](https://virtualizationreview.com/articles/2025/09/23/broadcom-reshapes-vmware-partner-ecosystem-again.aspx)
- Broadcom's NFR KB names the "Broadcom Technology Alliance Partner (TAP) program" as the right venue for partners "developing an adjacent offering like backup". That keeps backup ISVs in TAP rather than the Advantage reseller program. — [Broadcom KB 373980](https://knowledge.broadcom.com/external/article/373980/not-for-resale-vmware-license-informatio.html)
- The Register (Sep 2026) explains that VMware calls third-party developers who work with its products "technology alliance partners," or TAPs. — [The Register, 10 Sep 2026](https://www.theregister.com/virtualization/2026/09/10/vmware-defends-ending-downloads-of-sdk-that-helps-vm-backups-or-migrations-to-rivals/5295421)
- **[OLD, pre-acquisition]** In 2018 VMware relaunched TAP around integration areas and changed NFR policy so partners got only the licenses relevant to their integration area. There was also a Standard tier whose stated requirement, besides a program fee, was publishing on VMware Marketplace within the first year. — [VMware TAP blog (2018-era)](https://blogs.vmware.com/tap/?p=2059); [vcdx181.com, 2018](https://vcdx181.com/2018/07/13/announcing-the-updated-tap-program/)

### Inferences
- "Broadcom Technology Alliance Program", "Broadcom Advantage Partner Program" and a "Build track" are separate things. TAP is the ISV/technology track. Advantage is for resellers, VCSPs and OEMs, and is invitation-only. I found no "Build track" naming in any source; that phrasing may be confusion with other vendors' programs (e.g., "Build" tracks in Microsoft or AWS programs).
- TAP is not invite-only to join (self-registration plus review). However, Broadcom's Sep 2026 VDDK statement says "select TAP", which suggests a further gate inside TAP for VDDK access. See Section 4.
- VMware Marketplace and the VMware Ready branding seem to have been folded into the "VCF Solutions Catalog" and BCG listings. This is inferred from what the current TAP landing page lists and leaves out, not from a retirement announcement.
- The "Saturday, January 24, 2024" maintenance date is internally inconsistent: 24 Jan 2024 was a Wednesday, while 24 Jan 2026 is a Saturday. The year is probably a typo for 2026, which would mean the portal is actively maintained. This is an inference.

### Gaps
- I found no Broadcom press release or FAQ announcing TAP changes after the acquisition. The current TAP Program Guide and Membership Agreement are login-gated, and tap.broadcom.com/membership-agreement returned 404.
- I found no explicit statement on whether "VMware Ready" (including any data-protection badge) is retired or still issued. A Broadcom-hosted TAP staging page still mentioned the VMware Ready logo for hardware certifications, but it is undated ([tap-stgv2.broadcom.com](https://tap-stgv2.broadcom.com/programs-home)). The press-release URL news.broadcom.com/releases/vmw-ready-dp-partner redirected to a generic news index, so its content could not be checked.
- I could not determine whether TAP still has tiers in 2026.

---

## 2. Membership costs, tiers, requirements and benefits

### Takeaway
According to Broadcom's own TAP FAQ, TAP membership is free. Certification/integration programs are "generally free" to join, and BCG listings cost nothing. Listed benefits are NFR licenses with support, entry into certification/interoperability programs (each with a vetting questionnaire), public and gated SDKs, a ticketed technical support desk (Wolken Service Desk) open only after onboarding into a certification program, and BCG listing. I found no public evidence of current paid tiers.

### Cited Findings
- Fees, verbatim: "There is no fee associated with the VMware TAP membership." The FAQ also says certification/integration programs are generally free to join, Wolken Service Desk questions are free but capped based on past usage, and posting to the Broadcom Compatibility Guide costs nothing. — [TAP Help/FAQ](https://tap.broadcom.com/help)
- Certification programs: only TAP members can join. Members click "Join Program" under Programs > All Programs. Programs "generally require a vetting questionnaire that VMware reviews", after which VMware tells the member the next steps. — [TAP Help/FAQ](https://tap.broadcom.com/help); [TAP programs-home](https://tap.broadcom.com/programs-home)
- Programs-home says each program has its own vetting process, and certified products can be listed on the compatibility guide. The first person from a company to request a program triggers onboarding, and Partner Admins approve later users from the same company. — [TAP programs-home](https://tap.broadcom.com/programs-home)
- Support: technical, certification or program questions "require TAP membership and successful onboarding into a certification/integration program" and are handled through the Wolken Service Desk. — [TAP Help/FAQ](https://tap.broadcom.com/help)
- NFR licenses: TAP members see and request NFRs through "Review Available NFR Licenses" and "Request NFR Licenses" on the TAP Home page. The landing page advertises NFR licenses "with support". — [TAP Help/FAQ](https://tap.broadcom.com/help); [tap.broadcom.com](https://tap.broadcom.com/)
- Broadcom's general NFR KB (Advantage program) says NFRs may be requested by Broadcom Advantage Partner program partners holding a Pinnacle/Premier/Select reseller agreement. Customers are ineligible. Quantity depends on "use case, BAP tier" and alignment with Broadcom strategy. Licenses last 1 year, renewable within 30 days of expiry. They are for test, demo, training and development only, and production or customer-hosted use is prohibited. Developing an adjacent offering such as backup is pointed to the TAP program instead, which runs a separate TAP NFR process for VCF licenses through the TAP portal. — [Broadcom KB 373980](https://knowledge.broadcom.com/external/article/373980/not-for-resale-vmware-license-informatio.html)
- The BCG is a public Broadcom site listing certified TAP-member hardware and software with VMware version compatibility. Listings follow a fixed Broadcom format and cannot be customized. — [TAP Help/FAQ](https://tap.broadcom.com/help)
- **[OLD]** Pre-acquisition TAP gave all members a 20% discount on VMware certification courses via Partner University. — [VMware TAP blog](https://blogs.vmware.com/tap/?p=1721)

### Inferences
- Money is not a barrier to entry. The real costs are engineering time for certification test kits and questionnaires, plus whatever approval Broadcom applies when vetting.
- Broadcom says NFR quantity depends on alignment with its strategy. Allocations for a backup ISV are probably discretionary and may be small. Full VCF licensing for a dev/test lab would otherwise mean buying a subscription.

### Gaps
- I could not see the current TAP Program Guide, the NFR license list, or the gated SDK list (all behind login), so I cannot say which VCF/vSphere editions or how many keys a backup ISV gets.
- I could not determine whether early-access/beta builds are offered to TAP members in 2026. The public pages list only "additional advantages" without detail.
- I could not confirm whether TAP still has tiers (e.g., the old Standard/Advanced/Premier).

---

## 3. Is there a VADP certification / "VMware Ready for Data Protection" listing today; is it required for customer support?

### Takeaway
I found no public evidence of a current, mandatory VADP certification, and no evidence that a BCG listing decides whether a customer gets Broadcom support. VMware's long-standing policy has been that backup software running against ESXi is "partner supported": customers go to the backup vendor, and VMware/Broadcom works with that vendor through TSANet or a cooperative support agreement. Certification and listing in the BCG are free and handled through TAP. They work as marketing and interoperability validation.

### Cited Findings
- Old VMware KB 2007904: "The functionality of the tape devices and the backup software in conjunction with VMware ESXi/ESX are partner supported. In the event that a problem is found, customers are advised to open support requests directly with the third-party vendor's support organization. VMware collaborates with third-party vendors via Technical Support Alliance Network (TSANet) or Cooperative Support Agreement (CSA)." **[OLD; taken from a search snippet of kb.vmware.com. The Broadcom-hosted URL returned 404, so the current revision could not be checked]** — [VMware KB 2007904](https://kb.vmware.com/s/article/2007904)
- Broadcom KB 311197 (VADP FAQ) gives the current name "vSphere Storage APIs – Data Protection" and says the Data Protection APIs are included with all licensed vSphere editions. **[Search snippet only. The page returned 404 on fetch, so the wording could not be verified]** — [Broadcom KB 311197](https://knowledge.broadcom.com/external/article/311197/faq-vmware-vsphere-storage-apis-data-pr.html)
- The BCG replaced the VMware Compatibility Guide (VCG/HCL) on Broadcom's platform, and older KBs that say VCG/HCL should be read as BCG. As of Aug 2025, BCG's top-level areas were Platform & Compute, Storage & Availability, GPUs and Hardware Accelerators, and Management & Orchestration. — [VMware Japan blog, Aug 2025](https://blogs.vmware.com/vmware-japan/2025/08/broadcom-support-portal-compatibility-guide.html)
- The BCG lists certified TAP-member products and costs nothing to post to. Certification programs are only open to TAP members. — [TAP Help/FAQ](https://tap.broadcom.com/help)
- Broadcom's Sep 2026 statement: "Customers needing backup and recovery capabilities can choose from the many approved solutions already available from these partners." The phrase "approved solutions" ties backup products to TAP partners but does not name a certification program. — [The Register, 10 Sep 2026](https://www.theregister.com/virtualization/2026/09/10/vmware-defends-ending-downloads-of-sdk-that-helps-vm-backups-or-migrations-to-rivals/5295421)
- An example of vendor-side practice: Broadcom's KB on Veeam B&R on VMware Cloud on AWS names supported Veeam versions and points to the compatibility guide for interoperability. Product-specific support statements like this exist for some environments. — [Broadcom KB 326569](https://knowledge.broadcom.com/external/article/326569/veeam-backup-replication-11-on-vmware-c.html)

### Inferences
- With no contrary evidence, a BCG data-protection listing is most likely optional. Its value is customer confidence in procurement, especially in enterprise RFPs, plus the "approved solution" framing Broadcom now uses. It does not appear to be a technical or contractual condition for the customer's vSphere support.
- Joining TSANet, or setting up a cooperative support arrangement through TAP, is what gives a backup vendor a channel for escalating joint issues. Without it, vendor support cannot formally escalate to Broadcom. Customers can still open their own Broadcom support cases about vSphere-side problems, such as snapshot/CBT bugs.
- The "approved solutions" language in the Sep 2026 statement suggests Broadcom may increasingly treat being an approved TAP backup partner as a gate. That would apply at least to VDDK access, and possibly to future support positioning. This is an inference from a single statement.

### Gaps
- I could not open the BCG (compatibilityguide.broadcom.com is an app and returned nothing via fetch/search), so I could not confirm whether a "Data Protection"/VADP category exists today or which vendors are listed. The legacy VCG had storage-related programs such as VAIO, VASA and vVols ([legacy VCG](https://www.vmware.com/resources/compatibility/vcl/result.php)). I found no public evidence of a VADP-specific BCG category.
- I found no current (2025–2026) Broadcom KB stating the support policy for third-party backup tools. KB 2007904 is the latest known wording and may be superseded.
- I could not determine whether Broadcom's "approved solutions" for backup are a formal list.

---

## 4. Are backup-relevant APIs or tools partner-only or under NDA (VAIO, VDDK builds, beta access)?

### Takeaway
**This is the most important change of 2026.** The VDDK, the library VADP backup vendors use to read and write virtual disks, is no longer publicly downloadable. Developer-portal pages started returning 404 around 25 Aug 2026. Broadcom confirmed in Sep 2026 that VDDK is available only "through select TAP [partners] for its licensed use case, which has always been backup and recovery." It says VDDK is "not a customer entitlement" and is covered by its own developer license. Redistributing or bundling VDDK needs a signed redistribution agreement. VAIO (I/O filters) has always needed VMware vetting and certification plus a signed I/O filter certificate. The TAP portal also lists "gated" SDKs. So a new commercial backup vendor now effectively must join TAP and be accepted for VDDK access. I found no public evidence of a separate NDA beyond the TAP Membership Agreement and the SDK license terms.

### Cited Findings
- ShapeBlue (dated 25 Aug 2026): the Broadcom VDDK landing page `developer.broadcom.com/sdks/vmware-virtual-disk-development-kit-vddk` and paths `/latest/`, `/8.0`, `/9.0` return "The page you requested couldn't be found". The Programming Guide page also returns 404, and Broadcom made no announcement. ShapeBlue states that the VDDK licence does not permit general redistribution, and notes in a correction that partners with a signed redistribution agreement do ship VDDK, citing Commvault documentation. Without such an agreement, a vendor cannot bundle VDDK and users must get it from Broadcom themselves. — [ShapeBlue, 25 Aug 2026](https://www.shapeblue.com/broadcom-vddk-download-vmware-to-kvm/)
- VMware/Broadcom statement to The Register (10 Sep 2026): "Access to the VDDK is still available through select TAP for its licensed use case, which has always been backup and recovery. Customers needing backup and recovery capabilities can choose from the many approved solutions already available from these partners. The VDDK is not a customer entitlement. It is a software development kit covered by its own developer license and was never included with a Broadcom software purchase." — [The Register, 10 Sep 2026 (Simon Sharwood)](https://www.theregister.com/virtualization/2026/09/10/vmware-defends-ending-downloads-of-sdk-that-helps-vm-backups-or-migrations-to-rivals/5295421)
- TechTarget (9 Sep 2026) quotes a Broadcom spokesperson: "The ability for individual organizations to download VDDK recently changed." Access is "still available through select TAP partners for its licensed use case". The change "merely re-enforces those terms". Azure Migrate, Red Hat Migration Toolkit and Nutanix Move all depend on VDDK and posted notices after 25 Aug. Red Hat said there is no direct replacement because VDDK is proprietary and licensed. — [TechTarget, 9 Sep 2026](https://www.techtarget.com/it-infrastructure/news/366648768/Broadcom-confirms-change-in-VMware-migration-tool-access)
- Network World (11 Sep 2026): VDDK "is still available to larger Broadcom software partners, but can no longer be downloaded by anyone for free." The download link and many support pages now 404, and Internet Archive copies were removed and the page excluded from the Wayback Machine. — [Network World, 11 Sep 2026](https://www.networkworld.com/article/4221279/broadcom-hampers-vmware-migration-by-blocking-downloads-of-key-sdk.html)
- The exact eligibility criteria for "select TAP" partners have not been publicly disclosed. **[Secondary source; treat with caution]** — [ServNet UK](https://www.servnetuk.com/news/broadcom-vmware-sdk-download-block-2026)
- Sources disagree on the takedown date: ShapeBlue and Zadara say ~25 Aug 2026, while ServNet UK says ~9–10 Sep 2026. — [Zadara, 29 Sep 2026](https://www.zadara.com/blog/2026/09/29/broadcom-vddk-restriction-vmware-migration/); [ServNet UK](https://www.servnetuk.com/news/broadcom-vmware-sdk-download-block-2026)
- The TAP portal lists "Public and gated Software Development Kits (SDKs)" as a member benefit. — [tap.broadcom.com](https://tap.broadcom.com/)
- VDDK compatibility policy: VDDK supports N-2 and N+1 vSphere releases, and it is used with VADP to build backup and restore software. **[Release notes from 7.0 era]** — [VDDK 7.0.3.1 release notes](https://docs.vmware.com/en/VMware-vSphere/7.0/rn/vddk-7031-release-notes.html)
- VAIO: partners build I/O filters through the VAIO developer program, and "the program does entail a vetting and certification process for partners before a solution is officially VAIO certified." **[OLD, 2015]** — [VMware Storage blog, Feb 2015](https://blogs.vmware.com/storage/2015/02/05/vsphere-apis-io-filtering/)
- The vSphere 8.0 docs still describe I/O filters as offered by VMware or created by third parties through VAIO. A Broadcom KB on renewing an I/O filter certificate lists vCenter Server 8.x, so the signed-certificate mechanism for VAIO filters still applies. — [vSphere 8.0 Storage docs](https://docs.vmware.com/en/VMware-vSphere/8.0/vsphere-storage/GUID-8C0FBA2B-90EE-45C1-885D-AAD54DE601A0.html); [Broadcom KB 320568](https://knowledge.broadcom.com/external/article?articleNumber=320568)
- Analyst views: IDC's Matthew Flug said "Eliminating VDDK certainly increases the potential of vendor lock-in". Moor Insights' Mike Leone advised buyers to ask vendors how they still obtain the library. — [TechTarget, 9 Sep 2026](https://www.techtarget.com/it-infrastructure/news/366648768/Broadcom-confirms-change-in-VMware-migration-tool-access)

### Inferences
- **Practical requirement:** a new commercial vendor building VADP backup on VDDK now effectively has to (a) join TAP, (b) be accepted as one of the "select" partners with VDDK access, and (c) sign a VDDK redistribution agreement to ship VDDK in its product. Without those, it would need customers to supply VDDK, which customers can no longer download publicly either. Formally this is a licensing gate rather than a certification gate, but in practice it is mandatory.
- Building directly on vSphere public APIs (the vSphere Web Services/vSphere Management SDK for snapshots and CBT queries, plus NFC/HTTP datastore access) without VDDK may still be technically possible. Whether the developer license terms of those SDKs restrict this, and how robust such a design is, falls outside this note's scope and needs separate checking.
- Broadcom says VDDK's licensed use case is backup and recovery, not migration. A backup vendor with VM-migration or cross-hypervisor restore features (e.g., restore-to-Proxmox/Hyper-V/AHV) could face licensing scrutiny. This is inferred from Broadcom's wording.
- VAIO filters require VMware certification/vetting and a signed certificate to install, so any VAIO-based continuous data protection approach is partner-gated by design. VAIO was also never the main path for ordinary backup.

### Gaps
- I could not confirm whether the TAP Membership Agreement includes confidentiality/NDA clauses (the page returned 404 and the agreement is behind login), or whether gated SDKs or early-access builds need a separate NDA.
- I could not determine whether VDDK is now downloadable inside the TAP portal for every member or only for approved "select" partners, or how to apply for VDDK access.
- I could not determine VAIO's status in vSphere/VCF 9.x (deprecation or not). The latest docs found were vSphere 8.0.
- I found nothing on beta/early-access programs (e.g., VCF 9.x betas) for TAP members.

---

## 5. Reports from smaller vendors about joining or being dropped from the program after Broadcom (2024–2026)

### Takeaway
I found no report of backup ISVs being removed from TAP. Coverage of partners being dropped concerns resellers in 2024 and VCSP cloud providers in 2025–2026. The only TAP-relevant pain is the Aug/Sep 2026 VDDK restriction, which hit migration-tool vendors and open-source/smaller players (ShapeBlue, Platform9, Nutanix Move, Red Hat MTV, Azure Migrate). Press reports say large partners keep VDDK access while others lose it.

### Cited Findings
- Early 2024: Broadcom terminated existing VMware reseller/solution-provider programs and replaced them with the invitation-only Advantage program. — [TechRadar](https://www.techradar.com/pro/broadcom-is-ending-vmwares-channel-program); [Techzine](https://www.techzine.eu/news/infrastructure/133131/broadcom-ends-vmware-partner-program/)
- 2025–2026: the VCSP program went invite-only from 1 Nov 2025. Many small and mid-sized cloud providers were not invited and were told their contracts would not be renewed after January 2026. — [Techzine](https://www.techzine.eu/news/privacy-compliance/138418/broadcom-thins-out-vmware-partner-channel-forced-migrations-feared/); [SDxCentral](https://www.sdxcentral.com/news/broadcom-calls-time-on-less-than-valued-vmware-partners/)
- ShapeBlue (an Apache CloudStack migration vendor): two of four ShapeBlue Migrate methods (direct and CBT) rely on VDDK and were broken by the removal. The article says smaller parties, such as "a three-person consultancy or an open-source project", have fewer options than large partners with a direct relationship. — [ShapeBlue, 25 Aug 2026](https://www.shapeblue.com/broadcom-vddk-download-vmware-to-kvm/)
- Platform9's 1 Sep 2026 post said the change disrupted in-flight migrations. — [TechTarget, 9 Sep 2026](https://www.techtarget.com/it-infrastructure/news/366648768/Broadcom-confirms-change-in-VMware-migration-tool-access); [Platform9 blog](https://platform9.com/blog/vddk-no-longer-available/)
- Analyst Keith Townsend: smaller, price-conscious customers are most affected because they rely on cheaper third-party migration paths. — [TechTarget, 9 Sep 2026](https://www.techtarget.com/it-infrastructure/news/366648768/Broadcom-confirms-change-in-VMware-migration-tool-access)
- NAKIVO (backup vendor blog, 27 May 2026) discusses Broadcom-era changes for backup: invite-only partner programs, some existing partners not invited or losing privileges, and backup vendors publishing guides and surveying customers. It makes no TAP-specific claims. — [NAKIVO blog, 27 May 2026](https://www.nakivo.com/blog/vmware-backup-broadcom-era-changes-challenges/)
- Catalogic presents itself publicly as a VMware Technology Alliance Partner. This is an example of a smaller backup vendor still claiming TAP status. — [Catalogic](https://www.catalogicsoftware.com/portfolio/vmware/)

### Inferences
- The incumbent backup vendors (Veeam, Commvault, Cohesity/Veritas, Rubrik, NAKIVO, etc.) appear to have kept VDDK access, consistent with Broadcom's "many approved solutions already available" wording. Newcomers face an opaque approval step. The real uncertainty for a new vendor is whether Broadcom grants "select TAP" VDDK access to a new, small backup company, and on what terms.
- No source reports a backup ISV being refused TAP membership, but given how little Broadcom publishes, the absence of reports is weak evidence.

### Gaps
- I found no first-hand account from a new or small backup vendor of applying for TAP or VDDK access after August 2026, either successfully or not.
- I found no reports of TAP members losing NFR licenses or being removed from TAP in 2024–2026.
- I found no Blocks & Files or CRN article specifically about TAP changes.
