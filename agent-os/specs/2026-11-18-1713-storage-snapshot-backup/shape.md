# Backup from Storage Snapshots — Shaping Notes

## Scope

**The sequence:**
1. VM snapshot.
2. Array snapshot.
3. Remove the VM snapshot.
4. Back up from the array snapshot.
5. Delete it.

CBT still comes from the hypervisor.

**Coverage:**
- **Array:** HPE Alletra MP B10000, through its public WSAPI v3.
- **Hypervisors:** XCP-ng and VMware vSphere.
- **Plugin interface:** other arrays plug in later.

**Related specs.** Spec 3 of 3. It uses spec 1's Linux proxy agent, HotAdd and vim25 client.

## Decisions

### By the user (2026-11-18)

**Arrays and hypervisors**
- One array plugin (Alletra MP), used for both hypervisors.

**vSphere read methods**
- **Default:** resignature the snapshot on an ESXi host and read through HotAdd (public vim25 only).
- **In this spec, opt-in:** a VMFS6 reader on the proxy that reads the snapshot LUN directly.

**The VMFS policy exception**
- VMware never documented VMFS; the open-source readers were reverse-engineered by third parties.
- The user accepted relaxing "nothing undocumented" for this one on-disk format.
- Only GPL-2.0-or-later code may be ported, and only with verification and a fallback.

**XCP-ng**
- LVHD is read from the snapshot LUN in user space, opt-in.
- The earlier SAN report rated it experimental; it is now wanted, with safeguards.

**Proxy and tests**
- One Linux proxy agent for HotAdd and SAN reads.
- Tests run on dedicated test volumes only.
- Nothing is deleted on the array or in the pool that the product did not create.

### In the design (2026-11-18)

**Batching and snapshot release**
- One array snapshot per volume for up to 8 VMs (60 s window).
- The VM snapshot is released "after open" (XCP-ng, VMFS reader) or "at once" (vSphere resignature path).

**Alletra safety**
- `expireSecs` as a safety net, extended while in use; never `retainSecs`.
- Deletes require proof of ownership: name prefix, server id in the comment, `copyOfID`, ledger row.

**Reading and verification**
- **Experimental readers are verified:**
  - CBT-unchanged regions are compared on every run;
  - verify mode runs against the supported path;
  - a mismatch trips the reader off.
- **LVM metadata is parsed in user space.** `vgimportclone` and activation are never used, because they write to the PVs.
- **Approach E** of the slow-storage spec is superseded; the XAPI snapshot is kept for CBT.

## Context

- **Visuals:** none.
- **References:** see `references.md`.
- **Product alignment:**
  - `mission.md`: "Backup from storage snapshots, so the live cluster is barely stressed".
  - `roadmap.md` phase 1 lists it for XCP-ng + Alletra.
  - `tech-stack.md` "Storage Snapshot Integration" is corrected in the same commit (no `vgimportclone`) and gains the VMFS exception in the Integration Policy.

## Standards applied

- See `standards.md`.
