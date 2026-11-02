# Hyper-V Backup Integration: Access Requirements for a Third-Party Vendor (comparison baseline vs. VMware vSphere)

Scope: what a commercial backup vendor needs to integrate host-level Hyper-V VM backup on Windows Server 2016 through 2025, as of November 2026. Every API involved is on Microsoft Learn. I found no gated SDK, NDA, partner tier or certification requirement.

## 1. Are the Hyper-V WMI v2, snapshot/management services, production checkpoints, RCT and VHDX (virtdisk) APIs publicly documented? Is there a sample?

### Takeaway
Yes. The whole host-side backup stack is publicly documented on Microsoft Learn: the WMI v2 namespace, Msvm_VirtualSystemSnapshotService, Msvm_VirtualSystemReferencePointService (RCT reference points), and virtdisk.dll QueryChangesVirtualDisk. Microsoft also has a conceptual "Hyper-V Backup Approaches" page aimed at developers. The only official sample I could find is an old, unmaintained PowerShell reference module (xHyper-VBackup, 2014). I found no maintained C# or C++ RCT sample.

### Cited Findings
- Microsoft's "Hyper-V Backup Approaches" page is written for developers: "There are several approaches that are available for developers to utilize". It describes two methods. The host VSS writer is "designed for small scale backup operations". The WMI-based method is available "Starting in Windows Server 2016" and uses "a combination of reference points and resilient change tracking (RCT)"; Microsoft calls it "more scalable than using VSS in the host". (Page updated 2025-08.) — [MS Learn: Hyper-V Backup Approaches](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/backup-approaches)
- The same page names three documented ways to read backup data: (a) WMI export, which works remotely but transfers a lot of data; (b) the Win32 APIs `SetVirtualDiskInformation`, `GetVirtualDiskInformation` and `QueryChangesVirtualDisk`, which are local only, cannot read shared VHDX files, and return offsets relative to the VHDX's internal structure; (c) the publicly specified Remote Shared Virtual Disk Protocol (MS-RSVD), for shared VHDX. A full VM backup must also copy the `.VMCX`, `.VMGS` and `.VMRS` files. — [MS Learn: Hyper-V Backup Approaches](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/backup-approaches)
- Msvm_VirtualSystemSnapshotService is fully documented, with the methods CreateSnapshot, ApplySnapshot, DestroySnapshot, DestroySnapshotTree, ClearSnapshotState and ConvertToReferencePoint. ConvertToReferencePoint was "added in Windows 10"; only recovery snapshots can be converted. The class lives in namespace `Root\Virtualization\V2` (MOF WindowsVirtualization.V2.mof, implemented by Vmms.exe). Minimum versions are Windows 8 and Windows Server 2012. — [MS Learn: Msvm_VirtualSystemSnapshotService](https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-virtualsystemsnapshotservice)
- `Msvm_VirtualSystemReferencePointService.CreateReferencePoint` is documented with ReferencePointType "Log based (0)" (Hyper-V Replica log) and "RCT based (1)" (Resilient Change Tracking of virtual disks). Minimum versions are Windows 10 (client) and Windows Server 2016, namespace `Root\virtualization\v2`. The related service methods DestroyReferencePoint, ExportReferencePoint and RemoveAssociatedData are also documented. — [MS Learn: CreateReferencePoint](https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-virtualsystemreferencepointservice-createreferencepoint); [MS Learn: Msvm_VirtualSystemReferencePoint](https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-virtualsystemreferencepoint)
- Msvm_VirtualSystemReferencePoint exposes an array of RCT IDs, one per virtual disk at the time of the reference point. This is valid only for RCT-based points, and VirtualDiskIdentifiers maps those IDs to the disks. — [MS Learn: Msvm_VirtualSystemReferencePoint](https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-virtualsystemreferencepoint) (via search summary)
- `QueryChangesVirtualDisk` (virtdisk.h / VirtDisk.lib / VirtDisk.dll) "Retrieves information about changes to the specified areas of a virtual hard disk (VHD) that are tracked by resilient change tracking (RCT)". It needs a handle opened with VIRTUAL_DISK_ACCESS_GET_INFO, takes a ChangeTrackingId baseline and returns QUERY_CHANGES_VIRTUAL_DISK_RANGE arrays. Minimum versions are Windows 10 and Windows Server 2016. — [MS Learn: QueryChangesVirtualDisk](https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/nf-virtdisk-querychangesvirtualdisk)
- Production checkpoints are documented as using "Volume Shadow Copy Service or File System Freeze on a Linux virtual machine to create a data-consistent backup ... No snapshot of the virtual machine memory state is taken". They are "selected by default". Admins can set Standard, Production or ProductionOnly with `Set-VM -CheckpointType`. — [MS Learn: Using checkpoints](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/checkpoints)
- The official sample is `hyperv-samples/taylorb-wmi/xhypervbackup` in MicrosoftDocs/Virtualization-Documentation, a PowerShell module with a LICENSE file. Its README says it "was initially developed in 2014 to demonstrate and provide a reference implementation of new Hyper-V backup and restore APIs", is "not presently under active maintenance", and is "provided without expectation of support, guarantees or warrantee". — [GitHub: MicrosoftDocs/Virtualization-Documentation xhypervbackup](https://github.com/MicrosoftDocs/Virtualization-Documentation/tree/main/hyperv-samples/taylorb-wmi/xhypervbackup)
- A secondary description of that module says it does a full backup by taking a backup checkpoint and then incremental exports against a reference point. — [GitHub: machv/xhyper-vbackup](https://github.com/machv/xhyper-vbackup)
- A vendor document (secondary source) says RCT-based backup needs VM configuration version 6.2 or later. — [Storware docs: Microsoft Hyper-V](https://docs.storware.eu/backup-and-recovery-7.3/protecting-virtual-machines/virtual-machines/microsoft-hyper-v)

### Inferences
- Integrating needs no gated SDK, partner portal login or NDA. VMware's VDDK is also a free download, but it comes with its own license and distribution terms; the vSphere note should check those. For Hyper-V, everything runs through APIs built into the OS.
- The usual vendor flow is: create a recovery or backup checkpoint (Msvm_VirtualSystemSnapshotService.CreateSnapshot, which uses guest VSS through the integration service); copy the data; turn the checkpoint into an RCT reference point (ConvertToReferencePoint); and on the next run call QueryChangesVirtualDisk with the stored RCT ID to get changed blocks. This is the Hyper-V equivalent of vSphere snapshot + CBT + VDDK.
- Engineering cost is real even without access barriers. The sample is old and in PowerShell, documentation pages are dated 2018 to 2021, and QueryChangesVirtualDisk offsets are VHDX-internal and local-only. Expect to reverse-engineer edge cases, much as vendors do with CBT.

### Gaps
- I did not read the xhypervbackup LICENSE file, so its license (likely MIT, as in other MicrosoftDocs repos) is unconfirmed.
- I found no official Microsoft C#/C++ end-to-end RCT backup sample. Third-party open-source examples exist (for example BlueBlock/HvBackup2), but I did not review them.
- I did not find the VM configuration version 6.2 requirement for RCT on a Microsoft page; only vendor docs state it.

## 2. Are the VSS requester and writer APIs for application-consistent backup (SQL Server, AD) public and free, in guests and on the host?

### Takeaway
Yes. VSS is a public set of COM interfaces documented on Microsoft Learn. Headers (Vss.h, VsWriter.h) and samples ship in the free Windows SDK. Inside Hyper-V guests, application consistency comes from the built-in "Hyper-V Volume Shadow Copy Requestor" integration service (Windows `vmicvss`, Linux `hv_vss_daemon`). The vendor does not need an in-guest agent just for crash or application consistency at checkpoint time.

### Cited Findings
- "The Volume Shadow Copy Service (VSS) is a set of COM interfaces that implements a framework to allow volume backups to be performed while applications on a system continue to write to the volumes." VSS requesters, providers and writers must run natively; running them under WOW64 is not supported on Vista and later. — [MS Learn: Volume Shadow Copy Service](https://learn.microsoft.com/en-us/windows/win32/vss/volume-shadow-copy-service-portal)
- Microsoft says to use the current Windows SDK for VSS development on Vista and later. Sample requester, provider and writer code is in the Windows SDK, and requesters are compiled against headers such as Vss.h and VsWriter.h. The older VSS 7.2 SDK, still on the Download Center (republished 2024-07-15), is only for Windows Server 2003 and XP. — [MS Learn: VSS usage conventions](https://learn.microsoft.com/en-us/windows/win32/VSS/usage-conventions); [Microsoft Download Center: VSS SDK 7.2](https://www.microsoft.com/download/details.aspx?id=23490)
- The Hyper-V Volume Shadow Copy Requestor integration service ("vmicvss" / "hv_vss_daemon") "Allows Volume Shadow Copy Service to back up applications and data on the virtual machine." When it is disabled, "the virtual machine can't be backed up while running (using VSS)". It was added in Windows Server 2012 and Windows 8. — [MS Learn: Hyper-V Integration Services](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/integration-services)
- Hyper-V implements a host VSS writer on all Windows Server versions where Hyper-V is supported. The WMI-based approach "still utilizes VSS inside the virtual machine ... but no longer uses VSS in the host operating system". — [MS Learn: Hyper-V Backup Approaches](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/backup-approaches)

### Inferences
- Application-consistent backup of SQL Server and AD inside a Windows guest uses the in-box SQL Writer and NTDS writer, triggered by a production checkpoint. AD-safe restore (VM-GenerationID, non-authoritative restore) is handled by the OS. Granular SQL or AD item restore, or log truncation, is extra vendor work, often an in-guest agent using the same free VSS requester APIs.
- No Microsoft licensing fee or registration applies to writing a VSS requester. A custom VSS hardware provider is a separate and deeper area, outside the needs here.

### Gaps
- I did not fetch the Windows SDK license terms. They are generally understood to allow building and distributing commercial applications, but I did not verify this here.
- I did not verify the exact behavior of SQL Server log truncation under Hyper-V production checkpoints (copy-only versus full VSS backup type).

## 3. Can this be used from C#/.NET (System.Management, Microsoft.Management.Infrastructure, P/Invoke)? Any license restrictions?

### Takeaway
Yes. The Hyper-V WMI v2 classes can be called from .NET through System.Management or Microsoft.Management.Infrastructure (MI/CIM, published on NuGet under MIT by Microsoft). virtdisk.dll is a plain Win32 C API that can be called with P/Invoke. VSS is COM and is usable through interop. I found no license restriction on any of these.

### Cited Findings
- Microsoft.Management.Infrastructure on NuGet: MIT license, owners Microsoft and PowerShellTeam, latest version 3.0.0 (2023-11-03). It targets .NET Standard 1.6 and .NET Framework 4.5.1+, and is compatible with .NET 5–10. — [NuGet: Microsoft.Management.Infrastructure](https://www.nuget.org/packages/Microsoft.Management.Infrastructure)
- QueryChangesVirtualDisk and related virtdisk functions are exported from VirtDisk.dll as standard Win32 functions, which suits P/Invoke. — [MS Learn: QueryChangesVirtualDisk](https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/nf-virtdisk-querychangesvirtualdisk)
- Older Microsoft Hyper-V WMI documentation includes C# samples that depend on "Common Utilities for the Virtualization Samples". — [MS Learn (previous versions): Using the Hyper-V WMI Provider](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/virtual/using-the-virtualization-wmi-provider)

### Inferences
- A C#/.NET 8+ Windows-only backup agent is feasible with no third-party dependencies. VSS has no official managed wrapper, so C# needs custom COM interop or a community wrapper (for example AlphaVSS; its license was not verified here).
- System.Management is Windows-only. That is acceptable here because the agent must run locally on the Hyper-V host to use the virtdisk APIs.

### Gaps
- I did not verify the System.Management NuGet license (it is believed to be MIT as part of dotnet/runtime) or AlphaVSS's current maintenance and license status.

## 4. Is a Microsoft partner program, certification or logo required (or optional) for backup vendors? Is it free?

### Takeaway
No program, certification or logo is required to use the Hyper-V or VSS backup APIs or to sell a product built on them. The old "Certified for Windows Server" software logo programs (through Server 2012/2016) appear to be historical. I found no current Windows Server 2022/2025 software logo for backup applications. The Microsoft AI Cloud Partner Program is optional and is a commercial or marketing relationship, not an API gate.

### Cited Findings
- Historically, the Windows Server Software Logo Program covered Windows 2000 Server, 2003 and 2008, and logo-qualified products were listed in the Windows Server Catalog. — [TechNet: Windows Server Software Logo and Hardware Logo Programs](https://technet.microsoft.com/library/cc732698(v=ws.10))
- "Effective March 31, 2013, the Windows Server 2008 R2 Logo Program will no longer be accepting submissions." — [MS Learn (previous versions): Certified for Windows Server](https://learn.microsoft.com/en-us/previous-versions/jj672852(v=msdn.10))
- Vendors used a "Certified for Windows Server 2012" designation through the Windows Logo Program for Server Applications. One secondary source (Directions on Microsoft) mentions a Windows Server 2016 certification being retired on 2020-06-30, but gives no details. — [StorageNewsletter: Handy Backup for WS2012](https://www.storagenewsletter.com/?p=121601); [Directions on Microsoft](https://www.directionsonmicrosoft.com/?p=116739)
- A secondary blog (2018) reports that the Sysdev portal stopped handling Windows software logo certification. — [Born's Tech and Windows World](https://borncity.com/win/2018/07/22/windows-software-logo-certification-program-deprecated/)
- Microsoft AI Cloud Partner Program: joining needs a verified Partner Center account. Benefits include badges through Logo Builder, and the legacy Silver membership ended on 2025-01-22. ISV Success is free for the first year and then about $1,500–1,550 USD a year (sources disagree on the exact figure). — [MS Learn: Partner Center membership overview](https://learn.microsoft.com/en-us/partner-center/membership/mpn-overview); [Microsoft Partner: membership](https://partner.microsoft.com/en-us/membership)

### Inferences
- This differs from VMware, where vendors usually pursue Broadcom/VMware Technology Alliance Partner (TAP) membership and "VMware Ready" or compatibility-guide listings, which the vSphere notes should confirm. Hyper-V has no comparable requirement. Major vendors (Veeam, Commvault, and others) market Hyper-V support without a Microsoft-issued backup certification.

### Gaps
- I found no official Microsoft statement that a Windows Server 2022/2025 software logo program for applications does or does not exist. The conclusion that none applies is based on there being no current program pages, not on an explicit retirement notice for every version.
- I did not confirm whether basic AI Cloud Partner Program membership (without ISV Success) costs anything as of late 2026.

## 5. Testing: Hyper-V Server 2019, Windows Server evaluation, and Windows 10/11 client Hyper-V. Which support RCT and production checkpoints?

### Takeaway
All three work for development and testing. RCT reference points and QueryChangesVirtualDisk need Windows Server 2016+ or Windows 10+, so Hyper-V Server 2019, Windows Server 2022/2025 evaluation and Windows 10/11 client Hyper-V all qualify. Production checkpoints are the default on Windows 10/11 and on Server 2016+. Hyper-V Server 2019 was the last free standalone release (supported until January 2029). Windows Server has a free 180-day evaluation.

### Cited Findings
- A Microsoft PM said that "starting with Microsoft Hyper-V Server 2019, the free 'Microsoft Hyper-V Server' product has been deprecated" and that 2019 is the final version. It "will continue to be supported ... until January 2029". Microsoft points users to the free 180-day Windows Server evaluation from the Evaluation Center, or to Azure Local (60-day trial, then about $10 per core per month). — [Microsoft Tech Community: Hyper-V Server 2022 thread](https://techcommunity.microsoft.com/discussions/windowsserverinsiders/hyper-v-server-2022/2652790); [Petri: No Hyper-V Server 2022 Free Edition](https://petri.com/microsoft-says-there-will-be-no-hyper-v-server-2022-free-edition/)
- The RCT reference-point WMI method (CreateReferencePoint, RCT type 1) has minimums of Windows 10 [desktop apps only] and Windows Server 2016. — [MS Learn: CreateReferencePoint](https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-virtualsystemreferencepointservice-createreferencepoint)
- QueryChangesVirtualDisk has the same minimums: Windows 10 and Windows Server 2016. — [MS Learn: QueryChangesVirtualDisk](https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/nf-virtdisk-querychangesvirtualdisk)
- "Windows 10 and 11 Hyper-V includes two types of checkpoints ... Production checkpoints are selected by default". "Hyper-V only offered standard checkpoints (formerly called snapshots) prior to Windows 10." — [MS Learn: Using checkpoints](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/checkpoints)
- WMI-based (RCT) backup is "only available on Windows Server 2016 and later". — [MS Learn: Hyper-V Backup Approaches](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/backup-approaches)

### Inferences
- The cheapest test lab is Windows 11 Pro with Hyper-V or the free Hyper-V Server 2019 for the core RCT and checkpoint path, plus Windows Server 2022/2025 evaluation VMs (180 days, can be re-armed or reinstalled) for target-version validation, failover clustering (Msvm_CollectionReferencePointService) and Windows Server guests running SQL Server or AD.
- Unlike vSphere, where testing CBT and VDDK needs ESXi and vCenter licenses (or eval or VMUG), Hyper-V's full backup API surface can be tested at no cost.
- Hyper-V Server 2019 runs a newer configuration version (9.0) than the 6.2 minimum reportedly needed for RCT, so default VMs should support RCT. This is not verified on a Microsoft page.

### Gaps
- I did not fetch the Windows Server evaluation license terms (allowed use, number of re-arms).
- I did not verify any feature difference in RCT or reference-point behavior between client Hyper-V (Windows 10/11) and Server, such as cluster or shared-VHDX support. Shared VHDX and guest clustering need Server.
- I did not check Azure Local (formerly Azure Stack HCI) backup API parity.
