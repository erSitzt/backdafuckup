# Spike: Snapshot with Memory in the Lab

The pool is XCP-ng 8.3 with two hosts. The VM is bdfu-lab-sql2: Windows Server, 8 GiB RAM, disks C: (60 GiB) and D: (10 GiB) plus the config drive, all on alletra-mp02 (shared LVM over iSCSI). It ran on xcp-ng1, the pool master. The job was "Memory spike": manual, the VM with memory, no guest processing. Server build `9c85155`.

| # | Question | Result |
|---|---|---|
| S1 | How long does the VM pause? What does the checkpoint look like? Is anything left over? | Partly answered. See below. |
| S2 | Does a restored VM resume? | Open (restore part). |
| S3 | Does a VSS freeze survive the pause? | Open (needs `Engine:CheckpointWithVss=true`). |
| S4 | Can a copy resume without network connections? | Open (restore part). |
| S5 | Can the suspend VDI be read over NBD? | Partly answered. See below. |

## S1, 2026-11-10 18:02 (session 01a1c668)

**Pause: 310.2 s** (`VM.checkpoint` task, `bdfu_snapshot_duration_seconds{kind="checkpoint"}`). XAPI wrote the RAM image to **xcp-ng1-local**, the host's local `ext` storage, not to the Alletra:
- neither the VM nor the pool has a suspend storage;
- XAPI then takes the host's `suspend_image_sr`.

The image is 10.2 GiB (virtual), 8.0 GiB of it allocated. That works out to roughly 28 MB/s for the RAM write, after subtracting about 25 s for three disk snapshots, which matches a slow local disk. A Windows server and its SQL Server were unreachable for over 5 minutes.

**The checkpoint snapshot:**
- `power_state` Suspended;
- `is_a_template` true;
- `suspend_VDI` set, a VDI of type `suspend`, on the host's local storage.

**Cleanup:** after the backup there was no snapshot VM of ours (`bdfu_pool_snapshots{owner="backdafuckup"}` 0) and no pending artifact (`bdfu_artifacts_pending` 0). On xcp-ng1-local, the disk count went 0 → 1 (suspend VDI) during the backup and back to 0 after it. The one snapshot VDI and the used space there are unchanged since the morning (someone else's older snapshot).

**Backup** (full, first run):
- 80.2 GB processed, 34.1 GB read in 2:56 min;
- the RAM image: 8.0 GiB read (HTTP export, 160 MiB/s), 3.2 GB of zero pages, **2.0 GB stored** (compression 3.5×).

**Found and fixed:** the product took the suspend storage from the VM or the pool, else the pool's default storage. XAPI's order is the VM's `suspend_SR`, then the pool's `suspend_image_SR`, then the host's `suspend_image_sr`. The product used alletra-mp02 for the free-space check and the storage gate, while XAPI wrote to xcp-ng1-local.

The fix takes XAPI's order, without the pool default storage. When the image goes to a local storage, the session log says so and names how to shorten the pause.

**Next:** set a fast shared suspend storage, then measure again:
- for the VM: `xe vm-param-set uuid=<vm> suspend-SR-uuid=<alletra-mp02>`;
- or for the pool: `xe pool-param-set suspend-image-SR=<sr>`.

This is a pool setting and the user's decision.

## S5

`VDI.get_nbd_info` on the suspend VDI returns endpoints: one here, only on its host, because the storage is local. The NBD read itself could not be checked, because TCP 10809 from the backup server is blocked, as for all disks.

The HTTP export of the suspend VDI as VHD works.
