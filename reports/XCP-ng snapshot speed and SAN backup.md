# Open NBD first, park array snapshots

Reading XCP-ng disks directly from HPE Alletra Storage MP B10000 array snapshots can be built with public interfaces. It is not a supportable default for our product, though, and it gains almost nothing over a working NBD + CBT path. A safe "SAN mode" still needs the short XAPI snapshot, because CBT and a stable, read-only VHD chain both depend on it. So the slow step stays. The step it would speed up, reading, is slow today only because TCP 10809 from the backup server to the NBD network is blocked. As a result every incremental falls back to a whole-disk HTTP export: dc1 changed 247 MiB, 22.9 GiB were read, and the snapshot was held for about 1:30 min (own measurements, backup01 session logs, 2026-11-02 to 2026-11-09).

Snapshots are slow because every disk triggers a fixed sequence on the pool master that does not depend on disk size. By code reading it is about 25 LVM commands, or 50–60 with CBT when the VM runs on the other host. On top come VHD and CBT tool calls and 4–5 calls to the other host. All of this runs while that disk's I/O is paused, under a lock on the whole SR and behind a host-wide LVM lock. That is why times grow with the number of disks (7.3–8.8 s for one disk, 40–47 s for four) and not with their size.

The same code shows that `VM.snapshot` pauses and captures disks one after another. A multi-disk VM whose snapshot outlasts the 10 s VSS hold therefore gets a restore point that is crash-consistent per disk but not across disks. Our current "crash-consistent" label overstates that.

The most valuable fixes are cheap:
- open the NBD port;
- take one snapshot per SR at a time, with both SRs in parallel;
- exclude disks that need no backup;
- keep LV counts and free space under control;
- label consistency honestly.

QCOW2, VM placement and parallel per-SR `VDI.snapshot` need lab tests first. The structural fix (SMAPIv3, array integration) is on the Vates roadmap, and that roadmap does not mention HPE.

## Array-snapshot backup keeps the slow step and speeds up the wrong one

The only safe form of "backup from storage snapshots" on `lvmoiscsi` is a hybrid:
1. Take the usual XAPI `VM.snapshot` (with our VSS freeze).
2. Snapshot the SR LUN on the B10000.
3. Destroy the VM snapshot right away and run `VDI.data_destroy` on its disks.
4. Read the now read-only base VHDs from the array copy on an isolated proxy.

A simpler-looking variant, a VSS freeze plus an array snapshot with no XAPI snapshot, fails for two reasons:
- **CBT only works between XAPI snapshot VDIs.** The newer VDI "cannot be attached to a VM at the time this comparison is made" ([XenServer CBT: list changed blocks](https://docs.xenserver.com/en-us/xenserver/developer/changed-block-tracking-guide/list-changed-blocks)). Every run would therefore be a full read.
- **The live disk it would read is unsafe.** tapdisk overwrites the trailing VHD footer with 0xC7 bytes whenever it opens a disk read-write, re-opens in strict mode after every pause, and may have block-table updates in flight ([blktap block-vhd.c](https://github.com/xapi-project/blktap/blob/master/drivers/block-vhd.c)).

**The hybrid therefore keeps all of the XAPI snapshot cost.** It also keeps the space that thick provisioning reserves for the snapshot and the leaf-coalesce that follows `data_destroy`. All it changes is which machine reads the data and how long the XAPI snapshot must live.

That gain shrinks to almost nothing once NBD works. Today all production backups fall back to `export_raw_vdi`, because TCP 10809 to 10.26.3.132/.133 (network "VeeamNBD") is blocked. Each incremental therefore re-reads the whole allocated disk. **dc1 changed 247 MiB, but the export read 22.9 GiB** at 150–280 MiB/s per disk and held the VM snapshot for about 1:30 min (own measurements, backup01 session logs, 2026-11-02 to 2026-11-09). NBD with `VDI.list_changed_blocks` reads only the changed 64 KiB blocks ([XenServer CBT: list changed blocks](https://docs.xenserver.com/en-us/xenserver/developer/changed-block-tracking-guide/list-changed-blocks)). For dc1 that is **about 95 times less data**, roughly 1–2 s of transfer at the measured rates. The snapshot then lives barely longer than it takes to create, with no array integration at all.

SAN mode would still win on full reads. The first backup of the 260 GiB VM, or a fresh full after a crash, holds a snapshot for 16–30 min at 150–280 MiB/s; an array copy would cut that to seconds and take bulk reads off dom0 and the host network. But fulls are rare under CBT: a host or SR crash resets CBT and forces a new baseline, and little else does ([XenServer CBT troubleshooting](https://docs.xenserver.com/en-us/xenserver/developer/changed-block-tracking-guide/troubleshoot)). Nobody has measured SAN against NBD throughput on 8.3. The advantage is real but narrow and unquantified.

No vendor ships this for Xen:
- **Veeam** supports backup from storage snapshots with the B10000, but only for vSphere. A Veeam staff member wrote in February 2026 that the Alletra MP integration features "are only available for VMware backups" ([Veeam forum](https://forums.veeam.com/microsoft-hyper-v-f25/hpe-alletra-mp-integration-t102097.html)). Its XCP-ng plug-in moves data through worker VMs that run on the hosts ([Veeam: Xen solution architecture](https://helpcenter.veeam.com/docs/vbr/userguide/xen_infrastructure_components.html)). Our pool's NBD-purpose network named "VeeamNBD" fits this: here Veeam reads through the hosts, not from the LUNs (inference from the environment).
- **Storware** exports XVA or reads CBT deltas over NBD on 10809/tcp ([Storware: XCP-ng](https://docs.storware.eu/backup-and-recovery-7.4/protecting-virtual-machines/virtual-machines/xcp-ng)).
- **Commvault** attaches snapshot disks to a proxy VM, up to 16 disks ([Commvault: Citrix Xen](https://documentation.commvault.com/11.42/commcell-console/backups_for_citrix_xen.html)).
- **Citrix** shipped one array integration, StorageLink, and removed it in XenServer 6.5 ([Citrix: storage upgrade](https://docs.citrix.com/en-us/xencenter/8-2/storage-upgrade)).
- **Vates** promises storage that works "whether or not its vendor appears anywhere in our documentation". Array offload is planned through vendor work on a future SMAPIv3 design: Pure Storage and NetApp are listed as LATER on the roadmap, and HPE is not listed at all ([Vates: storage ecosystem](https://docs.vates.tech/compatible-solutions/storage-ecosystem/); [Vates roadmap](https://docs.vates.tech/product-roadmap/)).

The deeper problem is that **the on-disk layout of LVM SRs (LVHD) is not an interface.** Public docs say only that each VDI is a VHD inside a thick LV ([XenServer 8: storage](https://docs.xenserver.com/en-us/xenserver/8/storage)). Everything else exists only in the LGPL-2.1 `sm` code ([lvmcowutil.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/lvmcowutil.py); [cleanup.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-cowutil-qcow2/drivers/cleanup.py)):
- the name prefixes `VG_XenStorage-`, `VHD-`, `QCOW2-`, `LV-`, `MGT` and `<uuid>.cbtlog`;
- the journal LVs `clone`, `leaf`, `relink`, `coalesce`, `inflate` and `zero`;
- the `OLD_` rename protocol.

The 2026 QCOW2 work has just added a prefix and renamed the driver modules.

A reader would have to:
- parse LVM2 metadata;
- follow VHD chains with Xen's hidden-flag and batmap extensions, and QCOW2 v3 chains;
- detect half-finished garbage-collector (GC) operations frozen into the array image, since there is no public API to pause the GC;
- refuse to back up whenever anything is unclear;
- be re-validated after every `sm` update.

LTRData.DiscUtils (MIT) covers LVM2 and VHD from .NET but has no QCOW2 reader ([DiscUtils](https://github.com/LTRData/discutils)).

This stays inside our policy. It uses open-source code and public specifications: VHD under Microsoft's Open Specification Promise, the QCOW2 spec, and the libvslvm description of LVM2 ([libvslvm LVM format](https://github.com/libyal/libvslvm/blob/main/documentation/Logical%20Volume%20Manager%20(LVM)%20format.asciidoc)). It is not a documented API in any supportable sense, though. It would also put backup01 on the storage network with its own iSCSI initiator. And it would need an array account with the "edit" role, which can delete production volumes unless it is confined to an array Virtual Domain.

**Verdict on question 1:**
- **Not now.** At most it could later become an opt-in, experimental accelerator, with an allow-list of tested `sm` versions and automatic fallback to NBD, and only if measured full-read throughput becomes the bottleneck.
- The clean version of this idea only arrives when a SMAPIv3 array driver gives each VDI its own array volume. Nothing like that is on HPE's horizon today.

### The Alletra side is the easy, documented part

The HPE half raises no policy problem. WSAPI v3 is public and documented, and HPE's own Apache-2.0 client FlowKit and the OpenStack Cinder driver both use it ([FlowKit](https://pypi.org/project/hpe-storage-flowkit-py/); [Cinder Alletra MP driver](https://docs.openstack.org/cinder/2026.2/configuration/block-storage/drivers/hpe-alletramp-driver.html)).

Licensing is not an obstacle either. The B10000 is sold as an all-inclusive subscription with "access to all operating system features" ([HPE B10000 data sheet](https://www.acp-gruppe.com/hubfs/HPE%20Alletra%20Storage%20MP%20B10000-Datenblatt-PSN1014727563DEDE.pdf)). The "Virtual Copy licence" that Veeam lists is a 3PAR-era feature name, not an extra purchase.

Alletra MP snapshots are redirect-on-write ([Blocks & Files](https://blocksandfiles.com/2023/04/12/row-row-the-alletra-mp-boat-block-and-file-extras/)), so a short-lived backup snapshot costs little capacity and adds no write penalty.

The local copy of the spec settles most of the open points from the notes:

| Open point | What the spec says ([HPE WSAPI v3 OpenAPI spec 2.5.0](https://developer.hpe.com/ws/api-spec)) | Consequence |
|---|---|---|
| `CREATE_SNAPSHOT_VOLUME` parameters | `POST /api/v3/volumes/{uid}`; only `action` is required. Optional parameters: `customName`, `namePattern` (`PARENT_TIMESTAMP`, `PARENT_SEC_SINCE_EPOCH`, `CUSTOM`), `readOnly` ("If true snapshot mode is read only else read/write"), `expireSecs` and `retainSecs` (uint64), `comment`, `id` | Confirmed; matches FlowKit |
| Does `CREATE_SNAPSHOT_APPSET` take `readOnly` and expiry? | Yes. It takes `snapshotName`, `vvNamePattern`, `readOnly`, `expireSecs`, `retainSecs` and `comment`, and is described as "Creates a virtual volume set snapshot" | Open point closed. The spec says nothing anywhere about consistency across set members |
| 201 vs 202 | Custom actions return 201 (created) or 202 ("Task to perform operation has been created"), both with a `Location` header. Tasks are polled at `/api/v3/tasks/{uid}` (`status`, `percentComplete`) | The client must handle both |
| Expiry and retention | For snapshot schedules the range is "[60-157680000]" s. Retention "cannot be removed or reduced once it is set", a volume "cannot be removed within its retention time", and the system default `VVRetentionTimeMax` is 14 days | Use `expireSecs` (at least 60 s) as a safety net if the product crashes; **never set `retainSecs`** |
| Export | `POST /api/v3/vluns` requires `volumeName` and `hostName` (prefix `set:` for sets) and creates a "VLUN template". `DELETE /api/v3/vluns/{uid}` removes the export. Hosts are created with `POST /api/v3/hosts` (`iscsiPaths`, `persona`, `domain`) | Confirmed. Deleting a snapshot that is still exported is not documented beyond a generic 409 ("the OS has rejected the operation"), so always unexport first |
| LUN identity | Volume objects expose `wwn`, `readOnly`, `copyOfID`, `parentID`, `expirationTime` and `retentionTime` | The proxy can check the WWN and the read-only flag before it opens a device |
| Least privilege | `/api/v3/roles` and `/api/v3/rights` are GET-only. `POST /api/v3/users` takes `domainPrivileges` whose `priv` "May be super, service, security_admin, edit, create, browse, or basic_edit" | The v3 API cannot create custom roles. Least privilege is only possible through a user scoped to a Virtual Domain |
| Session lifetime | `POST /api/v3/credentials` returns `key` and `sessionId`; `DELETE /api/v3/credentials/{sessionToken}` ends the session; `GET /server/info` returns `timeout` ("Idle session timeout") | Read the timeout at runtime instead of copying FlowKit's 14-minute session cache |

One further point follows from the hybrid design: **the B10000 consistency guarantee for application sets hardly matters.** The XAPI snapshot defines the restore point, and the base VHDs are read-only before the array snapshot is taken. A consistency-group snapshot is therefore needed only if an SR's volume group ever spans more than one LUN.

## Each disk pays a fixed 7–12 s toll of serial LVM work

**xapi snapshots one disk at a time.** `VM.snapshot` loops over the VM's disk attachments (VBDs). For each it starts an asynchronous `VDI.snapshot` and waits for it to finish before starting the next ([xapi_vm_clone.ml](https://github.com/xapi-project/xen-api/blob/61de57919e12c9f429acd72d4b1ee79dc203c180/ocaml/xapi/xapi_vm_clone.ml#L143-L223)).

**Each `VDI.snapshot` is a separate storage-manager (SM) driver process, and it must run on the pool master** ([LVMSR.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1747-L1752)). It holds the SR lock for the entire command, because `vdi_snapshot` belongs to the operations that need exclusive access to the SR ([LVMSR.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L82-L86); [SRCommand.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/SRCommand.py#L139-L170)).

Inside that process the disk's tapdisk is paused, the snapshot body runs, and only then is the disk unpaused ([LVMSR.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1711-L1745)). The snapshot body, in order:
1. activates the VHD chain;
2. checks free space;
3. creates a journal LV;
4. renames the leaf into a "base copy" and shrinks it;
5. creates two new LVs and writes VHD headers into them;
6. marks the base hidden and read-only;
7. creates a new CBT log;
8. registers both new VDIs in the SR metadata and in XAPI.

([LVMSR.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1747-L1906))

None of these steps copies data in proportion to the disk size: LVs are created without zeroing (`-W n`), and shrinking the base only moves the VHD footer ([lvutil.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L601-L611); [xapi-project: snapshots](https://xapi-project.github.io/features/snapshots/snapshots.html)).

**The cost is the number of round trips.** Counting from the code (not measured), one disk snapshot runs:
- about 25 LVM commands without CBT;
- about 25–35 more with CBT, because every CBT log operation re-scans all LVs in the volume group and activates and deactivates the log LV ([LVMSR.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L2331-L2408));
- 8–10 `vhd-util` calls and up to 10 `cbt-util` calls;
- 4–5 plugin calls to the host that runs the VM when that is not the master. These are pause, deactivate, refresh, CBT refresh and unpause, and the unpause loads the whole SR driver there ([LVMSR.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1259-L1316); [tapdisk_pause.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/tapdisk_pause.py#L203-L260)).

Every LVM call also passes through one host-wide first-in-first-out lock, `Fairlock("devicemapper")` ([lvutil.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L145-L195)). That lock is shared by all SRs, the GC and the multipath tools ([mpath_cli.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/mpath_cli.py#L39)). It was introduced after LVM lock starvation had been "known to result in a commant taking many minutes to get the lock" ([sm commit 125aa9ec](https://github.com/xcp-ng/sm/commit/125aa9eca4900b4acfff232eafd62a3423ffd99b)).

If each LVM command takes 100–200 ms, then 50–60 of them account for 5–12 s. That matches **7.3–8.8 s for one disk, 24.7 s for three and 40–47 s for four** (own measurements, backup01 session logs, 2026-11-02 to 2026-11-09). The per-command latency is the single most important unmeasured number in this analysis.

**The locks explain the contention.** Two 4-disk VMs snapshotted at the same time on one SR took 38–103 s (own measurements). Their per-disk SM processes take turns on the SR lock, so both VMs approach the combined total. The 103 s peak exceeds twice the 47 s single-VM worst case, which points to extra contention from three likely sources (inference):
- xapi's follow-up `vdi_update` processes, which briefly take the SR lock again;
- the GC, which polls for the SR lock every 3 s after a snapshot deletion wakes it ([cleanup.py](https://github.com/xcp-ng/sm/blob/d8ca026a32674e1d76a488c92c6dbb96d3775075/drivers/cleanup.py#L2331-L2357));
- queueing on the shared Fairlock.

Snapshots on alletra-mp01 and alletra-mp02 do not share an SR lock. They still meet at the Fairlock on xcp-ng1, so running both SRs in parallel helps, but less than twofold. The same lock applies to every client: Veeam's XAPI snapshots on the same SR queue against ours.

**The number of LVs, not the disk size, is what grows the cost.** Each full `lvs` scan and each metadata commit processes the volume group's whole text metadata. That metadata grows with every VDI, base copy, snapshot LV and `.cbtlog` LV ([lvmcache.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvmcache.py#L45-L105)).

XenServer's CA-410086 patch to lvm2 stopped `lvs` and `vgs` from statting `/dev` and reading 128 KiB from every block device. XCP-ng has shipped it since lvm2 2.02.180-18.2.1 ([CA-410086](https://github.com/xcp-ng-rpms/lvm2/blob/5a29d27736634ec544987afa0d5a25c6ed5ceb68/SOURCES/CA-410086_filter_out_unnecessary_devices)). The cost of parsing the metadata remains.

Two measured anomalies fit this picture and need checking:
- **Per-disk cost rises with disk count.** It is 8.2 s per disk for the 3-disk VM but 10–11.8 s per disk for 4-disk VMs. Likely causes are VM placement (the master skips the cross-host calls) or more LVs on the fuller SR.
- **The 12 MiB config drive is expensive.** It adds 15.4–17.4 s, more than a whole single-disk VM (28 s with it, 10.6–12.6 s without; own measurements). Disk size cannot explain that. Likely causes are SR placement, chain depth, CBT state or lock waits for that VDI, and the SMlog of one such snapshot will show which.

**Space is the other half of the cost on thick LVM.** A snapshot of a running thick VDI must find free space of about the VDI's allocated size before it starts ([LVMSR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/LVMSR.py)). The XCP-ng docs warn that a snapshot "can reserve up to the full disk size" ([XCP-ng docs: snapshots](https://docs.xcp-ng.org/vms/snapshots/)). On 2026-11-03 an SR had 1–4 % free, which is 41–164 GiB of 4 TiB, and Veeam skipped 22 VMs (own measurements). A 260 GiB VM cannot be snapshotted in that state.

Used capacity today is about 1.1 TiB on alletra-mp01 and 3.0 TiB on alletra-mp02, roughly 0.5 TiB more than the pool's 3.5 TiB of virtual disk size. Thick leaves are sized to their virtual size, so that surplus most likely sits in base copies, snapshots or chains the GC has not yet merged (inference; check 4 below accounts for it).

**Verdict on question 2:** the slowness is structural to SMAPIv1 LVM SRs (LVHD), and no supported tuning setting shortens the per-disk sequence. dom0's `lvm.conf` is "not user modifiable" and is overwritten on updates ([lvm2.spec](https://github.com/xcp-ng-rpms/lvm2/blob/8.3/SPECS/lvm2.spec)). The only per-SR LVM option, `other-config:lvm-conf`, affects only `lvremove` ([sm commit 014d4e8](https://github.com/xcp-ng/sm/commit/014d4e8)).

What can be optimised:
- how many disks are snapshotted;
- how many snapshots compete for one SR;
- where the VM runs;
- how large the volume group is;
- how long each snapshot lives.

## `VM.snapshot` freezes disks one at a time, so multi-disk restore points are skewed

The serial loop has a consequence beyond speed. **Each disk is paused, captured and unpaused on its own, and the next disk is paused only after the previous one has resumed.** The capture moment of disk *n* is therefore roughly *n − 1* per-disk times after disk 1 ([xapi_vm_clone.ml](https://github.com/xapi-project/xen-api/blob/61de57919e12c9f429acd72d4b1ee79dc203c180/ocaml/xapi/xapi_vm_clone.ml#L143-L223); [LVMSR.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1711-L1745)). For our 4-disk VMs the last disk is captured about **30–35 s after the first**. In the 103 s case, with two VMs on one SR, the gap exceeds a minute (inference from the measured totals).

Each disk on its own is a clean point-in-time image, because tapdisk drains in-flight I/O before the pause. The set of disks is not: it is a series of snapshots taken at different times, not one crash.

Our VSS freeze holds writes for at most 10 s. That covers a single-disk VM (7.3–8.8 s, with a thin margin that any SR contention erases). For a multi-disk VM it covers the first disk and at best the start of the second. Every later disk is captured after the guest has resumed writing.

For applications whose consistency spans disks, the result is worse than a crash. Examples:
- SQL Server with data and log files on separate VDIs;
- AD with `ntds.dit` and its logs on different disks;
- any spanned or striped volume, or Storage Spaces.

After such a crash the log on one disk and the data on another come from different moments, and the write-ahead ordering their recovery relies on is broken. A restore can start, and then recovery fails or silently loses committed transactions.

Two paths in our product already avoid this for the application data, though not for the rest of the VM (spec `2026-11-04-2026-consistent-snapshots-slow-storage`, admin guide):
- **SQL Server 2022+ with the job method "SQL Server snapshot":** the agent suspends the databases' writes through the T-SQL snapshot backup for the whole `VM.snapshot`. That has no 10 s limit, so data and log files are captured consistently even when they sit on different disks.
- **Domain controllers:** the agent copies the directory with `ntdsutil` IFM before the snapshot. The copy sits on one disk and is what the AD explore uses. A whole-VM restore of a DC still boots from the live `ntds.dit`, so it is affected if the database and its logs are on different disks.

The general fix, an in-guest shadow copy that is reverted when the backup is read (approach B of that spec), is still open. Until it is built, every other multi-disk VM whose snapshot outlasts the VSS hold is skewed.

Others do not hide this. Xen Orchestra dropped quiesced snapshots and "now always takes normal snapshots". It also documents that its "synchronized snapshots" across VMs narrow the gap between restore points but do "not make them simultaneous" ([XO backup features](https://docs.xen-orchestra.com/backups-and-dr/backup-features-and-settings)).

**Our label must change.** "Crash-consistent" should be reserved for:
- single-disk VMs;
- multi-disk VMs whose snapshot finished inside the VSS hold (which then count as application-consistent).

Any other multi-disk restore point should be labelled something like **"per-disk crash-consistent, not consistent across disks"**. The session log should record the time between the first and last disk capture, naming the VM, the SR and the host.

Three levers shrink or remove the skew:
- **Fewer disks:** `ignore_vdis` for disks that need no backup.
- **Parallel per-disk `VDI.snapshot` calls on different SRs inside one VSS freeze.** The gap becomes the slowest single SR's serial time instead of the sum.
- **`VM.checkpoint` for VMs that need strict consistency across disks.** It suspends the VM while all disks are snapshotted ([xapi_vm_snapshot.ml](https://github.com/xapi-project/xen-api/blob/master/ocaml/xapi/xapi_vm_snapshot.ml)). That makes it consistent across disks and slow: the VM stops for the time to write its RAM plus *n* per-disk times.

## Fifteen levers, from a firewall rule to SMAPIv3

The table orders the levers roughly by benefit to our pool. "Who acts" uses three owners: the pool admin, our product, and the Vates roadmap.

| Lever | Effect | Evidence | Risk | Who acts |
|---|---|---|---|---|
| Open TCP 10809 from backup01 to 10.26.3.132/.133 and use NBD + CBT | Incrementals read changed blocks only (dc1: about 95× less data). Snapshot lifetime drops from about 1:30 min to seconds. The leaf left for coalesce after `data_destroy` stays tiny | Own measurements; [CBT list changed blocks](https://docs.xenserver.com/en-us/xenserver/developer/changed-block-tracking-guide/list-changed-blocks) | Low (one firewall rule) | Pool admin (pending user task) |
| Exclude disks that need no backup (`ignore_vdis`, `[NOBAK]`) | Each excluded disk saves its whole per-disk cost: 28 s became 10.6–12.6 s | Own measurements; [xapi_vm_clone.ml](https://github.com/xapi-project/xen-api/blob/61de57919e12c9f429acd72d4b1ee79dc203c180/ocaml/xapi/xapi_vm_clone.ml#L424-L460); [XCP-ng docs](https://docs.xcp-ng.org/vms/snapshots/) | The disk is missing from the restore point and must be rebuilt on restore | Our product (implemented); pool admin (tagging) |
| One snapshot per SR at a time, both SRs in parallel, no overlap with Veeam on the same SR | Avoids the 38–103 s lock queue and keeps 1-disk VMs inside the VSS window. The cross-SR gain is capped by the Fairlock | Own measurements; [SRCommand.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/SRCommand.py#L139-L170); [lvutil.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L145-L195) | Low | Our product (built since 2026-11-04: one snapshot per SR, SRs in parallel, `XenSnapshotGate`); pool admin (Veeam windows) |
| Parallel per-disk `VDI.snapshot` on different SRs inside the VSS freeze | Wall time and disk skew become the slowest single SR, not the sum. A 4-disk VM split 2+2 would be roughly halved (inference) | [LVMSR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/LVMSR.py); [XenServer CBT guide](https://docs.xenserver.com/en-us/xenserver/developer/changed-block-tracking-guide/getting-started) | Needs the disks spread across SRs, and the product must capture VM metadata itself. In 2021 XO said this route worked "only for halted VM" ([XO 5.56](https://xen-orchestra.com/blog/xen-orchestra-5-56/)). Untested | Our product + pool admin; lab |
| Snapshot while the VM runs on the pool master | Skips 3–4 cross-host plugin calls and the CBT log activations; the size of the gain is unmeasured | [LVMSR.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1259-L1316) | Migration traffic; extra load on the master | Our product (`VM.pool_migrate`) or pool admin (placement); lab |
| Fewer LVs per volume group | Shorter `lvs` scans and metadata commits, several per disk; unquantified | [lvmcache.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvmcache.py#L45-L105) | Deleting the wrong `cbt_metadata` VDI forces a full backup | Our product (removes only its own tagged `cbt_metadata` VDIs, keeps the newest per disk); pool admin (everything else) |
| Keep `sm` and `lvm2` current; take the November 2026 LVMSR fixes | CA-410086 already removed the device-scan cost. Commit 7ae4311b removes the SR-lock journal check from the unpause path, so the pause gets shorter | [CA-410086](https://github.com/xcp-ng-rpms/lvm2/blob/5a29d27736634ec544987afa0d5a25c6ed5ceb68/SOURCES/CA-410086_filter_out_unnecessary_devices); [7ae4311b](https://github.com/xcp-ng/sm/commit/7ae4311b640dc6570432661de8e366f1a4dd8f15); [c3b24fdc](https://github.com/xcp-ng/sm/commit/c3b24fdccf92ee4b35000f57fdc044ca5d780e36) | Normal patch risk | Pool admin; Vates roadmap |
| Free-space headroom per SR of at least the largest VM's allocated size, plus a pre-flight check | Prevents failed snapshots and failed coalesces (Veeam skipped 22 VMs at 1–4 % free) | Own measurements; [LVMSR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/LVMSR.py); [XCP-ng storage docs](https://docs.xcp-ng.org/storage/) | Capacity cost | Pool admin; our product (pre-check) |
| More, smaller SRs (for example 4 × 2 TiB) | More SR locks that can work in parallel, smaller volume-group metadata | [SRCommand.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/SRCommand.py) | Migration effort, fragmented free space. A forum report describes very slow iSCSI SR operations with 90+ LUNs ([forum](https://xcp-ng.org/forum/topic/12429/slow-sr-creation)) | Pool admin; lab |
| QCOW2 disk format on `lvmoiscsi` | Snapshot creation is not faster. Leaf coalesce runs live, without a pause or a temporary snapshot, and reserves less space | [QCOW2 FAQ](https://docs.xcp-ng.org/storage/qcow2_faq/); [August 2026 update](https://xcp-ng.org/blog/2026/08/18/august-2026-updates-1-for-xcp-ng-8-3-lts/) | About 1.5 years of field use. CBT for QCOW2 on LVM is undocumented. Needs a storage migration with space for a full copy | Pool admin + our product; lab |
| Turn the GC off during the backup window (`other-config:gc=false`) | Removes GC competition for the SR lock | [cleanup.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/cleanup.py) | Chains grow if re-enabling fails; the product would be changing pool configuration | Lab only |
| `VM.checkpoint` for VMs that need cross-disk consistency | All disks captured at one guest state | [xapi_vm_snapshot.ml](https://github.com/xapi-project/xen-api/blob/master/ocaml/xapi/xapi_vm_snapshot.ml) | The VM is suspended for RAM write time plus *n* per-disk times | Our product (opt-in per VM); lab |
| `driver_params:type=single`, `lvm.conf` edits, changes to `cleanup.py` constants | At best marginal: one LV fewer, or a smaller metadata backup | [VDI.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/VDI.py); [lvm2.spec](https://github.com/xcp-ng-rpms/lvm2/blob/8.3/SPECS/lvm2.spec); [xcp-ng/xcp#323](https://github.com/xcp-ng/xcp/issues/323) | Unsupported, overwritten by updates; bigger coalesce limits mean longer VM pauses | Do not use (`type=single` is a research item only) |
| Backup from B10000 array snapshots | Shortens snapshot lifetime on full reads and moves reads off the hosts. Creation time and skew are unchanged | Section 1 | Layout outside any API contract, no vendor precedent, storage-network exposure | Our product (optional proof of concept only) |
| SMAPIv3 design, tapdisk performance, live coalesce, array integration | The only structural fix for creation speed | [Vates roadmap](https://docs.vates.tech/product-roadmap/) | No dates; HPE not named; VHD live coalesce not announced | Vates roadmap |

One unverified option sits outside the table. OpenStack documents an "HPE Alletra MP B10000 Share Driver" for file services ([Manila](https://docs.openstack.org/manila/2026.1/configuration/shared-file-systems/drivers/hpe-alletra-mp-b10000-driver.html)), so an NFS SR served from the same array might be possible. File-based SRs avoid LVM metadata work at snapshot time and keep snapshots thin. XO users on NFS reported 1.6–5 s per VM ([XCP-ng forum](https://xcp-ng.org/forum/topic/9268/cbt-the-thread-to-centralize-your-feedback/161)). It is worth one question to HPE before any SR redesign.

## Do now, test in the lab, wait for Vates

| # | When | Action | Who | Expected result |
|---|---|---|---|---|
| 1 | Now | Open TCP 10809 from backup01 to 10.26.3.132 and .133. Re-run dc1 and confirm that it took the NBD + CBT path (about 247 MiB read, snapshot held for seconds) | Pool admin | Ends the whole-disk re-reads and the long snapshot lifetimes; also shrinks coalesce work |
| 2 | Now | Grade restore points: application-consistent only if the VSS hold covered every disk capture; "per-disk crash-consistent, not consistent across disks" for other multi-disk VMs. Log the first-to-last disk gap. Warn for database and AD VMs with data and logs on different disks | Our product | Restore-point labels that match what was captured |
| 3 | Now | Keep at most one `VM.snapshot` per SR, with alletra-mp01 and alletra-mp02 in parallel (our product has done this since 2026-11-04). Keep Veeam jobs off the same SR during our backup window | Pool admin | No 38–103 s queues; single-disk VMs stay inside the VSS window |
| 4 | Now | Tag config drives and scratch or pagefile disks `[NOBAK]` | Pool admin (tags), our product (honours them) | About 15–17 s less per config drive |
| 5 | Now | Pre-flight check per SR: free space at least the VM's allocated disk size plus a margin. On failure, skip with a message naming the SR, its free space and the VM | Our product | No snapshot or coalesce failures at low free space |
| 6 | Now | Keep only the newest own `cbt_metadata` per disk and never leave own snapshots behind | Our product | Smaller volume groups and faster LVM scans |
| 7 | Now | Run the read-only checks in the next section | Pool admin / us | Settles latency, LV counts, the placement effect and the config-drive anomaly |
| 8 | Lab | Time the same multi-disk VM resident on xcp-ng1 vs xcp-ng2 | Our product | Quantifies the placement lever |
| 9 | Lab | Parallel `VDI.snapshot` of a running VM's disks on two SRs inside one VSS freeze; verify the CBT chain, `list_changed_blocks` and a restore | Our product | Could bring split 4-disk VMs down to about 20 s with much less skew |
| 10 | Lab | QCOW2 on `lvmoiscsi`: CBT, `data_destroy`, NBD reads, snapshot time, live leaf coalesce | Our product + pool admin | Go/no-go on migrating to QCOW2 |
| 11 | Lab, only if full reads over NBD prove too slow | SAN-mode proof of concept: read-only snapshot with `expireSecs`, export to an isolated proxy host, check the WWN, parse with DiscUtils, compare throughput with NBD | Our product | Data for a future decision; not a roadmap item |
| 12 | Wait | `sm` with 7ae4311b and c3b24fdc; Vates "Performance improvement for XCP-ng 8.3 (tapdisk)"; SMAPIv3 design; any HPE array integration; live coalesce for VHD | Vates roadmap | The only real fix for how long each disk snapshot takes |

Do not:
- make SAN mode a default;
- edit `lvm.conf` or `cleanup.py`;
- set `retainSecs` on any array snapshot the product must clean up;
- delete pool objects the product did not create and tag.

## Read-only checks on xcp-ng1 settle the open points

Run these on the pool master xcp-ng1 (10.16.1.231), outside the backup window. All of them only read state. None activates LVs, rescans SRs or changes configuration.

| # | Check | Command | Settles |
|---|---|---|---|
| 1 | Package levels | `rpm -q sm lvm2 blktap xapi-core` | Whether CA-410086 (lvm2 2.02.180-18.2.1 or later) and the October 2026 `sm` fixes (3.2.12-25.x) are installed; baseline for later updates |
| 2 | SR identity | `xe sr-list type=lvmoiscsi params=uuid,name-label,physical-size,physical-utilisation,virtual-allocation` and `xe pbd-list sr-uuid=<sr> params=host-name-label,device-config` | SR UUIDs and SCSIids for the following checks; true free space |
| 3 | Volume group size | `vgs --readonly -o vg_name,pv_count,lv_count,vg_mda_size,vg_mda_free VG_XenStorage-<sr>` | LV count of mp01 vs mp02, metadata area usage, single-LUN volume group |
| 4 | LV inventory | `lvs --readonly -o lv_name,lv_size,lv_tags VG_XenStorage-<sr>` | Base copies, snapshot LVs, `.cbtlog` LVs, leftover journal and `OLD_` LVs; where the roughly 0.5 TiB above virtual size sits |
| 5 | LVM latency | `time env LVM_SYSTEM_DIR=/etc/lvm/master LVM_DEVICE=/dev/disk/by-scsid/<SCSIid> lvs --readonly VG_XenStorage-<sr> >/dev/null` (5 times per SR) | Whether 50–60 LVM calls explain 7–12 s per disk; whether the fuller SR is slower |
| 6 | SMlog step timing | `grep -e 'LVMVDI._snapshot' -e 'Calling tap-' -e 'Long LVM call' -e 'snapshot paused VM' /var/log/SMlog*`, then `grep 'SM: \[<pid>\]' /var/log/SMlog` for one snapshot | LVM commands per disk, length of the pause, whether timestamps have sub-second resolution, outliers above 15 s |
| 7 | xapi timing | `grep -e 'Snapshot: begin' -e 'Snapshot: end' /var/log/xensource.log*` | xapi time vs SM time; the gap between disk captures |
| 8 | VM placement | `xe pool-list params=master` and `xe vm-list power-state=running params=name-label,resident-on` | Per-disk times for VMs on the master vs on xcp-ng2 |
| 9 | Disk layout | `xe vbd-list vm-name-label=<vm> type=Disk params=userdevice,vdi-uuid`, then `xe vdi-list uuid=<vdi> params=name-label,sr-uuid,virtual-size,physical-utilisation,cbt-enabled,sm-config` | SR of each disk (potential for parallel per-SR snapshots), CBT state, VHD vs QCOW2, parent chain; the cause of the config-drive anomaly |
| 10 | CBT metadata | `xe vdi-list type=cbt_metadata params=uuid,name-label,sr-uuid` | Stale `cbt_metadata` VDIs per disk, ours vs Veeam's |
| 11 | Lingering snapshots | `xe vm-list is-a-snapshot=true params=name-label,snapshot-of,snapshot-time` | Snapshots (ours or Veeam's) holding thick space and lengthening chains |
| 12 | GC state | `xe sr-param-get uuid=<sr> param-name=other-config`, `grep -e coalesce -e GC /var/log/SMlog`, `xe task-list` during a backup | GC disabled? Coalesce running during our snapshots? Errors such as "Unexpected bump in size"? |
| 13 | NBD network | `xe network-list name-label=VeeamNBD params=uuid,purpose,bridge` and `xe pif-list network-name-label=VeeamNBD params=host-name-label,IP` | NBD purpose (`nbd` or `insecure_nbd`) and the .132/.133 addresses our client must reach and match |
| 14 | Multipath and dom0 | `multipath -ll`, `xl info`, `xl vcpu-list 0`, `free -m` | Path health (multipath shares the LVM lock); dom0 CPU and memory headroom |
| 15 | LVM metadata backups | `grep -e 'backup =' -e 'archive =' /etc/lvm/master/lvm.conf` | Whether every metadata change also rewrites a backup file |

If a SAN-mode proof of concept is ever started, the matching read-only array checks are:
- `GET /server/info` for the OS version and the idle session `timeout`;
- `GET /api/v3/roles` for the available roles;
- whether the SR LUNs sit in a Virtual Domain.

## Conclusion

The bottleneck in our backup cycle is metadata work on the pool master, not moving data. That changes what the investment should be. Anything that moves bytes faster, whether a SAN read path, a faster array or more bandwidth, leaves untouched the 7–12 s per disk that set both the snapshot time and the disk-to-disk skew. Only three things change it: fewer disks per snapshot, more independent SR locks, or a different storage stack. The one byte-moving fix that does matter, opening the NBD port, matters for a side effect. It shortens snapshot lifetime, and on thick LVM a short-lived snapshot also means a small leaf to coalesce and less time with reserved space. One firewall rule therefore improves reading, coalesce and the free-space risk at once.

The more important finding concerns honesty, not speed. Neither Veeam nor Xen Orchestra makes multi-disk XAPI snapshots consistent across disks. Our product does so only for SQL Server databases (T-SQL snapshot backup) and the AD database copy (IFM), not for the VM as a whole. A product that measures and reports the gap between disk captures, and grades restore points accordingly, gives administrators something the incumbents do not. Array-snapshot backup is worth revisiting only if one of three things happens:
- measured NBD full reads become the limiting factor;
- Vates ships a SMAPIv3 array driver with one volume per VDI;
- HPE appears on the Vates roadmap.

Until then the B10000 integration is cheap to write and expensive to keep correct, and nothing it would gain is currently our bottleneck.
