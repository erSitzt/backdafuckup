# VMware vSphere Provider — Shaping Notes

## Scope

**What the provider covers.** Back up and restore VMware vSphere VMs with every publicly documented technique:
- our own vim25 client;
- snapshots plus CBT incrementals;
- HotAdd through a Linux proxy VM;
- guest processing, with our Windows agent or with VMware Tools quiescing;
- agent install through guest operations;
- restore as a new VM or to the original location;
- quick rollback: write only the changed blocks back into the existing VM.

**Related specs.** This is spec 1 of 3. Instant VM recovery (spec 2) and backup from storage snapshots (spec 3) plug into its proxy agent, transports and snapshot hook.

## Decisions

### By the user (2026-11-18)

| Topic | Decision |
|---|---|
| Specs | Three specs: vSphere provider, instant VM recovery, backup from storage snapshots. |
| Spec 1 scope | The core plus VMware Tools quiescing with guest operations, and quick rollback. |
| Not in scope | ExportSnapshot / ImportVApp (HttpNfcLease). |
| Proxy | **One Linux proxy agent.** As a VM it does HotAdd; with SAN access (spec 3) it reads array snapshot LUNs. Several proxies can exist, chosen per job. |
| Test environment | A separate test host: ESXi 8 + vCenter 8, datastores on HPE Alletra MP over iSCSI. CI tests use a fake vim25 server. |
| Console | No visuals; follow the existing pages (pool wizard, Inventory, JobEditor, restore wizard). |
| Veeam | The user offered the Veeam proxy installation as a reference. It was declined: no code, libraries or design details from purchased products (EULA, clean-room AGPL). Veeam's public Help Center serves only as a source of ideas. |

### In the design (2026-11-18, checked against the code)

**Client and identity**
- **vim25 client:** hand-written over `HttpClient`, about 45 operations; the WSDL is a reference. Generated code (about 100 MB) is rejected. VI/JSON needs vCenter 8.0U1 or later and is not in the allowed list.
- **VM identity:** `NativeId` = `config.instanceUuid`, because the BIOS UUID can be duplicated. The new `InventoryEntry.GuestUuid` (the BIOS UUID) is used to match our Windows agent.

**Data path**
- **Transport:** the proxy serves the hot-added device as NBD, tunnelled through a WebSocket the proxy opens to the server. The server reads with the existing NBD pool, so CBT incrementals read only dirty chunks, and restore and rollback can write.
- **No VDDK, NFC, TAP or VAIO** (integration policy). vMotion of a VM during its backup cannot be prevented, because `DisableMethods` is not public.

**What v1 leaves out**
- Snapshot with memory.
- Standalone ESXi hosts (vCenter only).

**Quick rollback**
- A safety snapshot is on by default.
- CBT is reset afterwards; the next backup reads in full.

## Context

- **Visuals:** none.
- **References:** see `references.md`.
- **Product alignment:**
  - `mission.md`: "VMware vSphere … on the same core"; only public, documented APIs.
  - `roadmap.md` phase 2 "VMware vSphere support" (vim25, HotAdd, block-level restore), now in progress with this spec.
  - `tech-stack.md`: "Hypervisor integration … VMware vSphere (later)".

## Standards applied

- No standards are indexed; see `standards.md` for the conventions taken over from earlier specs and the additions for this one.
