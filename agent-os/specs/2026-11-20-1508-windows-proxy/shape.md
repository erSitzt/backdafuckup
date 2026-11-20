# Windows Backup Proxy — Shaping Notes

## Scope

A Windows version of the backup proxy agent, for **physical Windows servers that already have SAN access** to the array. It reads storage-snapshot LUNs that the backup server presents to the server's host object, and serves them to the backup server over the existing NBD tunnel. The array path on the server (snapshot, LVHD reader, verify mode, cleanup) stays as it is.

**Related specs.** Spec 3, *Backup from storage snapshots* (`../2026-11-18-1713-storage-snapshot-backup/`), planned this as "later". It comes first now.

## Why now (2026-11-20)

The first live storage-snapshot backup on XCP-ng failed at the iSCSI login. The Linux proxy VM had no interface in the array's iSCSI networks, which are not routed; giving it one needs a new address in a production storage network, which the user considers too risky. Physical Windows servers that already reach the arrays over iSCSI, with host objects on both arrays, can read the snapshots instead.

## Decisions by the user (2026-11-20)

| Topic | Decision |
|---|---|
| First server | A physical Windows server that already reaches the arrays over iSCSI (production). Ask before every change there, and only when it is not busy with other work. |
| Host object | Present our snapshots to the **existing host object** of that server. An initiator name can be in only one host object. We add and remove only the VLUNs of our own snapshots; the host object is never changed. |
| iSCSI | **Existing sessions only.** The agent rescans; it never logs in or out and never changes iSCSI or MPIO settings. A missing session is reported. |
| SAN policy | As that product documents for its own proxies: the Windows SAN policy is **Offline Shared**. If it is unsafe, the agent sets Offline Shared and reports it (opt-out `"prepareSan": false`). Every disk of ours is set offline and read-only before it is opened. |

## Out of scope

- FC host objects and FC proxies (the servers at hand use iSCSI).
- Restores through a Windows proxy: writable opens are refused.
- HotAdd on Windows: the agent never offers `hotadd-v1`.

## Context

- The other product's public help center: when a Windows server becomes its VMware backup proxy, it sets the SAN policy to Offline Shared and keeps SAN LUNs offline, so the OS never initializes VMFS volumes.
- Windows identifies an array LUN by its VPD page 0x83 NAA identifier (`IOCTL_STORAGE_QUERY_PROPERTY`, `StorageDeviceIdProperty`). `Get-Disk.SerialNumber` is page 0x80, not the WWN.
- With MPIO, `\\.\PhysicalDriveN` is the multipath disk, one per LUN.
