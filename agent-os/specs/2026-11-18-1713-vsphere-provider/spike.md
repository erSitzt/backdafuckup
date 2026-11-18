# Spikes: vSphere provider

## Test environment (2026-11-18)

**What we test on.** The test vCenter is **vSphere 7.0.3** (vCenter 7.0.3 build 21477706, API 7.0.3), not vSphere 8 as planned.
- Two ESXi 7.0.3 hosts in one cluster.
- Shared VMFS datastores on the array, plus local VMFS.

**Scope.** Everything happens in a dedicated VM folder and resource pool (`backdafuckup-test`). The other VMs of that vCenter are never touched.

**Test VMs** (Ubuntu 24.04 cloud image, imported with the public OVF import):
- A proxy VM: PVSCSI controller on bus 1 for hot-added disks, `disk.EnableUUID=TRUE`.
- A Linux source VM.
- A Windows test VM follows (evaluation ISO, unattended setup).

**The scripts** are throwaway Python: raw SOAP over HTTPS, with the vCenter leaf certificate pinned (`VERIFY_X509_PARTIAL_CHAIN` with the leaf as anchor). They are not part of the product.

## S0: vim25 client basics

- **Version:** `/sdk/vimServiceVersions.xml` names the version for the SOAPAction (`urn:vim25/7.0.3.0`). `RetrieveServiceContent` works without a session; `about.apiType` is `VirtualCenter`.
- **Login:** a local SSO user needs the domain (`user@vsphere.local`). Without it, the login fails with "incorrect user name or password".
- **vCenter enforces XSD element order** in requests. A `HostDatastoreBrowserSearchSpec` with `matchPattern` before `details` was refused: "Unexpected element tag 'details' seen while parsing serialized DataObject of type vim.host.DatastoreBrowser.SearchSpec". This confirms the plan: `VimWriter` writes in sequence order, the fake checks the order, and golden-file tests guard it.
- **Byte arrays** (`xsd:byte[]`, e.g. `HostSystem.config.certificate`) come as `ArrayOfByte` with one signed `<byte>` element per byte, not base64.
- **Privileges:** `FetchUserPrivilegeOnEntities` on the root folder returns the effective privileges; usable for the connection wizard's check.

## S1: HotAdd of a snapshot disk (Linux source, PVSCSI)

| Step | Result |
|---|---|
| `ReconfigVM_Task` on the proxy: add a `VirtualDisk` with `VirtualDiskFlatVer2BackingInfo{fileName = snapshot's disk, diskMode = independent_nonpersistent}`, controller key of the PVSCSI controller on bus 1, unit 0, **no `fileOperation`** | works; 0.55 s |
| The snapshot config's disk backing (`VirtualMachineSnapshot.config.hardware.device`) | names the base disk at snapshot time (`[ds] vm/vm.vmdk`) with its `uuid` and `changeId` |
| Rescan on the proxy (`echo '- - -' > /sys/class/scsi_host/host*/scan`, `udevadm settle`) | 1.3–1.6 s; the disk appears (here `sdb`) |
| Mapping | `/dev/disk/by-id/scsi-3<disk uuid without dashes, lower case>` and `wwn-0x<same>` point to the hot-added disk. |
| Read | 1 GiB with O_DIRECT in 0.26 s (about 4.1 GB/s) |
| Detach: proxy deletes the SCSI device (`/sys/block/sdX/device/delete`), then `ReconfigVM_Task` remove **without `fileOperation`** | works; 0.55 s; the source disk is untouched |

**Mapping detail.** Kernel 6.8 does not fill the sysfs files `vpd_pg80`, `vpd_pg83` and `wwid` for PVSCSI disks (`wwid` gives ENXIO), even with `disk.EnableUUID=TRUE`. udev still finds the serial through a SCSI INQUIRY (SG_IO). So the proxy agent must issue INQUIRY VPD 0x80/0x83 itself over SG_IO, or rely on udev's by-id links. The VPD 0x83 identifier is NAA `6000c29…`, the same as the disk UUID.

**Still open in S1:**
- IDE/SATA/NVMe source disks;
- vRDM;
- a delta disk (a VM that already has a user snapshot);
- a proxy reboot with a disk attached;
- a source and proxy on different hosts with a local datastore.

## S3: CBT

| Check | Result |
|---|---|
| `ReconfigVM_Task{changeTrackingEnabled=true}` on a **powered-on** VM | 0.56 s. The first snapshot afterwards already carries a changeId (`52 9c f1 … 61/1`), so no extra stun cycle is needed. |
| changeId format | `<epoch: 16 hex bytes as "xx xx … xx-xx … xx">/<sequence>`. The sequence grows with every snapshot (`/1`, `/5`, `/9`, `/13`); the epoch stays. |
| Live disk backing | `changeId` is empty on the running VM's own disk even with CBT on. ChangeIds appear only in a snapshot's config. |
| `QueryChangedDiskAreas(snapshot, 2000, 0, "*")` on a 10 GiB VMFS disk | one call answered the whole disk (`length` = capacity), 53–55 areas, 2.19 GB allocated: sparse full backups work. |
| Write 64 MiB in the guest, `sync`, new snapshot, QCDA against the previous snapshot's changeId (that snapshot already removed) | one call, 38 areas, 74 MB (the data plus file-system metadata). Correct. |
| Snapshot create / remove (consolidate) | about 0.55 s each, with the task polled every 0.5 s. |

**Still open in S3:**
- QCDA paging on large disks;
- `"*"` on NFS;
- whether the proxy's writes during a writable attach are tracked;
- QCDA on a powered-off VM without a snapshot (needed for quick rollback).

## Not started yet

S2 (writable attach), S4 (Tools quiescing), S5 (guest operations), S6 (`/folder`), S7 (NBD over WSS throughput).
