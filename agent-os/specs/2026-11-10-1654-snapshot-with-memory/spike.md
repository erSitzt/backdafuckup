# Spike: Snapshot with Memory in the Lab

The pool is XCP-ng 8.3 with two hosts. The VM is bdfu-lab-sql2: Windows Server, 8 GiB RAM, disks C: (60 GiB) and D: (10 GiB) plus the config drive, all on alletra-mp02 (shared LVM over iSCSI). It ran on xcp-ng1, the pool master. The job was "Memory spike": manual, the VM with memory, no guest processing. Server build `9c85155`.

| # | Question | Result |
|---|---|---|
| S1 | How long does the VM pause? What does the checkpoint look like? Is anything left over? | Answered: 310–323 s with the host's local suspend storage, 118 s with the Alletra. Nothing left over. |
| S2 | Does a restored VM resume? | Yes, as far as XAPI shows (suspended import, `VM.resume`, Running). The guest's view is open. See below. |
| S3 | Does a VSS freeze survive the pause? | No. VSS and the checkpoint stay exclusive. See below. |
| S4 | Can a copy resume without network connections? | Yes: XAPI removes the NIC of the suspended VM. |
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

**Again with the fix, 2026-11-10 22:17** (session 01a1c752, build `7a76f58`, host suspend storage still local): paused **323.0 s**. The session log named the local storage ("its memory image goes to the local storage 'xcp-ng1-local' of its host …"), and the free-space line now lists both storages: "3 disk(s) on 'alletra-mp02' (25.8 % free), 'xcp-ng1-local' (96.6 % free)". The memory image: 8.0 GiB read, 1.9 GB of it already stored from the first run, 1.6 GB stored. The user then set the pool's suspend storage to the Alletra.

**With the Alletra as the pool's suspend storage, 2026-11-10 22:26** (session 01a1c75b, the S3 run below): paused **118.0 s**, the memory image on alletra-mp02, no hint about local storage. 10.2 GiB (8.0 GiB allocated) plus three disk snapshots (about 25 s) gives roughly 90 MB/s for the RAM write: still about two minutes for 8 GiB.

**The guest's clock after the pause:** at 22:35 sql2's clock was **421 s behind** (WinRM `[DateTime]::UtcNow` against this machine).
- **After S1 (18:05):** Windows set the clock from the hardware clock (Kernel-General 1: "System time synchronized with the hardware clock", +310,464 ms).
- **After the two checkpoints of 22:17 and 22:26:** no such event.
- **Time source:** sql2 has none (`w32tm`: "Free-running System Clock", time.windows.com unreachable), so nothing corrected the clock.

A domain member whose clock is more than 5 minutes off fails Kerberos until its next time sync. A VM backed up with memory needs a working time source, and a short pause.

## S3, 2026-11-10 22:26 (session 01a1c75b)

`BDFU_Engine__CheckpointWithVss=true` in the service's environment, guest processing VSS (copy-only) in the spike job, the agent connected.
- **Server:** "VSS prepared … 10 VSS writer(s) … 10 SQL database(s)", then the checkpoint paused the VM for 118.0 s, then "thawing the guest failed: the agent … did not answer within 120 s". The restore point is crash-consistent; the backup itself worked.
- **Guest** (its clock about 5 min behind, see above): `VSS prepared` at 22:21:20. At 22:22:20 the VSS event **12298**: the writes could not be held during the shadow copy creation (the flush-and-hold limit of 10 s ran out). At the same second SQL Server's writer aborted the backups of all 10 databases (3041, 18210 with OS error 995, SQLWRITER 24583). They were copy-only, so the backup history is untouched.

**Decision (task 6):** VSS and the checkpoint stay exclusive, as built: a VM with memory gets no guest processing. The pause is far longer than VSS can hold writes, and the guest notices the gap only after the resume. `Engine:CheckpointWithVss` stays a spike switch (default false), and it was removed from the service's environment afterwards (22:35).

## S2 and S4, 2026-11-10 22:41–22:51 (build `ed24db3`)

**Backup** (session 01a1c769, guest processing off, the pool's suspend storage on the Alletra): paused **119.8 s**; the memory image (10.2 GiB, 8.0 GiB allocated) read in 32 s, 1.4 GB stored. The restore point records the checkpoint's disk ids (`MemoryDiskIds`).

**Restore** (session 01a1c76e) to another location as `bdfu-lab-sql2-resumed`, *Resume with memory*, *Resume the VM*, all on alletra-mp02: **3:47 min**.
- **Upload:** 3 disks (22.7 + 2.3 GiB and 2 MiB of data) and the memory image as a `suspend` VDI (7.4 of 10.2 GiB with data).
- **Import (S2):** `import_metadata` of the checkpoint's metadata with the disk map and `check_cpu=true` gave a **Suspended** VM with the uploaded memory image as its suspend VDI. The VM had no VM generation id; sql2 has none either.
- **NIC (S4):** `VIF.destroy` on the suspended VM worked: "NIC 0 (MAC a6:20:0b:33:64:4a) removed".
- **Resume:** `VM.resume` succeeded. The inventory (rescan) shows the copy **Running**; the suspend VDI is gone after the resume, and 3 disks remain.

**Open:** the guest's view on the console (uptime from before the backup, programs and SQL Server running, the clock). The copy has no network, so it can only be checked there.

**Noticed:**
- The copy keeps the original's description ("Created and removed by OpenTofu only"), as every restored VM does.
- The log said the VM "continues where it was at the backup (22:46)", the restore point's time; the memory is from the checkpoint at 22:41. The message now names the snapshot with memory of the restore point.

**Left in the pool for the user to delete:** the VM `bdfu-lab-sql2-resumed` (2a0f4a11-cdd3-d45d-64df-2f21132e503b) with its 3 disks.

## S5

`VDI.get_nbd_info` on the suspend VDI returns endpoints: one here, only on its host, because the storage is local. The NBD read itself could not be checked, because TCP 10809 from the backup server is blocked, as for all disks.

The HTTP export of the suspend VDI as VHD works.
