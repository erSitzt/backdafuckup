# vSphere management APIs and SDKs for a C#/.NET 10 backup backend (inventory, snapshots, CBT, VM create/restore): options, availability, licenses

Research date: 2026-11-01. All developer.broadcom.com pages were fetched on that date. Older sources (pre-2024) are flagged inline. The current vSphere/VCF line seen on Broadcom's portal is **9.1.1** (marked "Latest" on the API references).

---

## Q1. vSphere Web Services API (SOAP, vim25): where is the WSDL/reference, is it public, is it still supported in vSphere 8/9, can modern .NET generate a client, known issues?

### Takeaway
The SOAP vim25 API is still published and documented for every release up to vSphere 9.1.1, and I found no official deprecation notice for it. Broadcom also exposes the same object model over HTTP/JSON (VI/JSON, vCenter 8.0 U1+), which covers backup methods such as `QueryChangedDiskAreas`. Generating a .NET client from `vimService.wsdl` with dotnet-svcutil/WCF works, and VMware itself names this as the replacement for its dropped .NET SDK. The result is very large, though: about 105 MB of generated code, more than 800 operations, and XmlSerializer is mandatory. CoreWCF does not help here because it is server-side only.

### Cited Findings
- **API reference is public and current for 9.x.** The vSphere Web Services API reference on developer.broadcom.com lists versions "9.1.1 (Latest), 9.1, 9.0, 8.0U3, 8.0U2, 8.0U1, 8.0, 7.0U3f, 7.0U3, 7.0U2, 7.0U1, 7.0". It describes the API as "exposed as a Web service". The page itself has no WSDL download link and no deprecation note. — [Broadcom vSphere Web Services API reference](https://developer.broadcom.com/xapis/vsphere-web-services-api/latest/)
- **The WSDL ships inside the vSphere Management SDK zip.** The SDK readme says the WSDL "is divided into two separate files: vim.wsdl and vimService.wsdl (imports the vim.wsdl). Use the vimService.wsdl file to generate stubs." (This is an older VMware-hosted readme.) — [VMware vSphere Web Services SDK Readme](https://vdc-download.vmware.com/vmwb-repository/dcr-public/3d076a12-29a2-4d17-9269-cb8150b5a37f/8b5969e2-1a66-4425-af17-feff6d6f705d/SDK/vsphere-ws/docs/SDK-README.html)
- **vCenter also hosts WSDL files in its `docRoot\sdk` folder** (on Windows vCenter: `C:\ProgramData\VMware\VirtualCenter\docRoot\sdk`). A vCenter 5.0-era KB warned that the server-hosted set was incomplete, so "you cannot use the server-based WSDL and SOAP toolkit". This KB is OLD (vCenter 5.0) and may not apply today. — [VMware KB 2010507](https://kb.vmware.com/s/article/2010507)
- **VMware's own guidance for .NET users is to generate bindings from the WSDL.** The vSphere Web Services SDK 8.0 release notes say: ".NET SDK discontinued. C# sample programs in the SDK have been deleted." and "Programmers can still write Java programs for vSphere Web Services, or generate .NET bindings using WSDLs published in the SDK and using .NET libraries." — [vSphere Web Services SDK 8.0 Release Notes (Broadcom TechDocs)](https://techdocs.broadcom.com/us/en/vmware-cis/vsphere/vsphere-sdks-tools/8-0/release-notes/vsphere-management-sdk/vsphere-web-services-sdk-80-release-notes.html)
- **Generated code size and performance (dotnet/wcf issue #3002, July 2018, OLD, .NET Core 2.x era).** Running `dotnet svcutil` on `vimService.wsdl` produced a ~105 MB source file with "thousands of `ServiceKnownTypeAttribute` lines". A single SOAP call allocated 922 MB. The WCF maintainer said the service exposes more than 800 operations and requires XmlSerializer because some types cannot be serialized with DataContractSerializer, and that "unfortunately there's nothing we can do about it" regarding code size. The `dotnet-svcutil.xmlserializer` pre-generation tool initially failed with "Operation is not supported on this platform". Follow-up moved to issue #3004. — [dotnet/wcf #3002](https://github.com/dotnet/wcf/issues/3002)
- **Other svcutil bugs that may matter.** dotnet/wcf #5868 reports that since v10.0.652802 several types are missing from svcutil-generated proxies on Linux. #5404 reports that recursively nested XSD structures generate incorrectly. (Both are from search snippets and were not tested against vim25.) — [dotnet/wcf #5868](https://github.com/dotnet/wcf/issues/5868), [dotnet/wcf #5404](https://github.com/dotnet/wcf/issues/5404)
- **The dotnet/wcf repo holds the client libraries.** It describes them as "client-oriented WCF libraries that enable applications built on .NET Core to communicate with WCF services". `System.ServiceModel.Duplex` and `System.ServiceModel.Security` are deprecated: "6.0 is the last release of this package". — [dotnet/wcf GitHub](https://github.com/dotnet/wcf)
- **CoreWCF is server-side only.** Its README says "CoreWCF is a port of the service side of Windows Communication Foundation (WCF) to .NET Core" so existing WCF *services* can move to .NET Core. It is not a client generator. — [CoreWCF GitHub](https://github.com/CoreWCF/CoreWCF)
- **VI/JSON is the alternative wire protocol for the same API.** Broadcom calls it an "HTTP/JSON wire protocol for vCenter", an alternative to SOAP/XML, available "Starting with vCenter 8.0 Update 1" and described with OpenAPI 3.0. Its version selector goes up to 9.1.1 (Latest). URLs follow `/sdk/vim25/<release>/<ManagedObjectType>/<moId>/<operation>`, e.g. `GET /sdk/vim25/9.1.1.0/VirtualMachine/vm-495/config`. You log in with `POST .../SessionManager/<moid>/Login`, and the session travels in the `vmware-api-session-id` header. Broadcom also offers a Transcoder API (XML to JSON) for teams migrating from SOAP. — [Broadcom VI/JSON API reference](https://developer.broadcom.com/xapis/virtual-infrastructure-json-api/latest)
- **VI/JSON covers the full Web Services API, on vCenter only.** William Lam (VMware/Broadcom) says VI/JSON has "full parity with the existing vSphere Web Services API" and that "VI/JSON covers Web Services API". Asked whether standalone ESXi supports VI/JSON, he answered "No". A commenter asked whether SOAP is "reaching EOL" and the post shows no reply (Nov 2023). — [williamlam.com, "Is there a vSphere REST API for…" (Nov 2023)](https://williamlam.com/2023/11/is-there-a-vsphere-rest-api-for.html)
- **VMware's VI/JSON launch post** says the protocol exposes the same VI APIs on the same endpoint with the same security model, and was designed so that moving off SOAP is straightforward for most consumers (June 2023). — [VMware blog: Introducing VI/JSON](https://blogs.vmware.com/code/2023/06/10/introducing-vi-json-a-modern-wire-protocol-for-vsphere-management/)
- **VCF Java SDK wording suggests SOAP is the legacy path.** A Broadcom staging page refers to "Virtual Infrastructure JSON API (or the older vSphere Web Services API)". This is a staging site, so treat it with caution. — [developer-stgv2.broadcom.com java-sdk](https://developer-stgv2.broadcom.com/java-sdk)
- **No SOAP deprecation in VCF 9.0 deprecation lists.** The VCF 9.0 deprecations summarised by the community (Auto Deploy, vVols, Enhanced Linked Mode, vCLS, Host Profiles, SDDC Manager UI and others) do not include the Web Services API. — [vninja.net VCF9 deprecation notices (June 2025)](https://vninja.net/2025/06/18/vcf9-deprecation-notices/)

### Inferences
- The SOAP API remains a supported, documented interface in vSphere 9.1.1. Broadcom's direction (VCF SDK, VI/JSON docs, "older vSphere Web Services API") points toward VI/JSON as the preferred wire format for new work, but this is not a formal deprecation.
- **For a .NET 10 backend there are two realistic code-generation paths:**
  - **(a) WCF client from `vimService.wsdl`** using dotnet-svcutil and the System.ServiceModel.Http/Primitives packages. This works with vCenter 7.x and 8.0 GA, and with standalone ESXi. The cost is a huge generated assembly, XmlSerializer startup and memory overhead, and some fragility in the svcutil toolchain.
  - **(b) OpenAPI-generated client from the VI/JSON spec** (NSwag, Kiota, OpenAPI Generator). This works only with vCenter 8.0 U1+, not standalone ESXi. It avoids WCF entirely, but polymorphic data objects may be awkward in generated C# (not verified).
- The 105 MB / 922 MB figures date from 2018. Modern .NET may be better, but nothing current was found. Expect to trim the WSDL to the operations you need, or hand-write a thin SOAP layer over `HttpClient` with `XmlSerializer` for a subset of types.
- govmomi's `vcsim` (see Q5) implements the vSphere API and could serve as a CI test target for a .NET client. I did not verify how well it supports CBT/`QueryChangedDiskAreas`.

### Gaps
- No current (2025–2026) write-up was found that confirms dotnet-svcutil on .NET 8/10 against the vSphere 9.x `vimService.wsdl`, with real build sizes and startup timings.
- I did not confirm that current vCenter 8/9 serves a complete `https://<vcenter>/sdk/vimService.wsdl`. The KB about an incomplete server-side WSDL dates from vCenter 5.0.
- I found no official Broadcom statement, positive or negative, on the SOAP endpoint's long-term roadmap (end-of-life plans).
- I did not verify how VI/JSON encodes polymorphic types, or how well C# OpenAPI generators handle them.

---

## Q2. vSphere Management SDK: does it still exist, is .NET supported, where is it downloaded, is a login required?

### Takeaway
The vSphere Management SDK still exists on developer.broadcom.com, but the standalone bundle stops at 8.0 U3. From VCF 9.0 its Java part lives inside the VCF Java SDK. .NET support was deprecated in 7.0 U3d and removed in 8.0 (C# samples deleted), and no .NET variant exists for 9.x. SDK binary downloads need a Broadcom login, while the API reference documentation can be read without one.

### Cited Findings
- **Components.** The Management SDK page lists the vSphere Web Services SDK, vSphere Storage Policy SDK, vSphere Storage Management SDK, vSphere ESX Agent Manager SDK and SSO Client SDK. — [developer.broadcom.com vSphere Management SDK](https://developer.broadcom.com/sdks/vsphere-management-sdk/latest)
- **Versions.** The version selector offers 8.0 ("latest"), 7.0, 6.7 and 6.5, with downloads for 8.0, 8.0U1, 8.0U2 and 8.0U3. There is no 9.0 download. — [developer.broadcom.com vSphere Management SDK](https://developer.broadcom.com/sdks/vsphere-management-sdk/latest)
- **Login.** Each SDK download is marked "Download (Login Required)". Documentation, OpenAPI specs and open-source files show a plain "Download" link. — [developer.broadcom.com vSphere Management SDK](https://developer.broadcom.com/sdks/vsphere-management-sdk/latest)
- **Languages.** The page says "Code samples are provided for Java development" and does not mention .NET or C#. The zip is for Windows and Linux. — [developer.broadcom.com vSphere Management SDK](https://developer.broadcom.com/sdks/vsphere-management-sdk/latest)
- **VCF 9.0 change.** The page says the vSphere Management SDK for Java "will no longer be released as a standalone SDK. Instead, it is now integrated into the VCF Java SDK", and "The VIM and VSAN Management SDKs have been merged into a single WSDL specification and Java module." — [developer.broadcom.com vSphere Management SDK](https://developer.broadcom.com/sdks/vsphere-management-sdk/latest)
- **License.** Downloading or using the SDK binds you to "Broadcom's Software Development Kit License Agreement" unless another license is provided with the file. — [developer.broadcom.com vSphere Management SDK](https://developer.broadcom.com/sdks/vsphere-management-sdk/latest)
- **.NET deprecation history.** "As announced in August 2020 (KB 80144) vSphere Automation SDK for .NET was deprecated. The vSphere Management SDK for .NET was also deprecated in vSphere 7.0 U3d (P04) as outlined in KB 87965." and ".NET SDK discontinued. C# sample programs in the SDK have been deleted." (vSphere 8.0 SDK, released 11 Oct 2022). — [vSphere Web Services SDK 8.0 Release Notes](https://techdocs.broadcom.com/us/en/vmware-cis/vsphere/vsphere-sdks-tools/8-0/release-notes/vsphere-management-sdk/vsphere-web-services-sdk-80-release-notes.html)
- **VCF Java SDK (the 9.x successor).** The GitHub repo `vmware/vcf-sdk-java` is licensed "Apache-2.0" and supports vSphere 8.0, 9.0 and 9.1, Java 17, 21 and 25. Maven BOM: `com.vmware.sdk:vcf-sdk-bom:9.1.0.0`. The README notes that "Broadcom owned dependencies (e.g. vim25)" ship in the downloadable SDK zip. No .NET is mentioned. — [GitHub vmware/vcf-sdk-java](https://github.com/vmware/vcf-sdk-java)
- **VCF Java SDK 9.0** is described as bringing together "the legacy vSphere Automation SDK, vSphere Management SDK and vSAN Management SDK". It is available from the Broadcom portal and Maven Central. — [developer.broadcom.com VCF Java SDK 9.0](https://developer.broadcom.com/sdks/vcf-java-sdk/9.0)
- **VCF Python SDK 9.0** is installed from PyPI. — [developer-stgv2.broadcom.com vcf-python-sdk](https://developer-stgv2.broadcom.com/vcf-python-sdk)

### Inferences
- There is no official Broadcom .NET SDK of any kind for vSphere 8 or 9 (neither SOAP nor REST). Official SDK languages for 9.x are Java (VCF Java SDK), Python (VCF Python SDK / pyVmomi), Go (govmomi) and PowerShell (PowerCLI).
- To get the 9.x WSDL from an official package, a .NET developer would likely have to download the VCF Java SDK zip (login) or the 8.0 U3 Management SDK zip (login), or pull the WSDL from a running vCenter. Each route has the licensing questions covered in Q6.
- Inside the VCF Java SDK repo, only the repo contents are Apache-2.0. The vim25 bindings are explicitly "Broadcom owned" and come under the SDK License Agreement, not Apache.

### Gaps
- I could not open KB 87965 itself, so its exact wording and date are unknown.
- I did not confirm whether the VCF Java SDK 9.x zip still contains raw `vimService.wsdl`/`vim.wsdl` files, or only compiled Java bindings.

---

## Q3. vSphere Automation REST API (/api): which backup operations does it cover, and what still needs SOAP? Any .NET SDK?

### Takeaway
The Automation REST API (latest 9.1.1) covers VM create/configure, power, hardware (including disks), guest operations, Content Library, OVF and tagging. It shows no VM snapshot service and no CBT/changed-area query. Snapshot create/remove/browse and `QueryChangedDiskAreas` therefore still need the vim25 object model, over SOAP or VI/JSON. The official .NET Automation SDK was deprecated in 2020 and archived in 2021.

### Cited Findings
- **Services in the 9.1.1 navigation.** "Vcenter Vm" (guest customization, guest operations, hardware, power, storage policy, tools), "Vcenter Vm Hardware" (Cdrom, Cpu, Disk, Ethernet, Memory, Serial, Parallel), "Vcenter Vm Guest", "Content Library", "Cis Tagging"/"Vcenter Tagging", "Vcenter Vm Data Sets", "Vcenter Ovf", "Vcenter Deployment", Esx Hosts/Settings, Namespaces and others. No snapshot service and no CBT appear (the fetched page was truncated at 100k of 101k characters). — [Broadcom vSphere Automation API reference](https://developer.broadcom.com/xapis/vsphere-automation-api/latest/)
- **Snapshots are SOAP/VI/JSON-only.** William Lam lists "creating a VM snapshot", listing/updating VM advanced settings and VM custom attributes as tasks "not possible when using just the vSphere Automation API". He suggests looking up the MoRef via REST and then calling VI/JSON (Nov 2023). — [williamlam.com](https://williamlam.com/2023/11/is-there-a-vsphere-rest-api-for.html)
- **Community threads (OLD, 6.5–8.x era)** also say snapshot operations were missing from the REST API and recommend the SOAP API. — [Broadcom/VMware community: REST API Snapshot management](https://communities.vmware.com/t5/VMware-vCenter-Discussions/REST-API-Snapshot-management/m-p/2758327)
- **CBT via VI/JSON.** `QueryChangedDiskAreasRequestType` is documented in the VI/JSON reference for 8.0.3, 9.0 and latest. Required fields are `deviceKey`, `startOffset` and `changeId`, with an optional `snapshot` (a VirtualMachineSnapshot reference). Without a snapshot, changes are computed against "current", which works only for powered-off VMs. The result is a `DiskChangeInfo` with `changedArea`, paged by `length`. A separate `VslmQueryChangedDiskAreas` exists under /vslm (First Class Disks). — [VI/JSON QueryChangedDiskAreasRequestType (latest)](https://developer.broadcom.com/xapis/virtual-infrastructure-json-api/latest/data-structures/QueryChangedDiskAreasRequestType/), [9.0 version](https://developer.broadcom.com/xapis/virtual-infrastructure-json-api/9.0/data-structures/QueryChangedDiskAreasRequestType/), [DiskChangeInfo](https://developer.broadcom.com/xapis/virtual-infrastructure-json-api/latest/data-structures/DiskChangeInfo)
- **VI/JSON also lists "Virtual Machine Snapshot" and "Virtual Disk Manager"** among its managed-object categories. — [Broadcom VI/JSON API reference](https://developer.broadcom.com/xapis/virtual-infrastructure-json-api/latest)
- **Official .NET Automation SDK is dead.** The `vmware/vsphere-automation-sdk-.net` README says: "VMware has announced deprecation of vSphere Automation SDK for Perl and vSphere Automation SDK for .NET … effective August 25th, 2020" and "We highly recommend all our customers and partners to migrate from these SDKs." It supports only "vCenter 6.0, 6.5, 6.7, 6.7 U1, 7.0.0 and 7.0 U1". The repo is MIT licensed and was "archived by the owner on Dec 9, 2021". — [GitHub vmware/vsphere-automation-sdk-.net](https://github.com/vmware/vsphere-automation-sdk-.net)
- **The Broadcom portal still lists it as deprecated:** "vSphere Automation SDK for .Net [Deprecated]". — [developer.broadcom.com vSphere Automation SDK for .NET 7.0](https://developer.broadcom.com/sdks/vsphere-automation-sdk-for-.net/7.0)
- **Automation SDK replacements** are the Python and Java SDKs (per KB 80144 as cited in release notes). — [vSphere Web Services SDK 8.0 Release Notes](https://techdocs.broadcom.com/us/en/vmware-cis/vsphere/vsphere-sdks-tools/8-0/release-notes/vsphere-management-sdk/vsphere-web-services-sdk-80-release-notes.html)

### Inferences
- **Practical split for a backup product:**
  - The REST /api (or an OpenAPI-generated C# client) can handle session/inventory listing, VM create, hardware/disk configuration, power, tagging, Content Library/OVF deploy for restore templates, and guest operations.
  - vim25 (SOAP or VI/JSON) is needed for snapshot create/revert/remove (`CreateSnapshot_Task`, `RemoveSnapshot_Task`, `RevertToSnapshot_Task`), snapshot tree inspection, `QueryChangedDiskAreas`, enabling CBT (`ReconfigVM_Task` with `changeTrackingEnabled`), disk backing/changeId inspection, PropertyCollector-based inventory and task monitoring, `RegisterVM_Task`, `CreateVM_Task` with full ConfigSpec, and the `VirtualDiskManager`/FCD (`vslm`) operations.
  - These method names come from general vim25 knowledge. Only `QueryChangedDiskAreas` was explicitly confirmed in the docs fetched here.
- Because VI/JSON covers the whole Web Services API, a product targeting only vCenter 8.0 U1+ could in principle use REST for some tasks and VI/JSON for the rest, with no SOAP/WCF at all. Supporting vCenter 7.x or 8.0 GA, or direct-to-ESXi, still needs SOAP.
- The actual disk data transfer (reading changed blocks) is done through VDDK/NBD/HotAdd transports, not through these management APIs. That is a separate SDK with its own license, not covered here.

### Gaps
- Because the fetched REST reference was truncated, I could not fully rule out a snapshot or CBT service being added to the 9.x Automation API. No 9.0/9.1 release note mentioning REST snapshots was found.
- No community-maintained .NET SDK for the Automation REST API was found. I did not search NuGet specifically for "vsphere automation" packages.

---

## Q4. PowerCLI: license, and can a product depend on it or embed it?

### Takeaway
PowerCLI (renamed "VCF PowerCLI" from 9.0; latest 9.1.x) is Broadcom proprietary software. It ships under the same "Software Development Kit License Agreement" as the SDKs, which allows redistribution only of code that Broadcom designates as "distributable". The page that held those designations is now a dead link. Its `VMware.Vim` module contains a compiled .NET vim25 client (`VimService.dll`, `VMware.Vim.dll` for net472 and netcoreapp3.1). Embedding or redistributing it in a commercial product is not clearly allowed, so it should not be relied on.

### Cited Findings
- **Package details.** The PowerShell Gallery package VMware.Vim, version `9.1.0.25718932`, lists `<authors>Broadcom</authors>`, `<owners>Broadcom, Inc.</owners>` and the description "This PowerShell module contains PowerCLI Vim." Copyright reads "Copyright (c) Broadcom. All Rights Reserved." The project URL is developer.broadcom.com/powercli. (Checked by downloading and opening the .nupkg on 2026-11-01.) — [PowerShell Gallery VMware.Vim](https://www.powershellgallery.com/packages/VMware.Vim)
- **Contents** (from the same downloaded package). `EULA.rtf` plus `net472/` and `netcoreapp3.1/` folders containing `VimService.dll` (~16.5 MB), `VMware.Vim.dll` (~2 MB), `InternalVimService50.Wcf.dll`, `InternalVimService70.Wcf.dll`, `InventoryService55.Wcf.dll`, `DeployAutomationService.dll` and `ImageFactoryService.dll`. `VimService.dll` references `System.ServiceModel`, `System.ServiceModel.Channels` and `System.ServiceModel.Primitives` (i.e. it is a WCF client). The netcoreapp3.1 module manifest declares `PowerShellVersion = '7.0.0'` and `CompatiblePSEditions = @('Core')`. — [PowerShell Gallery VMware.Vim](https://www.powershellgallery.com/packages/VMware.Vim)
- **EULA text.** The `EULA.rtf` in VMware.Vim 9.1.0 is the "Software Development Kit License Agreement" (CA, Inc., document metadata "SDK License May 2024"). It grants internal use "solely for the purpose of creating software that communicates with CA Products". It says "Some code may be designated as 'distributable code' and/or 'modifiable code' at http://www.vmware.com/go/vwssdk-redistribution-info", and that redistribution is permitted "only as part of your Developer Software for non-commercial or commercial use", under a license protecting CA's interests. It forbids distributing "any part of the Software to any third party except as expressly permitted in Section 2" and forbids reverse engineering. — [PowerShell Gallery VMware.Vim (EULA.rtf inside package)](https://www.powershellgallery.com/packages/VMware.Vim)
- **Dead designation link.** `http://www.vmware.com/go/vwssdk-redistribution-info` currently redirects through communities.vmware.com to `https://community.broadcom.com/home`, i.e. the designation list is gone (checked with curl on 2026-11-01). Its last target was the community document "VMware vSphere SDK, vCloud SDK and Horizon View SDK" (`ta-p/2824868`). — redirect chain observed from [vmware.com/go/vwssdk-redistribution-info](http://www.vmware.com/go/vwssdk-redistribution-info)
- **Rename and continuity.** VCF.PowerCLI exists on PowerShell Gallery at 9.0.0, 9.1.0 and 9.1.1, with "Copyright (c) Broadcom. All Rights Reserved." The 9.1.0 listing includes an EULA.rtf and an open-source license file. Per the 9.0 notes, existing cmdlets and scripts "continue to work as expected". — [PowerShell Gallery VCF.PowerCLI](https://www.powershellgallery.com/packages/VCF.PowerCLI/), [VCF.PowerCLI 9.1.1 manifest](https://www.powershellgallery.com/packages/VCF.PowerCLI/9.1.1.25718932/Content/VCF.PowerCLI.psd1), [vinfrastructure.it on VCF PowerCLI 9 (July 2025)](https://vinfrastructure.it/2025/07/vmware-vcf-powercli-9/)
- **Community statements (OLD, secondhand).** A forum reply states that "redistribution of any part of powerCLI is not allowed". Another commenter says there is "no redistributable runtime version of PowerCLI", and the original poster found that vmware.vim.dll alone was not enough to run. — [VMware community: vSphere web client API / PowerCLI redistribution thread](https://communities.vmware.com/t5/VMware-PowerCLI-Discussions/vSphere-web-client-API/td-p/964490)

### Inferences
- Technically, a .NET 10 app could reference `VMware.Vim.dll`/`VimService.dll` (netcoreapp3.1 builds load on modern .NET in most cases; not tested). Legally, those DLLs are "Software" under the SDK agreement, and with the designation list gone it cannot be shown that they are "distributable code". Shipping them inside a closed-source product is therefore an unquantified legal risk. Requiring the customer to install PowerCLI themselves, and calling it out of process via PowerShell, avoids redistribution. But it adds a heavy runtime dependency and still relies on a license either party "may terminate … for any reason at any time" (see Q6).
- PowerCLI is fine as an admin/test tool for the dev team and for customer-side scripts. It is not a sound embedded API layer for a commercial backup engine.

### Gaps
- I could not retrieve the archived content of the "distributable code" designation page (community doc ta-p/2824868) to check whether `VimService.dll`/`VMware.Vim.dll` were ever designated distributable.
- I found no official Broadcom statement on whether third-party products may bundle or depend on PowerCLI modules.

---

## Q5. Community/open-source alternatives (govmomi, pyvmomi, NuGet vim25 wrappers) and how other .NET backup vendors talk to vSphere

### Takeaway
VMware/Broadcom itself publishes full vim25 bindings under Apache 2.0 for Go (govmomi, with the `vcsim` simulator) and Python (pyVmomi 9.1.1.0, Sep 2026). For .NET there are only two small, stale NuGet packages that wrap WCF-generated `vimService.wsdl` clients: VimClient 7.0.0 (CMU SEI, BSD-3-Clause, Feb 2022) and NetVimClient 6.7.0 (June 2019). Neither tracks vSphere 8 or 9. I found no public source on which bindings .NET-based vendors such as Veeam use internally.

### Cited Findings
- **govmomi** is licensed "Apache 2 License", supports "vSphere 7.0 and higher", sits under the `vmware` GitHub org, and includes `vcsim`, a "vSphere API mock framework". Its generated code comes from "the vSphere API description" and follows the vSphere API Reference. — [GitHub vmware/govmomi](https://github.com/vmware/govmomi)
- **pyVmomi**: latest 9.1.1.0, released Sep 3, 2026 (previous 9.1.0.0 on May 12, 2026). License "Apache Software License". "pyVmomi maintains minimum backward compatibility with the previous four releases of vSphere." — [PyPI pyvmomi](https://pypi.org/project/pyvmomi/)
- **NuGet search for "vim25"** returns only two packages, both "WCF Service Reference for VMWare vSphere Management SDK (wsdl/vim25/vimService.wsdl)":
  - **NetVimClient 6.7.0** — owner jeff.mattson, last updated 6/2/2019, ~24.9k downloads.
  - **VimClient 7.0.0** — owner CmuSei, last updated 2/28/2022, ~17.9k downloads.
  - Both target .NET Standard 2.0. — [NuGet search "vim25"](https://www.nuget.org/packages?q=vim25)
- **VimClient 7.0.0 details.** License "BSD-3-Clause"; footer "Copyright 2022 Carnegie Mellon University". No project or source URL. Depends on `dotnet-svcutil.xmlserializer (>= 1.2.0)` and `System.ServiceModel.Duplex/Http/NetTcp/Security (>= 4.9.0)`. NuGet computes compatibility with .NET 5–10. — [NuGet VimClient](https://www.nuget.org/packages/VimClient)
- **NetVimClient 6.7.0** targets .NET Standard 2.0. — [NuGet NetVimClient](https://www.nuget.org/packages/NetVimClient)
- **Two of VimClient's dependencies are deprecated.** `System.ServiceModel.Duplex` and `System.ServiceModel.Security` show "6.0 is the last release of this package". — [dotnet/wcf GitHub](https://github.com/dotnet/wcf)
- **Veeam (a .NET-based vendor) documents the workflow but not the binding.** Veeam B&R "requests vCenter Server or ESXi host to create a VM snapshot". During incrementals "the source proxy uses changed block tracking (CBT) to retrieve only those data blocks that have changed since the previous job session". — [Veeam Help Center: How Replication Works](https://helpcenter.veeam.com/docs/backup/vsphere/replication_process.html)
- **Veeam forum (staff answer).** Each job stores its own last changeId and passes it in its API call to get only later changes, and resetting CBT invalidates all previous changeIds (community forum, secondary). — [Veeam forums: how do backup jobs share CBT data](https://forums.veeam.com/vmware-vsphere-f24/how-do-backup-jobs-share-cbt-data-with-replication-jobs-t65746.html)

### Inferences
- For .NET there is no maintained third-party vim25 library. The realistic choices are to generate your own WCF proxy, to generate a VI/JSON OpenAPI client, or to write a thin hand-rolled SOAP/JSON client for the roughly 30–50 operations a backup product needs. The two NuGet packages are useful as references only, not as dependencies (stale API version, deprecated transitive packages, no source repo, and the derived-from-WSDL licensing question in Q6).
- govmomi and pyVmomi being Apache 2.0 matter for licensing. VMware/Broadcom itself publishes generated vim25 type definitions under a permissive open-source license, which supports (but does not prove) the view that client bindings derived from the API description may be redistributed. govmomi/pyVmomi source could also legally serve as a reference for hand-written .NET bindings (Apache 2.0 attribution applies if code is ported).
- A sidecar architecture (a small Go service using govmomi, or Python using pyVmomi, called by the .NET backend over gRPC/HTTP) is a legally clean, well-maintained alternative to native .NET bindings, at the cost of a second runtime.
- Large .NET backup vendors (Veeam, plausibly others) most likely maintain their own internally generated vim25 bindings, given that VMware never offered a supported .NET SDK after 7.0 U3d. This is an inference, not sourced.

### Gaps
- I found no public source naming the vSphere binding approach used by Veeam, Nakivo, Altaro/Hornetsecurity, or other .NET-based vendors.
- I did not check GitHub for unpublished or community .NET vim25/VI-JSON clients (e.g. repos generating from the VI/JSON OpenAPI spec).
- I did not verify current govmomi release numbers or whether `vcsim` implements `QueryChangedDiskAreas`.

---

## Q6. License terms of Broadcom/VMware SDK downloads: commercial use, redistribution of generated stubs and bundled DLLs

### Takeaway
Broadcom's current "Software Development Kit License Agreement" (v. 01222026, CA, Inc.) explicitly allows creating commercial software that communicates with Broadcom products. It allows redistributing only code that Broadcom has designated "distributable", as part of your product, under terms that protect Broadcom. Nothing else may be distributed, the license is terminable "for any reason at any time", and Broadcom's liability is capped at US $1,000. The designation list no longer resolves. The only explicit statement that WSDL-generated stubs are redistributable is a 2013 VMware FAQ, so current redistribution rights for generated stubs or bundled DLLs are unconfirmed.

### Cited Findings
- **Current agreement text** (Broadcom-hosted PDF, footer "v. 01222026", "Copyright © 2026 Broadcom"; licensor "CA, Inc. ('Broadcom')"; text extracted locally from the PDF). All quotes from [Broadcom: Software Development Kit License Agreement](https://docs.broadcom.com/doc/vmware-software-development-kit-license-agreement):
  - **§1 Software.** The Software contains "materials, interface definitions, documentation, sample utility applications and sample code regarding programming interfaces to one or more Broadcom products".
  - **§2 Use Rights.** "you may download and make a reasonable number of copies of the Software for your own internal use solely for the purpose of creating software that communicates with Broadcom Offerings (your software, 'Developer Software')."
  - **§2 Distributable code.** "Some code may be designated as distributable code and/or modifiable code. You may use and merge all or portions of the distributable code with your Developer Software… You are permitted to re-distribute the distributable code and the modified or derivative works of the modifiable code only as part of your Developer Software for non-commercial or commercial use; provided that you shall only distribute such code subject to a license agreement that protects Broadcom's and its licensors' interests consistent with the terms contained in this Agreement."
  - **§2 Open source.** OSS components are under their own licenses (open_source_licenses.txt / https://legaldocs.broadcom.com).
  - **§3 Restrictions.** No use "to create, design or develop anything other than Developer Software". No modifying, reverse engineering or disassembling "except as expressly permitted in Section 2". No distributing, selling, sublicensing "any part of the Software to any third party except as expressly permitted in Section 2". No use to "circumvent any technical restrictions of Broadcom Offerings or violate any additional licensing terms applicable to Broadcom Offerings". The restrictions do not apply where they contradict mandatory local law, "including … law implementing the EC Software Directive".
  - **§5 Trademarks.** No use of Broadcom marks that "suggests your Developer Software is certified or endorsed by Broadcom".
  - **§6 Support.** No support under the agreement, even if you hold a Broadcom support contract.
  - **§7 Termination.** "You or Broadcom each may terminate this Agreement for any reason at any time. You agree, upon termination, to destroy all copies of the Software".
  - **§9 Liability.** Liability is capped at "US $1,000.00".
  - **§10 Indemnity.** You indemnify Broadcom for claims arising from "the distribution or use of your Developer Software by you or anyone else".
  - **§13 Law.** California law governs. You may not assign the agreement.
- **The vSphere Management SDK download page binds users to this agreement:** "Broadcom's Software Development Kit License Agreement". — [developer.broadcom.com vSphere Management SDK](https://developer.broadcom.com/sdks/vsphere-management-sdk/latest)
- **The PowerCLI VMware.Vim 9.1.0 EULA** is the May 2024 version of the same agreement. It points designations to `http://www.vmware.com/go/vwssdk-redistribution-info`, which now redirects to the Broadcom community home page (dead; checked 2026-11-01). — [PowerShell Gallery VMware.Vim](https://www.powershellgallery.com/packages/VMware.Vim)
- **2013 VMware SDK FAQ (OLD, quoted on the OpenStack mailing list, Aug 14, 2013):**
  - "You can redistribute only those parts of the SDK package that have been designated as 'distributable code'."
  - "In VI SDK 2.5, the following components can be redistributed: vim.jar, vim25.jar."
  - "developers typically generate web service stubs from the WSDL file that is included in the VI SDK … using a SOAP toolkit" and "The stubs source and the compiled stubs can also be distributed."
  - Licensing contact: "SDK_Legal (at) vmware.com".
  - — [OpenStack mailing list: Could we distribute wsdl files from vSphere SDK?](https://lists.openstack.org/pipermail/openstack/2013-August/000555.html)
- **Older VMware-era SDK agreements** (vCloud SDK for Java, View Session Enhancement SDK, Workspace ONE SDK) use the same "distributable code"/"modifiable code" framework. — [BMC docs: vCloud SDK for Java license](https://docs.bmc.com/docs/display/public/common/vCloud+SDK+for+Java+license), [Law Insider: VMware SDK License Agreement](https://lawinsider.com/contracts/1Zpcb4i33gW)
- **Broadcom's own vim25 bindings in open source.** govmomi (Apache 2.0) and pyVmomi (Apache 2.0) both contain generated vim25 bindings published by VMware/Broadcom. In contrast, the VCF Java SDK repo (Apache 2.0) calls its vim25 dependency "Broadcom owned" and ships it separately in the zip. — [GitHub vmware/govmomi](https://github.com/vmware/govmomi), [PyPI pyvmomi](https://pypi.org/project/pyvmomi/), [GitHub vmware/vcf-sdk-java](https://github.com/vmware/vcf-sdk-java)

### Inferences
- **Commercial use is allowed.** Building and selling closed-source software that talks to vSphere is the stated purpose of the agreement ("Developer Software… for non-commercial or commercial use"). No royalty or partner-program requirement appears in the text.
- **Bundled Broadcom DLLs** (`VimService.dll`/`VMware.Vim.dll` from PowerCLI, Java vim25 jars, etc.) should be treated as not redistributable unless Broadcom confirms in writing that they are "distributable code". The designation list is unreachable and §3(iv) bans all other distribution.
- **Self-generated stubs from the WSDL** are the lower-risk path. The 2013 FAQ said generated stub source and binaries "can also be distributed", VMware's 8.0 release notes tell .NET users to generate their own bindings, and VMware itself publishes generated bindings under Apache 2.0. Two caveats:
  - The current agreement's §3(iii) "derivative works" clause plus the missing designation list leave room for argument.
  - The WSDL file itself (if obtained from the SDK zip) is "Software" and should not be shipped in the product. Generate at build time and ship only the compiled client. Generating from a VI/JSON OpenAPI spec raises the same question.
- **Termination at will** (§7) is a business risk for any architecture that depends on SDK-licensed artifacts at runtime. Hand-written or self-generated clients built from the publicly viewable API reference reduce dependence on the SDK license. A legal review is recommended. Under EU law, the agreement's own carve-out for the EC Software Directive (interoperability provisions) may be relevant; this needs counsel.
- The `SDK_Legal@vmware.com` contact from 2013 is very likely obsolete after the move to Broadcom. A current Broadcom contact for SDK licensing questions was not found.
- The data path (VDDK) has its own license and redistribution terms, which a backup product will also need to check. That is out of scope for these notes.

### Gaps
- I could not retrieve the current "distributable code" designation list (if one exists under Broadcom) or an archived copy of the community document it used to point to.
- I found no current Broadcom statement specifically addressing redistribution of WSDL-generated or OpenAPI-generated client code.
- I found no current Broadcom legal contact or technology-alliance-partner requirement for backup vendors (e.g. whether the Broadcom TAP/VADP certification programme imposes extra terms). Not researched.
- The vSphere/VCF *product* EULA terms that govern a customer's use of APIs on their own vCenter were not reviewed.
