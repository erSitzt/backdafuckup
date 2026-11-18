# Standards for Backup from Storage Snapshots

No standards are indexed in `agent-os/standards/` yet. The conventions of the earlier specs apply:
- restore points are write-once;
- the manifest is the commit point;
- errors name the place;
- both operating systems.

The additions of the vSphere provider spec apply as well (`../2026-11-18-1713-vsphere-provider/standards.md`):
- only public interfaces;
- never destroy what we did not create;
- a ledger row before every external call.

## Additions for this spec

### Ownership is proven before every delete
An array snapshot or VLUN is deleted or unpresented only when all of these hold:
- our name prefix;
- our server id in the comment;
- `type=VVTYPE_SNAPSHOT`;
- `copyOfID` equal to the recorded base;
- a ledger row (or an orphan older than 2 h).

ESXi temporary datastores are removed only when they carry our name and a ledger row.

### Snapshots and LUNs are read-only to us
- Snapshot LUNs are opened read-only on every path (`blockdev --setro`, `O_RDONLY`).
- LVM on a snapshot is parsed in user space; it is never activated or imported.
- The only write is the VMFS resignature, which ESXi performs on the snapshot copy, never on the source volume.

### A safety net outside the product
- Every array snapshot carries `expireSecs`, extended while a lease is open, so a lost server cannot leave snapshots forever.
- `retainSecs` is never set.

### Experimental readers are verified and can trip
VMFS6 and LVHD:
- compare CBT-unchanged regions with the previous restore point on every run;
- run a verify mode against the supported path for the first runs and after version changes;
- on a mismatch, fall back, switch off for that storage system, and alert.

The read method is recorded per disk in the restore point.

### Licences of derived code
- Code ported from GPL-2.0-or-later sources keeps its copyright lines and an SPDX header.
- It is listed in `NOTICE.md` and `THIRD-PARTY-NOTICES.md`.
- Files that are GPL-2.0-only are never ported.

### Only the public array API
- WSAPI v3 calls and fields from the array's OpenAPI document.
- No CLI scraping, no undocumented headers.
