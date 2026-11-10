# Levers for faster, less disruptive VM snapshots on XCP-ng 8.3 with `lvmoiscsi` (SMAPIv1, VHD), as of November 2026

Evidence base: public docs, blog posts and forum threads, plus open-source code read on 2026-11-10:
- `xcp-ng/sm`, branch `3.2.12-8.3-devel`, HEAD `538c3e8` (2026-11-09). This is the devel branch. Some commits may not be in the stable 8.3 RPMs yet, so check `rpm -q sm` on the hosts.
- `xapi-project/xen-api` master.
- `vatesfr/xen-orchestra` master `2e67209` (2026-11-10).
- `xcp-ng-rpms/lvm2`, branch `8.3`.

No commercial binaries were used.

## 1. Pool-side tunables (lvm.conf, multipath, dom0, LV count, SR size/count, iSCSI, GC/coalesce keys, sm-config)

### Takeaway
There is almost no supported pool-side knob that shortens an LVHD snapshot.
- dom0's `lvm.conf` is shipped "not user modifiable" and is overwritten on every update. It already has lvmetad, archive, udev sync and AIO turned off.
- The only per-SR LVM knob (`other-config:lvm-conf`) applies only to `lvremove`.

The useful pool-side levers are structural rather than tuning:
- **More, smaller SRs.** The SR lock serialises snapshots per SR.
- **Fewer LVs per VG.**
- **Current lvm2/sm packages.** These include the 2025 device-scan filter and the October 2026 iSCSI/CBT fixes.
- **Possibly running the VM on the pool master,** which skips on-slave plugin round-trips.
- **GC/leaf-coalesce keys.** `SR other-config:gc`, `VDI other-config:leaf-coalesce` and the hard-coded `LIVE_LEAF_COALESCE_*` constants control coalesce. They do not control snapshot creation.

### Cited Findings

**What a single LVHD snapshot does (explains the 5–9 s per disk)**
- xapi's `VM.snapshot` snapshots a VM's disks strictly one after another. `safe_clone_disks` uses `List.fold_left` over the VBDs and calls `clone_single_vdi` per disk, with no parallelism. — [xapi_vm_clone.ml](https://github.com/xapi-project/xen-api/blob/master/ocaml/xapi/xapi_vm_clone.ml)
- SM serialises per SR. `SRCommand._run_locked` acquires the SR lock (`sr.lock.acquire()`) for VDI commands. Two snapshots on the same SR therefore wait for each other, while snapshots on different SRs do not share a lock. — [SRCommand.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/SRCommand.py)
- The VM's I/O is stalled for the whole duration of each disk's snapshot. `LVMVDI._do_snapshot` calls `blktap2.VDI.tap_pause()` before `_snapshot()` and `tap_unpause()` after it. It logs `WARNING: snapshot paused VM for N seconds` only above `LONG_SNAPTIME = 60` s. — [LVMSR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/LVMSR.py)
- Inside `LVMVDI._snapshot` (master only; raises `LVMMaster` otherwise), the work is a long series of serial LVM and VHD operations:
  - activate the whole chain (`_chainSetActive`), then check VHD depth against the max chain length (`SnapshotChainTooLong`);
  - `_ensureSpaceAvailable`, then create a clone journal LV;
  - `lvrename` the leaf into a "base copy", then deflate it with `lvresize`;
  - `lvcreate` a new leaf (thick: `fullpr`, the full virtual size plus VHD overhead), then `vhd-util snapshot`;
  - `lvcreate` plus `vhd-util snapshot` for the second (snapshot) VDI, because `SNAPSHOT_DOUBLE` is the default for `VDI.snapshot`;
  - mark the base hidden and set it read-only (`lvchange -p r`; skipped for QCOW2);
  - for CBT-enabled disks, `_cbt_snapshot` (a new CBT log LV);
  - remove the journal, then `_finishSnapshot` with refcount/stat updates.

  — [LVMSR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/LVMSR.py), [VDI.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/VDI.py)
- If the VDI is attached on a non-master host, the snapshot adds synchronous `host.call_plugin(... "on-slave", "multi" ...)` round-trips. The calls are `_deactivateOnSlave`, `_updateSlavesOnClone` (refresh + activate) and, with CBT, `_updateSlavesOnCBTClone`. `tap_pause`/`tap_unpause` also call the plugin on the host running the VM. `call_on_slave` explicitly skips the master (`if hostRef == master_ref: continue`). — [LVMSR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/LVMSR.py), [blktap2.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/blktap2.py)
- For a snapshot of an attached VDI, the new leaf LV is created at full size (`if hostRefs: lvSizeOrig = fullpr`) even on a "thin" LVM SR. On thick SRs the leaf is always full size unless it is itself a snapshot. — [LVMSR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/LVMSR.py)

**lvm.conf in XCP-ng 8.3 (lvm2 7:2.02.180-18.3.1, Feb 2026)**
- The spec rewrites `/etc/lvm/lvm.conf` at build time. It sets `use_lvmetad = 0`, `archive = 0`, `udev_sync = 0`, `udev_rules = 0`, `obtain_device_list_from_udev = 0`, `write_cache_state = 0` and `use_aio = 0`. It also creates `/etc/lvm/master/lvm.conf` with `metadata_read_only = 0`; SM switches to this file via `LVM_SYSTEM_DIR` on the master. — [lvm2.spec (8.3)](https://github.com/xcp-ng-rpms/lvm2/blob/8.3/SPECS/lvm2.spec), [lvutil.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/lvutil.py)
- The spec comments: "lvm config is 'magic' and not user modifiable, always overwrite". Local edits to `lvm.conf` are lost on lvm2 updates. — [lvm2.spec (8.3)](https://github.com/xcp-ng-rpms/lvm2/blob/8.3/SPECS/lvm2.spec)
- Patch CA-410086 (Citrix, 2025-04-29) addresses scan overhead. Its description: "The default configuration of LVM2 results in the `lvs` and `vgs` commands performing a lot of unnecessary work by 1. running stat and lstat on everything in and below `/dev/` 2. Opening all the block devices it finds and reading the first 128K from them, including the Logical Volumes which form the SR VDIs". The fix sets `scan = [ "/dev/disk/by-id" ]` and filters out `dm-name-VG_XenStorage.*`, `dm-uuid-LVM-.*` and `dm-name-XSLocal.*`. XCP-ng rebased onto this as 7:2.02.180-18.2.1 on 2025-09-05. Another config update (CP-31079) followed in 18.3 / 18.3.1 (Feb 2026). — [CA-410086 patch](https://github.com/xcp-ng-rpms/lvm2/blob/8.3/SOURCES/CA-410086_filter_out_unnecessary_devices), [lvm2.spec changelog](https://github.com/xcp-ng-rpms/lvm2/blob/8.3/SPECS/lvm2.spec)
- A further patch makes LVM "only scan the physical devices passed via LVM_DEVICE env var … This will speed up a lot lvm commands when there are many LVs". SM sets `LVM_DEVICE` in `SR.py`. — [scan_only_the_device_in_LVM_DEVICE_env.diff](https://github.com/xcp-ng-rpms/lvm2/blob/8.3/SOURCES/scan_only_the_device_in_LVM_DEVICE_env.diff), [SR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/SR.py)
- SM creates SR PVs/VGs with `--metadatasize 10M`. This is fixed at SR creation, so the metadata area is not a runtime tunable. — [lvutil.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/lvutil.py)
- The only per-SR LVM config hook is `xe sr-param-set uuid=<SR> other-config:lvm-conf=issue_discards=1`. Its commit message says the config "is only used by 'remove' calls from LVMCache" (commit 014d4e8, 2024-07-30). — [sm commit 014d4e8](https://github.com/xcp-ng/sm/commit/014d4e8)

**GC / coalesce keys (current code)**
- `SR other-config:gc=false` disables the GC for that SR: "GC is disabled for this SR, abort". — [cleanup.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/cleanup.py)
- `VDI other-config:leaf-coalesce` takes two values. `false` means "do not leaf-coalesce". `force` means "skip snap-coalesce", i.e. do a paused live leaf coalesce regardless of size. SM sets `offline` for information only. — [cleanup.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/cleanup.py)
- Hard-coded constants: `LIVE_LEAF_COALESCE_MAX_SIZE = 20 MiB`, `LIVE_LEAF_COALESCE_TIMEOUT = 10 s` and `TIMEOUT_SAFETY_MARGIN = 0.5`. A VHD leaf is live-coalesced (with the VM paused) only if allocated size / measured speed < 5 s, or < 20 MiB when no speed is known. Otherwise SM repeatedly "single-snapshots" the leaf and coalesces the temporary snapshot (`MAX_ITERATIONS = 20`, `MAX_ITERATIONS_NO_PROGRESS = 3`). — [cleanup.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/cleanup.py)
- The GC loop sleeps `GCPAUSE_DEFAULT_SLEEP = 5*60` s ("GC active, about to go quiet") between active phases. — [cleanup.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/cleanup.py)
- `SR other-config` also supports a coalesce-error message rate key (`COALESCE_ERR_RATE_TAG`), which controls only XenCenter alert spam. — [cleanup.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/cleanup.py)
- LVMSR accepts `allocation=thin|thick` via `device-config:allocation` or `sm-config:allocation`, default `thick`, along with an `lvhd-thin` plugin. XCP-ng docs still list iSCSI/HBA as "Shared, thick-provisioned storage". — [LVMSR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/LVMSR.py), [docs.xcp-ng.org/storage](https://docs.xcp-ng.org/storage/)

**Recent fixes relevant to this pool**
- The October 2026 #1 update for 8.3 includes two fixes:
  - "Concurrent operations could fail when creating or updating disks on iSCSI SRs, and could block later iSCSI operations"; reliability improved "especially with many LUNs and multipath connections".
  - On shared LVM SRs (iSCSI, HBA), "deleting a CBT log on a non-master host could damage the SR's LVM metadata". The matching sm commit is a8cf2bf, "fix(LVMSR): correct CBT log deletion on slave", 2026-09-11.

  — [October 2026 Updates #1](https://xcp-ng.org/blog/2026/10/05/october-2026-updates-1-for-xcp-ng-8-3-lts/), [sm commit a8cf2bf](https://github.com/xcp-ng/sm/commit/a8cf2bf)
- XenServer sm patches imported into XCP-ng sm in 2025–2026:
  - CP-311724 "optimise_tapdisk_from_minor" (adds an `lsof`-based PID lookup in `tapdisk-pause`; sm-3.2.12-21.xs8);
  - CP-312848 "allow tapdisk-pause.Pause to be idempotent when safe";
  - CP-313057 "improve_failure_path_speed" (sm-3.2.12-25.xs8).

  — [xcp-ng/sm history](https://github.com/xcp-ng/sm/commits/3.2.12-8.3-devel), [commit f72c835](https://github.com/xcp-ng/sm/commit/f72c835), [commit 04ee75f](https://github.com/xcp-ng/sm/commit/04ee75f)

**Multipath, dom0, iSCSI**
- XCP-ng docs require the Multipathing page steps before creating iSCSI/FC SRs but give no further tuning advice. — [docs.xcp-ng.org/storage](https://docs.xcp-ng.org/storage/)
- dom0 memory: XCP-ng docs advise "at least 2GiB of RAM for Dom0", set via `xen-cmdline --set-xen dom0_mem=4096M,max:4096M`. — [dom0 memory guide](https://docs.xcp-ng.org/guides/dom0-memory/) (via search snippet)
- No official dom0 vCPU guidance was found. Forum users attribute per-disk I/O limits to single-threaded tapdisk. — [forum post 81167](https://xcp-ng.org/forum/post/81167) (via search snippet)
- A 2026 forum thread reports slow iSCSI `lvmoiscsi` control-plane operations on a Huawei array with multipath and 90+ LUNs. LUN listing took 4–5 min, validating one LUN 1–3 min, and PBD plug on the master 10–20 min. No root cause was published. A Vates staffer suggested timing `xe sr-probe` directly on the host. — [Slow SR Creation](https://xcp-ng.org/forum/topic/12429/slow-sr-creation)

### Inferences
- **Why 5–9 s per disk.** On LVHD each disk snapshot is roughly 10–20 serial LVM metadata commits and `vhd-util` calls on the master. Each LVM command re-reads and re-writes the VG text metadata synchronously over iSCSI (lvmetad off, AIO off). There are also 2–4 XAPI plugin round-trips when the VM runs on the non-master host. The VM's disk is paused for that whole time. This matches 5–9 s per disk and roughly linear scaling (4 disks ≈ 40–47 s). Array write latency for small synchronous writes and VG metadata size probably dominate. No public measurement breaks this down, so confirm with `/var/log/SMlog` timestamps on the master.
- **Run the VM on the pool master (test).** A VM running on the master skips `_deactivateOnSlave`, `_updateSlavesOnClone` and `_updateSlavesOnCBTClone`, and its tap-pause/unpause are local. Compare the same VM on master vs non-master. Needs no code change: XAPI `VM.pool_migrate` before backup, or VM placement.
- **Fewer LVs per VG (pool hygiene, tool-assisted).** Every VDI, base copy, CBT log (`cbt_metadata` VDI), journal and the MGT volume is an LV. The VG metadata text grows with LV count, and every LVM command parses all of it. Ways to reduce it:
  - keep only the latest `cbt_metadata` per disk;
  - remove orphaned base copies and snapshots;
  - split large VGs.

  CA-410086 removed the device-scan part of the LV-count cost, but metadata parse/commit cost remains (inference).
- **More, smaller SRs (pool change).** SR-level serialisation is the reason "snapshots on one SR wait for each other". Two SRs already give two independent lock domains. Splitting each 4 TiB LUN into, say, 2×2 TiB SRs doubles the possible concurrency and roughly halves VG metadata per SR. Placing a multi-disk VM's disks on different SRs lets the tool snapshot them in parallel (see section 2). Risk: more LUNs/paths make the iSCSI control plane slower (see the 2026 Huawei report), and free space is fragmented across SRs.
- **lvm.conf tuning is not a practical lever.** It is overwritten on update and already minimal. Possible remaining tweaks: `backup = 0`, which stops writing `/etc/lvm/backup/<VG>` on every metadata change (unverified benefit, loses recovery copy), and fewer metadata copies (needs PV recreate). Both are unsupported and need a re-apply after updates.
- **Do not edit `cleanup.py` constants for snapshot speed.** They only affect coalesce. Raising `LIVE_LEAF_COALESCE_TIMEOUT` lengthens VM pauses during coalesce (see section 3).

### Gaps
- No public benchmark breaks down LVHD snapshot time (LVM commands vs plugin calls vs tap-pause) or measures VG-size/LV-count effects on 8.3.
- No Vates/XCP-ng guidance found on multipath settings (path_grouping, `no_path_retry`, ALUA) or iSCSI session parameters with respect to snapshot latency. Nothing specific to HPE Alletra Storage MP B10000 was found.
- No official dom0 vCPU sizing guidance for storage-heavy pools was found.
- I did not verify whether `allocation=thin` on `lvmoiscsi` is supported or tested by Vates in 8.3. Only the code path exists; docs say iSCSI is thick.
- I could not verify which of the devel-branch sm commits cited above are already in the stable 8.3 RPMs.

## 2. Backup-tool-side levers (fewer disks, VDI.snapshot vs VM.snapshot, checkpoint, parallelism, ordering, data_destroy vs keeping a snapshot, CBT, how XO does it)

### Takeaway
The biggest tool-only lever is to stop paying the per-disk cost serially:
- Skip disks that do not need backup (`ignore_vdis`).
- Issue per-disk `VDI.snapshot` calls in parallel across different SRs while the guest's VSS freeze is held. Same-SR snapshots stay serial because of the SM SR lock.

`VM.snapshot` gives no cross-disk atomicity anyway: each disk is paused and snapped in turn. `VM.checkpoint` is slower because it suspends the VM.

XO's approach (2026):
- `VM.snapshot` with `ignore_vdis` for `[NOBAK]` disks;
- `snapshotConcurrency` 2 by default;
- optional "synchronized snapshots";
- chain-health check before snapshotting;
- with CBT purge: `VM.destroy` of the snapshot, then `VDI.data_destroy` per disk.

### Cited Findings
- **`ignore_vdis` in XAPI.** XAPI `VM.snapshot` takes `~ignore_vdis`, and `Xapi_vm_clone.clone` filters out VBDs whose VDI is in that list. — [xapi_vm_snapshot.ml](https://github.com/xapi-project/xen-api/blob/master/ocaml/xapi/xapi_vm_snapshot.ml), [xapi_vm_clone.ml](https://github.com/xapi-project/xen-api/blob/master/ocaml/xapi/xapi_vm_clone.ml)
- **`[NOBAK]` history in XO.** XO first excluded `[NOBAK]` disks by using `VDI.snapshot` (XO 5.56, 2021-02-26), but "this option is now working only for halted VM, because of XAPI limitations". XAPI later gained `ignore_vdis` on `VM.snapshot` (xen-api PR #4563), available since 8.3. On 8.2, XO only skips exporting `[NOBAK]` disks. — [XO 5.56 blog](https://xen-orchestra.com/blog/xen-orchestra-5-56/), [XO 5.78 blog](https://xen-orchestra.com/blog/xen-orchestra-5-78/), [forum: NOBAK for full backups](https://xcp-ng.org/forum/topic/3370/xo-backup-nobak-for-full-backups)
- **`[NOSNAP]`.** XO 5.96 added `[NOSNAP]` to exclude a disk from regular snapshots. — [XO 5.96 blog](https://xen-orchestra.com/blog/xen-orchestra-5-96/) (via search snippet)
- **XO's current `VM_snapshot` code:**
  - collects `[NOBAK]`-tagged VBDs and passes their VDIs as the 4th `VM.snapshot` argument;
  - on `MESSAGE_PARAMETER_COUNT_MISMATCH` (old XAPI), it snapshots everything and then destroys the ignored snapshot VDIs;
  - calls an optional in-guest HTTP hook `/sync` before and `/post-sync` after the snapshot (VM tag `xo:notify-on-snapshot`).

  — [@xen-orchestra/xapi/vm.mjs](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/xapi/vm.mjs)
- **XO backup defaults** (`DEFAULT_XAPI_VM_SETTINGS`): `snapshotConcurrency: 2`, `concurrency: 2`, `synchronizedSnapshot: false`, `diskPerVmConcurrency: 0` (unlimited), `cbtDestroySnapshotData: false`, `checkpointSnapshot: false`, `offlineSnapshot: false` and `bypassVdiChainsCheck: false`. — [VmsXapi.mjs](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/backups/_runners/VmsXapi.mjs)
- **XO snapshot flow:**
  - checks VDI chain health (`$assertHealthyVdiChains`) unless bypassed;
  - with `cbtDestroySnapshotData`, enables CBT on all disks (`VDI.enable_cbt`) right before the snapshot;
  - calls `$checkpoint` or `$snapshot`;
  - after export, if `snapshotRetention === 0 && preferNbd && cbtDestroySnapshotData`, it calls `VM.destroy` on the snapshot and then `VDI.data_destroy` per disk, logging a warning on `VDI_NO_CBT_METADATA`.

  — [_AbstractXapi.mjs](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/backups/_runners/_vmRunners/_AbstractXapi.mjs)
- **XO docs on synchronized snapshots.** These take all VMs' snapshots "up front, before the first export starts", in parallel up to snapshot concurrency. The feature "narrows the window between the restore points, it does not make them simultaneous". It costs extra space "especially on thick-provisioned SRs". The docs also say snapshot creation "uses a lot of I/O on your storage", with an order of magnitude of "seconds per VM", and that quiesced (VSS) snapshots were removed, so "Xen Orchestra now always takes normal snapshots". — [XO backup features and settings](https://docs.xen-orchestra.com/backups-and-dr/backup-features-and-settings)
- **XO user timings (2024, NFS SR).** Per-VM snapshot times from task logs were about 1.6–5.0 s (XO 2.9 s, Bitwarden 5.0 s, Docker 1.6 s, Media 1.6 s, Unifi 1.9 s). Disk counts were not stated. — [CBT feedback thread p.161](https://xcp-ng.org/forum/topic/9268/cbt-the-thread-to-centralize-your-feedback/161)
- **XO concurrency post.** It recommends keeping snapshot concurrency low ("2 is a good compromise") and notes one snapshot normally takes "only a few seconds". — [XO backup concurrency blog](https://xen-orchestra.com/blog/xen-orchestra-backup-concurrency/) (via search snippet)
- **`VM.checkpoint`** live-suspends a running VM (`Xapi_xenops.suspend`), snapshots disks plus the suspend VDI, then resumes it. — [xapi_vm_snapshot.ml](https://github.com/xapi-project/xen-api/blob/master/ocaml/xapi/xapi_vm_snapshot.ml)
- **XenServer's CBT guide** takes snapshots per disk with `VDI.snapshot(<vdi_ref>)` and then `VDI.data_destroy(<snapshot>)`, which "frees up space on the host or SR". It says nothing about multi-disk consistency or VM power state. `data_destroy` fails while the VDI snapshot is still attached to a VM snapshot by a VBD, so the VM snapshot must be destroyed first. — [XenServer CBT getting started](https://docs.xenserver.com/en-us/xenserver/developer/changed-block-tracking-guide/getting-started), [deleting snapshot](https://docs.xenserver.com/en-us/xenserver/developer/changed-block-tracking-guide/deleting-snapshot)
- **SM supports snapshotting attached VDIs:**
  - `LVMVDI._do_snapshot` pauses the attached tapdisk;
  - `_snapshot` handles hosts where the VDI is attached (`get_hosts_attached_on`);
  - with CBT it records a per-snapshot "consistency state" from `tap_status`.

  — [LVMSR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/LVMSR.py)
- **CBT adds work per snapshot.** On a CBT-enabled disk, each snapshot adds `_cbt_snapshot` (new CBT log LV plus cbt-util updates) and, when attached on a non-master, `_updateSlavesOnCBTClone`. `VDI.snapshot` uses `SNAPSHOT_DOUBLE` unless `driver_params:type=single|internal` is given. — [LVMSR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/LVMSR.py), [VDI.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/VDI.py)
- **CBT log size.** The bitmap granularity is `CBT_BLOCK_SIZE = 64 KiB`, so a CBT log is about virtual_size/64Ki bits. The minimum LV size is 4 MiB. — [constants.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/constants.py), [LVMSR.py `ensureCBTSpace`](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/LVMSR.py)
- **Space needed for a snapshot.** `_ensureSpaceAvailable` requires `lvSizeOrig + lvSizeClon + 2 journal LVs` minus what deflating the base frees. For a thick running VDI this is roughly the VDI's physical (allocated) size plus a few MiB. — [LVMSR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/LVMSR.py)

### Inferences
- **No cross-disk atomicity in `VM.snapshot`.** xapi snaps disks sequentially and SM pauses only the disk being snapped, so disks are captured seconds apart. Application consistency for multi-disk VMs therefore rests entirely on the in-guest VSS freeze spanning all disk snapshots. With roughly 8 s per disk on LVHD, more than one disk cannot fit Windows' 10 s VSS hold.
- **Tool-only levers, roughly in order of impact:**
  1. **Exclude disks** (`ignore_vdis`, a `[NOBAK]`-style tag) that hold pagefile, temp or scratch data. Each excluded disk saves the full ~5–9 s and its coalesce.
  2. **Per-disk `VDI.snapshot` in parallel, one call per SR,** inside the VSS freeze. This needs the VM's disks spread over different SRs, which is a pool change. Same-SR calls still serialise on the SM SR lock.
     - Wall time becomes max(per-SR serial time) instead of the sum.
     - The tool must then capture VM metadata itself (VM record and VBD layout), because no VM snapshot object is created.
     - XO's 2021 statement that the `VDI.snapshot` route worked "only for halted VM, because of XAPI limitations" conflicts with the SM code paths for attached VDIs and with XenServer's CBT guide. Test `VDI.snapshot` on a running VM on 8.3 before relying on it.
  3. **Run the VM on the pool master** for the snapshot (see section 1), and/or schedule multi-disk VMs while the SR's GC is idle. GC operations take the same SR lock (`cleanup.py` uses `scanLocked` and SR lock retries).
  4. **Per-SR scheduling instead of a global concurrency number.** Run at most one snapshot per SR at a time, and as many SRs in parallel as exist. Raising same-SR concurrency only adds queueing and can make the VSS window miss.
  5. **Pass `driver_params:type=single`** to `VDI.snapshot` where possible. SM then skips the second LV (`lvSizeClon = 0`). However, XAPI/XO expect a snapshot VDI to exist, and whether XAPI exposes `driver_params` for `VDI.snapshot` with type `single` for this use is unverified. Treat this as a research item, not a recommendation.
- **CBT on/off.** CBT adds roughly one LV create plus a slave refresh per disk per snapshot, a small fraction of the 5–9 s, but it is what makes `data_destroy` and NBD changed-block reads possible. Keeping CBT on is right. Remove stale `cbt_metadata` VDIs so the LV count does not grow (XO lists them "unconditionally to remove older one").
- **Keeping one snapshot vs `data_destroy`:**
  - `data_destroy` right after export makes every backup end in a leaf-coalesce of the running disk. This is the problematic, pause-based coalesce on VHD (section 3).
  - Keeping the previous snapshot until the next backup has taken its new one turns the merge into a non-leaf coalesce, which needs no long VM pause.
  - The cost of keeping one is permanent extra space equal to the base copy's allocated size per disk on thick LVM. At 1–4 % free on a 4 TiB SR, this is only feasible for small disks.
- **`VM.checkpoint` is not a lever for speed.** It suspends the VM (RAM written to disk) and is slower and more disruptive.

### Gaps
- No public, current measurement of XO snapshot time on `lvmoiscsi` per disk count was found. The 1.6–5 s/VM figures are from NFS in 2024.
- Whether XAPI 8.3 permits `VDI.snapshot` on an attached VDI of a running VM without restrictions was not verified in xapi code (`Xapi_vdi` allowed-operations). This needs a lab test.
- Whether `driver_params` with `type=single` is honoured through `VDI.snapshot` from the public API for this purpose was not verified.
- Whether XO exposes "one snapshot per SR" scheduling was not found. Its concurrency settings are per VM, not per SR.

## 3. Coalesce: duration, triggers, keeping chains short, known problems and how to avoid them

### Takeaway
On VHD/LVHD, deleting or `data_destroy`ing the backup snapshot leaves base copy ← running leaf. The GC must then leaf-coalesce, either by pausing the VM (only if the merge is estimated under 5 s or 20 MiB) or by repeatedly snapshotting and merging the leaf. On busy VMs this loops or fails ("Timed out", "Unexpected bump in size") and needs free space on thick SRs.

Levers:
- Keep a non-leaf coalesce shape: roll snapshots, or set `leaf-coalesce=false` on the leaf.
- Ensure free space.
- Back up during low write load.
- Move disks to QCOW2: since August 2026, QCOW2 has live leaf coalesce without pause or temporary snapshot.

### Cited Findings
- **Docs on triggers and duration.** Coalesce runs "as soon as a snapshot is removed" and "will take some time to finish", especially with a running VM or heavy writes. If snapshots are created faster than coalesce merges, the chain grows; the docs cite a maximum chain length of 30. XO skips backups for VMs with uncoalesced disks. — [docs.xcp-ng.org/storage](https://docs.xcp-ng.org/storage/)
- **XO chain-health check.** XO refuses to snapshot a VM with an "unhealthy VDI chain", meaning more uncoalesced parents than `maxUncoalescedVdis` allows, unless `bypassVdiChainsCheck` is set. — [@xen-orchestra/xapi/vm.mjs](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/xapi/vm.mjs), [VmsXapi.mjs](https://github.com/vatesfr/xen-orchestra/blob/master/@xen-orchestra/backups/_runners/VmsXapi.mjs)
- **VHD leaf-coalesce algorithm (current code):**
  - `canLiveCoalesce` is true if `allocated_size / speed < 0.5 × 10 s`, or `< 20 MiB` without speed history, or if `leaf-coalesce=force`.
  - Otherwise `_snapshotCoalesce` takes a single snapshot of the leaf and coalesces the temporary parent, up to 20 iterations or 3 without progress, then raises "VDI … could not be coalesced".
  - Storage speed is logged per SR in `/var/run/<uuid>.speed_log`.

  — [cleanup.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/cleanup.py)
- **Citrix KB on the mechanism.** Leaf coalesce works by single-snapshotting the VM until the delta is under 20 MB. Inline (non-leaf) coalesce is safe and does not need tapdisk to take part in copying blocks. The default tapdisk pause threshold is 10 s. — [CTX201296](https://support.citrix.com/external/article/CTX201296/understanding-garbage-collection-and-coa.html) (via search snippet and the [xcp-ng/xcp#323](https://github.com/xcp-ng/xcp/issues/323) quote)
- **GC improvements from XenServer, imported into XCP-ng sm:**
  - CP-309718 (2025-09-11) adds "a moving average of leaf size in GC", so a VM writing during coalesce does not abort the leaf-reduction loop.
  - CA-416486 (2025-10-06) makes "leaf coalesce wait for GC".

  — [commit 26eb921](https://github.com/xcp-ng/sm/commit/26eb921), [commit 13fc309](https://github.com/xcp-ng/sm/commit/13fc309)
- **Tuning `LIVE_LEAF_COALESCE_*` (anecdotal).** Users edit these constants in `/opt/xensource/sm/cleanup.py`; one example is 1 GiB / 300 s. Edits do not survive updates. A Vates CEO explanation: "you are writing faster in the VM than the garbage collector can merge/coalesce" (July 2024). — [CBT thread p.161](https://xcp-ng.org/forum/topic/9268/cbt-the-thread-to-centralize-your-feedback/161)
- **Vates position on raising defaults (2020).** Vates (Wescoeur) opposed raising them globally: "I don't think it's a good thing to pause tapdisk and to copy 1GB of delta globally … Is it nice to freeze the VM during many seconds?". New coalesce logic shipped in 8.1 (April 2020). The issue is still open. — [xcp-ng/xcp#323](https://github.com/xcp-ng/xcp/issues/323)
- **"Unexpected bump in size".** Leaf-coalesce fails with this when the leaf grows faster than it merges, even with 16 GiB / 400 s settings (8.2.0). Advice: coalesce offline or at low load. — [forum: leaf-coalesce EXCEPTION Unexpected bump in size](https://xcp-ng.org/forum/topic/7634/leaf-coalesce-exception-unexpected-bump-in-size)
- **Reported coalesce durations (anecdotal, 2024):**
  - with CBT, snapshots removed "in seconds" vs 10–15 min before CBT (rtjdamen);
  - contradicted by flakpyro: with CBT plus purge, coalesce ranged from seconds to 2–3 h on large VMs (a SharePoint server), with looping timeouts, vs about 10–15 min without purge, on NFS.

  — [forum: Switching to XCP-ng p.38](https://xcp-ng.org/forum/topic/9093/switching-to-xcp-ng-want-to-hear-your-problems/38)
- **Thick LVM over iSCSI is the case users call painful** ("snapshots … a pain on a thick lvm iscsi sr"); coalesce on NFS is reported as faster. — [forum: Switching to XCP-ng p.20](https://xcp-ng.org/forum/topic/9093/switching-to-xcp-ng-want-to-hear-your-problems/20) (via search snippet)
- **QCOW2 live leaf coalesce (August 2026, 8.3):** "We can now coalesce disks without blocking access to the disk." It "prevents occasional failures caused by a large amount of writes to the disk during coalescing" and "Not requiring a temporary snapshot also limits the amount of data reserved during coalescing on block-based SRs". In code, `coalesceLeaf` uses `tap-ctl commit` inside the running tapdisk when `cowutil.isCoalesceableOnRemote()` is true, which is only for QCOW2; VHD returns `False`. — [August 2026 Updates #1](https://xcp-ng.org/blog/2026/08/18/august-2026-updates-1-for-xcp-ng-8-3-lts/), [cleanup.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/cleanup.py), [commit a3a8e6f](https://github.com/xcp-ng/sm/commit/a3a8e6f)
- **Follow-up QCOW2 fixes.** October 2026 fixed QCOW2 coalescing where "a failed or interrupted coalesce could leave the disk chain or the SR in a bad state". Related commits include #162 (leaf not paused for undo), #163 (interrupted QCOW2 leaf-coalesce) and #167 (abort in online coalesce). — [October 2026 Updates #1](https://xcp-ng.org/blog/2026/10/05/october-2026-updates-1-for-xcp-ng-8-3-lts/), [sm PR #162](https://github.com/xcp-ng/sm/pull/162)
- **QCOW2 GA known issue (May 2026).** On LVM SRs, deleting an older snapshot while the VM runs could crash tapdisk; "Only the last two snapshots can be safely deleted while the VM is on". A fix was announced. — [QCOW2 GA blog](https://xcp-ng.org/blog/2026/05/05/qcow2-is-now-ga-in-xcp-ng/)

### Inferences
- **Avoid leaf-coalesce entirely on VHD with a non-leaf shape.** Either keep the previous backup snapshot until the next one is taken (rolling, tool change), or set `xe vdi-param-set uuid=<leaf> other-config:leaf-coalesce=false` on the VM's disks (pool change, per VDI). Then:
  - The chain stays at base ← leaf, and each new backup's base is merged by non-leaf coalesce. That needs no long VM pause and no temporary snapshot.
  - The cost is permanent thick space equal to the base's allocated size per disk, plus one extra VHD level.
  - Verify in a lab that the GC then coalesces the intermediate base into the old base as expected.
- **The 1–4 % free state is itself a coalesce risk.** Both snapshot creation (`_ensureSpaceAvailable`) and VHD snapshot-coalesce iterations need free space of about the leaf's allocated delta on thick SRs. Either fails with no space (`gc_no_space`). Keep headroom of at least the largest VM's allocated size per SR, or move to QCOW2 so leaf coalesce needs no temporary snapshot.
- **`SR other-config:gc=false` during the backup window** stops coalesce from competing with backup reads and SR-lock-holding snapshot calls. The tool must re-enable it afterwards (`xe sr-param-remove … param-key=gc`) and handle crash cases. This is a pool change done by the tool. The risk is chain growth if re-enable fails.
- **Leave `LIVE_LEAF_COALESCE_*` alone.** Raising it trades failed coalesces for longer VM I/O freezes, of the same kind the backup is trying to avoid.

### Gaps
- No measured coalesce durations specific to `lvmoiscsi` on 8.3 with current sm (post CP-309718) were found.
- No source confirms whether VHD will get live (tapdisk) leaf coalesce. The August 2026 release covers QCOW2 only.

## 4. Alternative SR types on the same array and their snapshot behaviour; what is production-ready in 8.3

### Takeaway
On 8.3 the production options are thick LVM (`lvmoiscsi`/HBA, VHD or QCOW2), NFS/EXT (thin, file-based) and XOSTOR (thin, DRBD; local disks only).
- **QCOW2 on `lvmoiscsi`.** It is GA since May 2026 and selectable per SR or VDI. It does not remove LVM work from snapshot creation, but it fixes the leaf-coalesce pain (live coalesce, less reserved space).
- **NFS.** File-based SRs avoid all LVM metadata operations at snapshot time and are thin, so a snapshot is "almost free" in space.
- **SMAPIv3 `zfs-vol`** is still a local-only preview.
- **largeblock** is a local 4Kn workaround.
- **GFS2** is not in XCP-ng.

### Cited Findings
- **QCOW2 GA.** QCOW2 has been GA in XCP-ng 8.3 since 2026-05-05 on SMAPIv1, for disks up to 16,381 GiB. It works on "almost all existing SR types (except `linstor` and `smb`)", including LVM, LVMoISCSI and LVMoHBA. VHD remains the default (max 2040 GiB). — [QCOW2 FAQ](https://docs.xcp-ng.org/storage/qcow2_faq/), [QCOW2 GA blog](https://xcp-ng.org/blog/2026/05/05/qcow2-is-now-ga-in-xcp-ng/)
- **Choosing QCOW2:**
  - SR-level `device-config:preferred-image-formats`, one of `vhd,qcow2` (default), `qcow2,vhd`, `qcow2` or `vhd`;
  - changing an existing SR requires unplug, destroy, recreate and re-plug of the PBD, with VMs stopped or moved;
  - per disk via `xe vdi-create … sm-config:image-format=qcow2`;
  - existing VHDs are converted by storage-migrating them to a QCOW2-preferring SR. Since August 2026, `xe vdi-pool-migrate`/`vm-migrate` also accept `image-format:<VDI-UUID>=qcow2`.

  — [QCOW2 FAQ](https://docs.xcp-ng.org/storage/qcow2_faq/), [August 2026 Updates #1](https://xcp-ng.org/blog/2026/08/18/august-2026-updates-1-for-xcp-ng-8-3-lts/)
- **Thin vs thick snapshot space (QCOW2 FAQ).** On thin SRs the overhead "is almost free, just a bit of data for the metadata of a few new VDI". On thick SRs you need space for base copy, snapshot and active disk. — [QCOW2 FAQ](https://docs.xcp-ng.org/storage/qcow2_faq/)
- **QCOW2 maturity and backups.**
  - It has about 1.5 years of hands-on experience vs 8+ for VHD, and Vates recommends a gradual rollout.
  - Performance is "comparable to VHD".
  - October 2026 enlarged tapdisk's QCOW2 metadata cache and fixed tapdisk crashes.
  - For QCOW2, XO incremental backups need NBD; "without it, each run falls back to a full backup".

  — [QCOW2 GA blog](https://xcp-ng.org/blog/2026/05/05/qcow2-is-now-ga-in-xcp-ng/), [October 2026 Updates #1](https://xcp-ng.org/blog/2026/10/05/october-2026-updates-1-for-xcp-ng-8-3-lts/), [XO backup features](https://docs.xen-orchestra.com/backups-and-dr/backup-features-and-settings)
- **QCOW2 in the LVM snapshot code.** It uses the same `LVMVDI._snapshot` sequence. The only difference is that the base copy is not set read-only ("For QCOW2, the whole chain need to be writable for tapdisk to do the coalesce"). — [LVMSR.py](https://github.com/xcp-ng/sm/blob/3.2.12-8.3-devel/drivers/LVMSR.py)
- **Docs on block SR types.** iSCSI/HBA are "Shared, thick-provisioned storage", and "Cost of thick provisioning is relatively high when you do snapshots (used for backup)". The docs recommend thin options such as NFS/EXT. — [docs.xcp-ng.org/storage](https://docs.xcp-ng.org/storage/)
- **Thin on iSCSI.** Forum consensus is that XCP-ng does not offer thin `lvmoiscsi`; array-side thin LUNs still look thick to XCP-ng. A forum statement that thin iSCSI is "planned for XO 6" is unverified. — [forum: thin provisioning on iSCSI](https://xcp-ng.org/forum/topic/7540/any-plans-to-support-thin-provisioning-on-iscsi-storage-repository), [Lawrence Systems forum](https://forums.lawrencesystems.com/t/xcp-ng-iscsi-thin-provisioning/9420)
- **NFS vs iSCSI.** Vates (Olivier Lambert) reported "the same perf level between both NFS and iSCSI" in their benchmarks. Users report faster coalesce on NFS, but also NFS 4/4.1 lease timeouts under load (NFS 3 "rock solid"). — [forum: Switching to XCP-ng p.38](https://xcp-ng.org/forum/topic/9093/switching-to-xcp-ng-want-to-hear-your-problems/38), [forum: Switching to XCP-ng](https://xcp-ng.org/forum/topic/9093/switching-to-xcp-ng-want-to-hear-your-problems/20) (via search snippet)
- **XOSTOR.** It is LINSTOR/DRBD hyperconverged from host-local disks, shared and thin, with no QCOW2 or large disks until a SMAPIv3 port.
  - Forum reports describe SR-scan cost with about 350 disks (advice: raise `auto-scan-interval` to 300 s).
  - Users moved off XOSTOR after snapshot and large-VDI problems.
  - A Vates engineer said the coalesce algorithm "should be changed to no longer depend on the write speed to the SR", which matters even more for LINSTOR.

  — [docs.xcp-ng.org/storage](https://docs.xcp-ng.org/storage/), [forum: SR.Scan performance within XOSTOR](https://xcp-ng.org/forum/topic/11638/sr.scan-performance-withing-xostor), [forum: XOSTOR preview p.320](https://xcp-ng.org/forum/topic/5361/xostor-hyperconvergence-preview/320) (via search snippets)
- **SMAPIv3 `zfs-vol`.** Its April 2024 preview was "still experimental and not meant for your production VMs yet!", and no 2026 production declaration was found. The largeblock SR is a SMAPIv1 local driver: a loop device with 512 B sectors on a 4 KiB device plus EXT, "a stopgap until SMAPIv3 is ready". — [First SMAPIv3 driver preview](https://xcp-ng.org/blog/2024/04/19/first-smapiv3-driver-is-available-in-preview/), [LargeBlockSR forum](https://xcp-ng.org/forum/topic/8901/largeblocksr-for-4kib-blocksize-disks)
- **sm driver list.** The 8.3 devel sm tree contains these drivers: LVM, LVMoISCSI, LVMoHBA, LVMoFCoE, EXT, NFS, SMB, CephFS, GlusterFS, MooseFS, XFS, ZFS, LargeBlock, Linstor and RawISCSI. There is no GFS2 driver. — [xcp-ng/sm drivers/](https://github.com/xcp-ng/sm/tree/3.2.12-8.3-devel/drivers)

### Inferences
- **On the same HPE Alletra B10000 (block-only), the realistic choices are `lvmoiscsi` VHD vs `lvmoiscsi` QCOW2.**
  - QCOW2 will not make `VM.snapshot` itself much faster: the same LVM operations run, minus one `lvchange -p r`.
  - It does make post-backup coalesce non-disruptive and less space-hungry. That is the main operational pain at 1–4 % free.
  - Moving VMs to QCOW2 requires a storage migration with space for a full copy, plus NBD for incremental reads.
  - Verify that CBT, `data_destroy` and NBD changed-block reads work for QCOW2 on LVM in the backup tool.
- **NFS needs a separate file server/NAS.** The B10000 is a block array; NAS capabilities were not researched. NFS would remove LVM metadata operations from the snapshot path and give thin snapshots. Evidence: the code path difference, and 1.6–5 s/VM reported on NFS. It adds a NAS and NFS failure modes.
- **XOSTOR is not applicable to a SAN-centric pool and has its own snapshot/coalesce issues.**
- **`allocation=thin` on LVMSR exists in code** but is undocumented and unsupported for XCP-ng. Attached VDIs are inflated to full size at snapshot anyway, so it would not help running VMs much.

### Gaps
- No public 8.3 benchmark compares snapshot creation time for `lvmoiscsi` VHD vs `lvmoiscsi` QCOW2 vs NFS.
- CBT/`data_destroy` support and behaviour for QCOW2 VDIs on LVM SRs is not documented in the QCOW2 FAQ; only code hints exist.
- GFS2 availability in XCP-ng was not confirmed from a primary XCP-ng statement. It is inferred from the absence of a driver in sm and the docs table.

## 5. Vates roadmap 2025–2026: SMAPIv3, new storage stack, QCOW2, snapshot performance, CBT, array offload

### Takeaway
Shipped in 2025–2026:
- QCOW2 GA (May 2026);
- QCOW2 live leaf coalesce (August 2026);
- QCOW2 metadata cache, faster storage migration, and iSCSI/LVM concurrency and CBT fixes (October 2026);
- XenServer GC improvements imported into sm (2025).

On the public roadmap (updated 2026-08-05):
- NOW: "Live Coalesce" and "Performance improvement for XCP-ng 8.3 (tapdisk)";
- NEXT: "SMAPIv3 Design" and "Trim/discard support QCOW2";
- LATER: "XOSTORv2", "SMAPIv3 - Ceph Driver", "PureStorage integration support" and "NetApp integration support".

Nothing on the roadmap names LVM snapshot-creation speed, thin block storage, CBT improvements or HPE array offload.

### Cited Findings
- **Roadmap (last updated 2026-08-05), Storage:**
  - NOW: "Live Coalesce", "Live VDI Resize", "XOSTOR External witness", "XOSTORv1 robustness", "Performance improvement for XCP-ng 8.3 (tapdisk)", "XCP-ng 9 modern platform development";
  - NEXT: "SMAPIv3 Design (Provide performance Enhancement, Enable advanced features)", "XCP-ng encryption (Discovery)", "Trim/discard support QCOW2";
  - LATER: "XOSTORv2", "PureStorage integration support", "NetApp integration support", "SMAPIv3 - Ceph Driver";
  - XAPI NEXT: "SMAPIv3 Migration Support (Migration when snapshots)";
  - XO LATER: "Purestorage/Netapp integration", "Instant Restore", "Backup Deduplication".

  Thin provisioning on block storage, CBT, NBD, snapshot performance and SAN snapshot offload are not named. The page says items are intentions, not commitments. — [Vates product roadmap](https://docs.vates.tech/product-roadmap/)
- **QCOW2 GA promises.** "storage performance optimizations … in the coming months", decoupling maximum size from SR type, snapshot-free leaf coalescing (delivered August 2026), and that `linstor` needs a SMAPIv3 port for large disks. — [QCOW2 GA blog](https://xcp-ng.org/blog/2026/05/05/qcow2-is-now-ga-in-xcp-ng/), [August 2026 Updates #1](https://xcp-ng.org/blog/2026/08/18/august-2026-updates-1-for-xcp-ng-8-3-lts/)
- **October 2026 #1 storage items:**
  - faster live storage migration ("2 to 2.5 times faster in some production-like tests");
  - bigger QCOW2 metadata cache in tapdisk;
  - QCOW2 coalesce robustness;
  - CBT fixes on NFS and on shared LVM;
  - iSCSI concurrent-operation reliability;
  - Windows Guest Tools 9.2.385, fixing "a data corruption issue that could occur during in-memory snapshots and backups".

  — [October 2026 Updates #1](https://xcp-ng.org/blog/2026/10/05/october-2026-updates-1-for-xcp-ng-8-3-lts/)
- **August 2026.** QCOW2 SR scans on file-based SRs got faster ("from 3 min 20 s down to 20 s" with very many VDIs). — [August 2026 Updates #1](https://xcp-ng.org/blog/2026/08/18/august-2026-updates-1-for-xcp-ng-8-3-lts/)
- **SMAPIv1 vs SMAPIv3 in the docs.** SMAPIv1 is described as "historical" with a code base that "has become hard to maintain". SMAPIv3 is "far more flexible and storage agnostic" and the team is "working on" it; no production status is given. — [docs.xcp-ng.org/storage](https://docs.xcp-ng.org/storage/)
- **8.3 support window.** 8.3 is LTS and supported until 2028-11-30. — [XCP-ng 8.3 release notes](https://docs.xcp-ng.org/releases/release-8-3/) (via search snippet)

### Inferences
- **What would remove the problem, on the evidence:**
  - a SMAPIv3 block driver with cheap snapshots, which is not yet designed for iSCSI/LVM; "SMAPIv3 Design" is only NEXT;
  - array-integrated snapshots, which are only announced as LATER and only for Pure/NetApp; nothing for HPE;
  - live coalesce, already delivered for QCOW2.

  For an Alletra-based pool in 2026–2027, the practical path is QCOW2 on `lvmoiscsi` plus the tool and pool levers in sections 1–3. There is no near-term roadmap item that makes LVHD `VM.snapshot` creation itself fast.

### Gaps
- No public Vates statement was found on improving LVM-based snapshot creation latency, thin LVM on shared block storage, or CBT redesign.
- No details are public on what the "PureStorage/NetApp integration" items will do (array snapshots for backup or not), or on their timeline.
