# Why XAPI `VM.snapshot` is slow on XCP-ng 8.3 shared LVM-over-iSCSI (`lvmoiscsi`, SMAPIv1 LVHD/LVMSR, VHD)

Scope note on code versions (read this first):
- The XCP-ng 8.3 `sm` RPM is `sm-3.2.12-25.2` (changelog 2026-10-16). Its patch list includes patches such as `0184-fix-LVMSR-correct-CBT-log-deletion-on-slave-164.patch` and QCOW2 fixes. So the shipped LVM driver is the newer `LVMSR.py` (the LVHD driver renamed when QCOW2 support was added), not the old `LVHDSR.py` — [xcp-ng-rpms/sm 8.3 SPECS/sm.spec](https://github.com/xcp-ng-rpms/sm/blob/88f62aea20b0cfb9c4f66d98c25aa98c37a3884d/SPECS/sm.spec).
- Line references below are to the xcp-ng/sm development branch `3.2.12-8.3-devel` at commit [538c3e8b](https://github.com/xcp-ng/sm/tree/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d) (2026-11-09). For VHD VDIs its snapshot path is functionally the same as the older `LVHDSR.py` on branch `3.2.12-8.3` at commit [d8ca026a](https://github.com/xcp-ng/sm/tree/d8ca026a32674e1d76a488c92c6dbb96d3775075). Only two LVMSR commits from November 2026 (described below) are newer than the shipped RPM.
- Abbreviations used in links: SM = `https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d`; XAPI = `https://github.com/xapi-project/xen-api/blob/61de57919e12c9f429acd72d4b1ee79dc203c180`.

## Q1: What exactly happens per VDI in `VDI.snapshot` in the LVHD/LVMSR driver, and where can the seconds go?

### Takeaway
Each VDI snapshot is a separate SM driver process on the pool master. That process runs dozens of external commands one after another: about 25 LVM commands without CBT, and roughly 50–60 or more with CBT enabled and the VM on a non-master host. It also runs about 8–10 `vhd-util` calls, up to about 10 `cbt-util` calls, and 4–5 cross-host XAPI plugin calls (tap-pause, on-slave deactivate, on-slave refresh/activate, on-slave CBT refresh, tap-unpause). All of this happens while the VM's disk is paused and while an SR-wide lock is held. No step copies data in proportion to disk size, so the cost is a large fixed per-VDI control-plane overhead. That matches the measurement that a 12 MiB disk costs as much as a 60 GiB one.

### Cited Findings

**Before the driver is called (xapi):**
- `VM.snapshot` → `Xapi_vm_snapshot.snapshot` → `Xapi_vm_clone.clone Disk_op_snapshot ~ignore_vdis` (logs `"Snapshot: begin"` / `"Snapshot: end"` to xensource.log) — [XAPI ocaml/xapi/xapi_vm_snapshot.ml#L29-L37](https://github.com/xapi-project/xen-api/blob/61de57919e12c9f429acd72d4b1ee79dc203c180/ocaml/xapi/xapi_vm_snapshot.ml#L29-L37)
- For every non-empty, non-CD VBD, xapi calls `Client.Async.VDI.snapshot` and then waits for that task (`wait_for_clone`) before moving on. The loop is a `List.fold_left` over the VBDs (`safe_clone_disks`) — [XAPI ocaml/xapi/xapi_vm_clone.ml#L143-L223](https://github.com/xapi-project/xen-api/blob/61de57919e12c9f429acd72d4b1ee79dc203c180/ocaml/xapi/xapi_vm_clone.ml#L143-L223)
- In xapi's SMAPIv1 layer, `VDI.snapshot` first runs the SM `vdi_snapshot` call and then updates the DB (name, snapshot_time, is_a_snapshot, content_id, sm-config). It then makes a second SM call, `Sm.vdi_update`, on the new VDI — [XAPI ocaml/xapi/storage_smapiv1.ml#L670-L733](https://github.com/xapi-project/xen-api/blob/61de57919e12c9f429acd72d4b1ee79dc203c180/ocaml/xapi/storage_smapiv1.ml#L670-L733) (vdi_update at L719).
- xapi itself only takes a per-VDI lock (`with_vdi` → `Storage_locks.with_instance_lock`) around VDI operations. There is no xapi-level SR lock for `VDI.snapshot` — [XAPI ocaml/xapi/storage_smapiv1_wrapper.ml#L385-L416](https://github.com/xapi-project/xen-api/blob/61de57919e12c9f429acd72d4b1ee79dc203c180/ocaml/xapi/storage_smapiv1_wrapper.ml#L385-L416), L795.

**SM process start and SR load (on the master, before the snapshot itself):**
- `SRCommand.run` builds the driver object, which runs `load()`, before it runs the command — [SM drivers/SRCommand.py#L384-L392](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/SRCommand.py#L384-L392)
- For VDI commands, `LVHDoISCSISR.load` skips iSCSI session setup and only reads `SCSIid`. iSCSI login and discovery therefore cost nothing per snapshot — [SM 3.2.12-8.3 drivers/LVHDoISCSISR.py#L91-L102](https://github.com/xcp-ng/sm/blob/d8ca026a32674e1d76a488c92c6dbb96d3775075/drivers/LVHDoISCSISR.py#L91-L102)
- On the master, `SR.__init__` sets `LVM_SYSTEM_DIR=/etc/lvm/master` and `LVM_DEVICE=/dev/disk/by-scsid/<SCSIid>`, so LVM only scans the SR's PV — [SM drivers/SR.py#L130-L139](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/SR.py#L130-L139)
- `LVMSR.load` creates an `LVMCache` and runs `lvutil._checkVG` (`vgs --readonly`). On the master, for every command except attach/detach/activate/deactivate, it runs `_undoAllJournals()`, which acquires the SR lock and reads all journals (the first cache access triggers a full `lvs` of the VG). It then runs `_checkMetadataVolume()` — [SM drivers/LVMSR.py#L158-L222](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L158-L222), [L1175-L1185](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1175-L1185)
- `LVMCache` is lazy. The first use triggers `refresh()`, which runs `lvs --noheadings --units b -o +lv_tags /dev/VG_XenStorage-<sr>`, a scan of every LV in the VG — [SM drivers/lvmcache.py#L45-L105](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvmcache.py#L45-L105)

**SR lock for the whole command:**
- `OPS_EXCLUSIVE` includes `vdi_snapshot` and `vdi_clone` — [SM drivers/LVMSR.py#L82-L86](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L82-L86)
- `SRCommand._run_locked` takes `sr.lock` for any command in `ops_exclusive` and holds it until the command returns. That includes pause → snapshot → unpause — [SM drivers/SRCommand.py#L139-L170](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/SRCommand.py#L139-L170)
- The SR lock is a blocking `fcntl(F_SETLKW)` lock on a file under `/var/lock/sm/<sr-uuid>/` on the host that runs the command — [SM drivers/lock.py#L41](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lock.py#L41), [drivers/flock.py#L95-L98](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/flock.py#L95-L98)
- `_snapshot` refuses to run unless `self.sr.isMaster` is true, so every LVM snapshot runs on the pool master — [SM drivers/LVMSR.py#L1747-L1752](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1747-L1752)

**`VDI.snapshot` → `_do_snapshot` (pause window):**
- `VDI.snapshot()` chooses `SNAPSHOT_DOUBLE` unless driver_params say otherwise. It checks CBT with `_get_blocktracking_status()`, which makes a XAPI `sr_get_capability` call and runs `lvutil.exists(<uuid>.cbtlog)`, i.e. an `lvs` — [SM drivers/VDI.py#L348-L380](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/VDI.py#L348-L380), [L795-L806](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/VDI.py#L795-L806), [LVMSR.py#L2407-L2408](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L2407-L2408)
- `_do_snapshot` first records CBT consistency with `tap_status`. It then calls `blktap2.VDI.tap_pause`, runs `_snapshot()`, calls `tap_unpause`, and logs `WARNING: snapshot paused VM for N seconds` if the pause lasted more than `LONG_SNAPTIME = 60` s — [SM drivers/LVMSR.py#L88](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L88), [L1711-L1745](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1711-L1745); the warning was added in [commit ec5662b1 "CA-350871: Log if LVHD snapshot pauses VM for more than 60secs"](https://github.com/xcp-ng/sm/commit/ec5662b143537028ceccf778b330de9a63a97f6c) (2021).
- `tap_pause` sets `sm-config:paused=true` through XAPI. For every `host_<ref>` key in the VDI's sm-config, it calls `host.call_plugin(<host>, "tapdisk-pause", "pause")`. `tap_unpause` does the same with "unpause" and then removes `paused` — [SM drivers/blktap2.py#L1513-L1600](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/blktap2.py#L1513-L1600)
- On the host running the VM, the plugin's `Pause` takes the per-VDI lock and calls `tapdisk.pause()` (tap-ctl pause), which waits until in-flight I/O drains. `Unpause` calls `_pathRefresh()` and then instantiates the full SR driver via `VDI.VDI.from_uuid` → `SR.from_uuid` (a "nop" SR load: `vgs` plus a full `lvs` through `_checkMetadataVolume`). If CBT is on, it runs `_get_blocktracking_status()` (`lvs`) and `_activate_cbt_log()` (full `lvs` refresh plus `lvchange -ay`) before `tapdisk.unpause(...)` — [SM drivers/tapdisk_pause.py#L203-L260](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/tapdisk_pause.py#L203-L260), [drivers/VDI.py#L103-L115](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/VDI.py#L103-L115), [drivers/SR.py from_uuid](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/SR.py)

**`_snapshot()` body, in order** ([SM drivers/LVMSR.py#L1747-L1906](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1747-L1906)):
1. `_loadThis()`: LV info from the cache. It activates the leaf LV on the master with `lvmCache.activate` → `activateNoRefcount(lv, refresh=True)`, which runs `lvchange -ay`, `lvchange --refresh` and `dmsetup table`. It then runs `vhd-util query` (VHD info) — [LVMSR.py#L2173-L2196](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L2173-L2196), [lvmcache.py#L143-L156](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvmcache.py#L143-L156), [lvutil.py#L678-L698](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L678-L698). The `--refresh` on activation was reinforced in [commit 788ceb6a "CA-413899: Rescan LVs whilst activating"](https://github.com/xcp-ng/sm/commit/788ceb6a70c2a5812438ed21fb3953c1a3b555a5) (2025).
2. `_chainSetActive(True, False)` "activates the entire VHD chain". `getParentChain` runs `vhd-util scan -f -m <lv> -l <VG> -a`. `vhd-util scan -l` itself runs `vgs` and `lvs` for the VG — [LVMSR.py#L1770](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1770), [L2198-L2220](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L2198-L2220), [vhdutil.py#L154-L175](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/vhdutil.py#L154-L175), [blktap lvm/lvm-util.c#L123 (vgs), #L235 (lvs)](https://github.com/xapi-project/blktap/blob/45f6e8e0e056a8f6aa516e24918eb1606902c45a/lvm/lvm-util.c#L123-L240), [blktap vhd/lib/vhd-util-scan.c#L1273](https://github.com/xapi-project/blktap/blob/45f6e8e0e056a8f6aa516e24918eb1606902c45a/vhd/lib/vhd-util-scan.c#L1273). It then activates each parent LV (again `lvchange -ay` + `--refresh` + `dmsetup` when it is not already active).
3. `getDepth` (`vhd-util query -d`). The chain is limited to max depth 30 — [LVMSR.py#L1776-L1780](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1776-L1780)
4. Size planning. If the VDI is attached (`get_hosts_attached_on`, which reads the `host_*` sm-config keys), the new leaf gets `fullpr` = full virtual size + VHD overhead, because "thick" is the default for LVM SRs (`PROVISIONING_DEFAULT = "thick"`). The space check (`_ensureSpaceAvailable` → `vgs`) needs room for the new leaf + snapshot LV + 2 journal LVs — [LVMSR.py#L94](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L94), [L1786-L1816](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1786-L1816), [L918-L923](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L918-L923), [util.py#L897-L908](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/util.py#L897-L908)
5. If the VDI is attached on another host: `_deactivateOnSlave`, i.e. `host.call_plugin(<host>, "on-slave", "multi", {deactivateNoRefcount <leaf LV>})`. `call_on_slave` skips the master itself — [LVMSR.py#L1819](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1819), [L1259-L1316](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1259-L1316)
6. Clone journal: `journaler.create("clone", orig, "<base>_<clone>")` creates a 4 MiB LV with `lvcreate`. Because the name is too long, it writes the value into the LV, closes it and deactivates it (`lvchange -an`) — [journaler.py#L32-L92](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/journaler.py#L32-L92), [LVMSR.py#L1827](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1827)
7. The original leaf LV is renamed to become the "base copy": `lvrename VHD-<orig> VHD-<base>` — [LVMSR.py#L1834](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1834), [lvutil.py#L637-L639](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L637-L639)
8. Deflate the base copy to its physical VHD size: `vhd-util modify -s` (moves the footer) followed by `lvresize -L` (shrink) — [LVMSR.py#L1848](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1848), [lvmcowutil.py#L158-L170](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvmcowutil.py#L158-L170), [lvutil.py#L661-L668](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L661-L668)
9. `_createSnap` runs twice: once for the new leaf, which keeps the original UUID, and once for the snapshot VDI. Each run does `lvcreate -n VHD-<uuid> -L <MiB> VG -W n`, then `vhd-util snapshot -n <new> -p <base> -S <prealloc>`, then `vhd-util query -p` — [LVMSR.py#L1852-L1856](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1852-L1856), [L1908-L1940](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1908-L1940), [lvutil.py#L601-L611](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L601-L611). In the older LVHDSR code the `-S` value is `MSIZE_MB = 2 TiB` ("max virt size for fast resize") — [SM 3.2.12-8.3 lvhdutil.py#L29-L30](https://github.com/xcp-ng/sm/blob/d8ca026a32674e1d76a488c92c6dbb96d3775075/drivers/lvhdutil.py#L29-L30)
10. Mark the base hidden (`vhd-util set ... hidden`) and read-only (`lvchange -p r`, under a local `lvchange-p` lock) — [LVMSR.py#L1866-L1878](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1866-L1878), [lvmcache.py setReadonly](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvmcache.py)
11. If the VDI is attached on another host: `_updateSlavesOnClone`, one `on-slave multi` plugin call that does a "refresh" of the new leaf LV (`lvchange -ay` + `lvchange --refresh` + `dmsetup table`) and an "activate" of the base LV — [LVMSR.py#L1271-L1285](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1271-L1285), [on_slave.py#L31-L70](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/on_slave.py#L31-L70). The slave builds a new `LVMCache`, so its first cache access also runs a full `lvs` there.
12. CBT, only if enabled: `_cbt_snapshot`, then `_updateSlavesOnCBTClone` (another `on-slave` plugin call that deactivates and refreshes the cbtlog LV) — [LVMSR.py#L1885-L1897](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1885-L1897), [L1287-L1296](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1287-L1296). `_cbt_snapshot` runs, in order:
    - `lvrename` of `<vdi>.cbtlog` to `<snap>.cbtlog`
    - up to about 10 `_cbt_op(...)` calls (set consistency, get parent, set child on the parent log if present, create the new log, set consistency, get child, set parent, set child ×2)
    - `ensureCBTSpace` (`vgs`)
    - `lvcreate` of the new cbtlog LV, then `lvchange -an`

    Each `_cbt_op` takes a `cbtlog` file lock and calls `_activate_cbt_log`. That function always runs `self.sr.lvmCache.refresh()`, a full `lvs` of the VG, and runs `lvchange -ay` if the log LV is not active. Then come the `cbt-util` call and `lvchange -an` — [SM drivers/VDI.py#L747-L793](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/VDI.py#L747-L793), [L866-L880](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/VDI.py#L866-L880), [LVMSR.py#L2331-L2408](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L2331-L2408), [LVMSR.py#L1336-L1339](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1336-L1339)
13. Remove the clone journal (`lvremove -f`, plus `_lvmBugCleanup` dmsetup checks) — [LVMSR.py#L1904](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1904), [lvutil.py#L612-L635](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L612-L635)
14. `_finishSnapshot`:
    - `_updateStats`: XAPI read, `vgs`, and an SR `_db_update`
    - twice (once for the new snapshot VDI, once for the base copy): `LVMMetadataHandler.ensureSpaceIsAvailableForVdis` + `addVdi`, which read and write the SR's MGT metadata LV on the LUN; `_db_introduce` (XAPI)
    - `VDI.set_sm_config` and `set_physical_utilisation` on the original VDI

    — [LVMSR.py#L1942-L2071](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1942-L2071), [L864-L872](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L864-L872)
15. Back in `_do_snapshot`: `tap_unpause` (see above). After the command, `sr.cleanup()` → `lvActivator.deactivateAll()` deactivates the LVs that this process activated on the master (`lvchange -an` each, with a cache `refresh` if one is open) — [lvmanager.py#L88-L110](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvmanager.py#L88-L110), [lvmcache.py deactivate](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvmcache.py)
16. The follow-up `vdi_update` SM process (from xapi) loads the SR again (`vgs`, full `lvs`, a short SR-lock hold inside `_undoAllJournals`). It then updates the MGT metadata with the VDI's name/description/snapshot fields — [LVMSR.py#L2305-L2330](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L2305-L2330)

**Every LVM command is serialized host-wide:**
- `lvutil.cmd_lvm` wraps every LVM binary call in `with Fairlock("devicemapper")`. It logs `***** Long LVM call of '<cmd>' took <s>` if a call takes more than `MAX_OPERATION_DURATION = 15` s — [SM drivers/lvutil.py#L56](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L56), [L145-L195](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L145-L195)
- Fairlock is a systemd-socket-based FIFO lock. It was introduced because "None of the file-based locking systems available are 'fair'… This has been something of a problem across various attempts to wrap access to LVM, and indeed it turns out that multipath commands also need to be serialised on some of the same resources" — [commit a077ecda "CA-387861 Introduce fair locking subsystem"](https://github.com/xcp-ng/sm/commit/a077ecda407337e5c6d178a6d4ee60c0aea27509) (2024); [misc/fairlock/fairlock.py](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/misc/fairlock/fairlock.py)
- Earlier rework motivation: "prevent a storm of later LVM commands from usurping the lock that an earlier command is waiting for… In some pathalogical circumstances, this has been known to result in a commant taking many minutes to get the lock" — [commit 125aa9ec "CP-39600: Rework LVM locking to use fair lock queue"](https://github.com/xcp-ng/sm/commit/125aa9eca4900b4acfff232eafd62a3423ffd99b) (2023)
- In 8.2 the equivalent was an (unfair) file lock `LvmLockContext` named "lvm" — [SM 2.30.8-8.2 drivers/lvutil.py#L116-L134](https://github.com/xcp-ng/sm/blob/2.30.8-8.2/drivers/lvutil.py#L116-L134) *(8.2, older)*

**Why size does not matter:**
- Nothing in steps 1–16 reads or writes VDI data in proportion to virtual size:
  - `lvcreate` uses `-W n`
  - the deflate only rewrites the VHD footer and shrinks the LV
  - `vhd-util snapshot` writes a new VHD header/BAT
  - the CBT log is a small bitmap file

  — code references above ([lvutil.py#L601-L611](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L601-L611), [lvmcowutil.py#L158-L170](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvmcowutil.py#L158-L170))
- XCP-ng docs: "on thick-provisioned SRs like LVM/iSCSI/HBA, a snapshot can reserve up to the full disk size" — that is space, not time — [docs.xcp-ng.org Snapshots](https://docs.xcp-ng.org/vms/snapshots/)
- xapi project docs: "The LVs corresponding with snapshot VDIs only contain vhd metadata and by default consume 8MiB" and "When parent nodes are created they are automatically shrunk to the minimum size needed to store the shared blocks" — [xapi-project Snapshots](https://xapi-project.github.io/features/snapshots/snapshots.html)

**Recent upstream changes in this path (not yet in the 3.2.12-25.2 RPM):**
- [c3b24fdc "fix(LVMSR): isMaster wasn't always set"](https://github.com/xcp-ng/sm/commit/c3b24fdccf92ee4b35000f57fdc044ca5d780e36) (2026-11-05): `SR.from_uuid()` builds dconf without `SRmaster`, so `isMaster` was always False in such loads.
- [7ae4311b "fix(LVMSR): don't undo journals on nop loads"](https://github.com/xcp-ng/sm/commit/7ae4311b640dc6570432661de8e366f1a4dd8f15) (2026-11-09): skips `_undoAllJournals` (and its SR lock) for the `nop` load. Such a load is what the tap-unpause plugin does through `VDI.from_uuid`.

### Inferences
Tally of external commands per VDI snapshot. This is my own count from reading the code (not measured). Assumptions: VHD, thick, VM running on a non-master host, CBT enabled, chain depth d.

- **Master `vdi_snapshot` process: about 25 LVM command invocations without CBT.**
  - 1 `vgs` + 1 `lvs` at load
  - 1 `lvs` for the cbtlog check
  - 2 for leaf activation, plus 2 per inactive parent
  - `vgs` + `lvs` inside `vhd-util scan -l`
  - 1 `vgs` space check
  - `lvcreate` + `lvchange -an` for the journal
  - `lvrename`
  - `lvresize`
  - 2 × `lvcreate`
  - `lvchange -p r`
  - `lvremove`
  - `vgs` in `_updateStats`
  - several `lvchange -an` in cleanup
- **CBT adds roughly 25–35 more:** `lvrename`, about 10 full `lvs` refreshes, up to about 20 `lvchange -ay/-an` because the cbtlog LV is not active on the master when the VM runs elsewhere, `vgs`, and `lvcreate`.
- **Other process invocations:** about 8–10 `vhd-util` and up to about 10 `cbt-util`.
- **Cross-host plugin round trips: 4–5**, each of which starts a Python process on the target host and runs its own LVM commands:
  - tap-pause
  - on-slave deactivate
  - on-slave refresh/activate
  - on-slave CBT refresh
  - tap-unpause, which loads the whole SR driver and runs about 3–5 more LVM commands on that host
- **Plus a second SM process (`vdi_update`)** with its own `vgs` + `lvs` and MGT metadata update.

What the tally implies for timing:
- At an assumed 100–200 ms per LVM command on a VG with a few hundred LVs, 50–60 LVM commands alone would be about 5–12 s. That is in line with the observed 5–9 s per disk, but per-command latency was not measured. It is the first thing to verify (see Q5).
- The guest disk is paused for almost the whole per-VDI time: steps 1–14 all run between `tap_pause` and `tap_unpause`. With 5–9 s per disk, each disk's I/O is frozen for several seconds.
- Disks of one VM are paused one after another, not together. Because xapi snapshots the VBDs serially and each VDI has its own pause/unpause, the disks of a multi-disk VM are captured at different moments, seconds apart. The snapshot is therefore not crash-consistent across disks. That matters for applications that span disks (e.g., DB data/log on separate VDIs).
- VM placement changes the cost. If the VM runs on the pool master, `call_on_slave` skips the on-slave calls and the chain and cbtlog are already active on the master, so fewer `lvchange` calls run. This suggests (untested) that snapshots of VMs running on the master are cheaper per VDI than snapshots of VMs running on the other host.
- The 12 MiB config drive costing as much as or more than a 60 GiB disk is consistent with the code: the cost is per VDI and per LVM/plugin round trip, not per byte.

### Gaps
- No public measurement of how many milliseconds each LVM command takes on a VG of a given LV count with lvm2 2.02.180 on XCP-ng was found. The ~100–200 ms per command figure above is an assumption, not a sourced number.
- `getDefaultPreallocationSizeVirt()` for VHD in the current LVMSR code (the `-S` preallocation argument) was not traced. The 2 TiB value is from the older LVHDSR branch.
- I did not trace the exact LVM calls of `_checkMetadataVolume`/`LVMMetadataHandler` (MGT LV reads and writes) or measure their I/O size.

## Q2: Why are one VM's disks snapshotted serially, and why do snapshots of different VMs on the same SR wait for each other? Does `ignore_vdis` help?

### Takeaway
There are three serialization points. (1) xapi loops over a VM's VBDs serially and waits for each `VDI.snapshot` task. (2) The SM driver holds the SR lock on the master for the entire `vdi_snapshot` command (`OPS_EXCLUSIVE`), so only one VDI snapshot per SR runs at a time, pool-wide. (3) Every LVM command on the master passes through one host-wide FIFO lock (`Fairlock("devicemapper")`), shared by all SRs, the GC and multipath queries. `ignore_vdis` (API since xapi 21.4.0, used by XO on 8.3) avoids the whole per-VDI cost for excluded disks.

### Cited Findings
- xapi: `safe_clone_disks` folds over the VBD list. For each VBD it calls `clone_single_vdi` → `Client.Async.VDI.snapshot`, then waits for that task (`wait_for_clone`) and destroys it before the next VBD — [XAPI xapi_vm_clone.ml#L143-L223](https://github.com/xapi-project/xen-api/blob/61de57919e12c9f429acd72d4b1ee79dc203c180/ocaml/xapi/xapi_vm_clone.ml#L143-L223)
- `ignore_vdis`: for snapshot and checkpoint operations xapi filters the VBD list with `not (List.mem (Db.VBD.get_VDI vbd) ignore_vdis)` before calling `safe_clone_disks`, so ignored VDIs never reach SM — [XAPI xapi_vm_clone.ml#L424-L460](https://github.com/xapi-project/xen-api/blob/61de57919e12c9f429acd72d4b1ee79dc203c180/ocaml/xapi/xapi_vm_clone.ml#L424-L460)
- API definition: `VM.snapshot` parameter `ignore_vdis` ("A list of VDIs to ignore for the snapshot"), `param_release = numbered_release "21.4.0"`, default empty set — [XAPI ocaml/idl/datamodel_vm.ml#L372-L410](https://github.com/xapi-project/xen-api/blob/61de57919e12c9f429acd72d4b1ee79dc203c180/ocaml/idl/datamodel_vm.ml#L372-L410)
- XCP-ng docs: disks can be excluded by "putting `[NOSNAP]` (snapshots) or `[NOBAK]` (backups and their snapshots) in the disk's name" and "On XCP-ng 8.3, disk exclusion is supported natively by the snapshot API, which XO uses." — [docs.xcp-ng.org Snapshots](https://docs.xcp-ng.org/vms/snapshots/)
- SR lock: `vdi_snapshot`/`vdi_clone` are in `OPS_EXCLUSIVE`, and `SRCommand._run_locked` holds `sr.lock` (blocking fcntl lock in `/var/lock/sm/<sr-uuid>/`) for the full command — [SM LVMSR.py#L82-L86](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L82-L86), [SRCommand.py#L139-L170](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/SRCommand.py#L139-L170), [lock.py#L41](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lock.py#L41)
- Snapshots must run on the master ("LVMMaster" error otherwise), so this local file lock is effectively pool-wide per SR — [SM LVMSR.py#L1747-L1752](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1747-L1752)
- Even commands that are not in `OPS_EXCLUSIVE` (e.g. xapi's follow-up `vdi_update`) take the SR lock briefly during `load()` on the master through `_undoAllJournals()` — [SM LVMSR.py#L217-L222](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L217-L222), [L1175-L1185](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1175-L1185)
- Host-wide LVM serialization: `cmd_lvm` → `with Fairlock("devicemapper")` — [SM lvutil.py#L145-L195](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L145-L195). The same lock name is used by multipath CLI calls — [SM mpath_cli.py#L39](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/mpath_cli.py#L39), [mpath_dmp.py#L81-L173](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/mpath_dmp.py#L81-L173)
- GC/coalesce (`cleanup.py`) competes for the same SR lock. It polls with `acquireNoblock()` and `LOCK_RETRY_INTERVAL = 3` s, up to 100 attempts — [SM 3.2.12-8.3 cleanup.py#L1937-L1939, L2331-L2357](https://github.com/xcp-ng/sm/blob/d8ca026a32674e1d76a488c92c6dbb96d3775075/drivers/cleanup.py#L2331-L2357). `VDI.delete` kicks the GC (`self.sr._kickGC()`) — [SM LVMSR.py#L1516](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1516), [L1332](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1332)
- Xen Orchestra runs snapshots in parallel up to a "snapshot concurrency (2 by default)" and backup "Concurrency" defaults to 2 — [XO docs: Backup features and settings](https://docs.xen-orchestra.com/backups-and-dr/backup-features-and-settings)

### Inferences
- **Two VMs at once on one SR (38–103 s):** the second VM's `vdi_snapshot` blocks on the SR lock for as long as the first VDI snapshot runs, and per-VDI tasks of both VMs interleave in lock order. Both VMs therefore take about the sum of all per-VDI times, which explains "they wait for each other". The worst case (103 s, more than 2 × 47 s) suggests extra contention on top of that. Likely candidates are the `vdi_update` follow-ups, a GC woken by a snapshot deletion, and Fairlock queueing with other SM activity. This is unverified.
- **Snapshots on SR A and SR B in parallel** are not blocked by the SR lock. Their LVM commands still queue on the master's single `Fairlock("devicemapper")`, so the gain is limited to the non-LVM parts (XAPI and plugin round trips, vhd-util, tapdisk pause/drain). Both SRs share one master, and both SRs' GCs use the same LVM lock.
- **Parallelizing at the backup-product level** within one SR gives no throughput gain and lengthens each VM's snapshot window, so per-VM pauses get longer. One SR at a time per VM, but different SRs in parallel, is the most that the locks allow.
- **`ignore_vdis` helps directly:** an ignored VDI never reaches SM, saving its whole per-VDI cost (for the 2-disk VM with a 12 MiB config drive that is about 15 s, according to the measurements). Excluded disks are not in the snapshot VM, so the backup product must handle them (e.g. re-create or skip config drives).
- **Snapshot deletion and GC:** deleting old snapshots right before or between snapshots kicks the GC, which then competes for the SR lock and the LVM Fairlock while snapshots run. Batching deletions after a snapshot round may reduce interference. This is an inference and was not measured.

### Gaps
- I did not verify whether xapi's `allowed_operations` logic blocks concurrent `VDI.snapshot` on different VDIs of the same SR at the API level. The code read only shows per-VDI locks in the SMAPIv1 wrapper.
- No public measurement was found of how much Fairlock queueing adds when two SRs are snapshotted in parallel.

## Q3: Which costs grow with the number of LVs in the VG, with multipath, with the number of pool hosts, or with iSCSI latency?

### Takeaway
LV count matters most: every full `lvs` scan (several per VDI, about 10 more with CBT) and every metadata read or write scales with the size of the VG's text metadata, which grows with every VDI, base copy, snapshot and `.cbtlog` LV. Pool host count does not matter for snapshots, because only hosts that have the VDI attached are contacted. Multipath is not in the snapshot path except through the shared host-wide Fairlock. iSCSI latency adds to each synchronous LVM metadata I/O and each VHD/CBT header I/O, multiplied by the 50–60 command invocations.

### Cited Findings
- **Full VG scans in the snapshot path:**
  - `LVMCache.refresh()` runs `lvs ... /dev/VG_XenStorage-<sr>` (all LVs) — [SM lvmcache.py#L75-L105](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvmcache.py#L75-L105)
  - it is called on every new process's first cache access (`lazyInit`) — [SM lvmcache.py#L45-L57](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvmcache.py#L45-L57)
  - and on every CBT log operation — [SM LVMSR.py#L2385-L2396](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L2385-L2396)
  - `vhd-util scan -l <VG>` runs `vgs` and `lvs` over the VG — [blktap lvm/lvm-util.c#L123-L240](https://github.com/xapi-project/blktap/blob/45f6e8e0e056a8f6aa516e24918eb1606902c45a/lvm/lvm-util.c#L123-L240)
- **VG metadata area:** SM creates the PV/VG with `--metadatasize 10M` — [SM lvutil.py#L493-L500](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L493-L500)
- **XCP-ng 8.3 lvm2 package** (`lvm2 2.02.180-18.3.1`, Feb 2026) — [xcp-ng-rpms/lvm2 SPECS/lvm2.spec#L272-L286](https://github.com/xcp-ng-rpms/lvm2/blob/5a29d27736634ec544987afa0d5a25c6ed5ceb68/SPECS/lvm2.spec#L272-L286):
  - `use_lvmetad = 0` (no metadata caching daemon), `archive = 0`, `udev_sync = 0`, `udev_rules = 0`, `obtain_device_list_from_udev = 0`, `write_cache_state = 0`
  - a separate `/etc/lvm/master/lvm.conf` that is a copy with `metadata_read_only = 0`
  - The spec does not change `backup`.
- **LVM default:** "Metadata backups and archives are automatically created on every volume group and logical volume configuration change unless disabled in the lvm.conf file" (default backup dir `/etc/lvm/backup`) — [Red Hat/CentOS LVM Administration: Logical Volume Backup](https://vault.centos.org/4.6/docs/4.5/SAC_Cluster_Logical_Volume_Manager/backup.html). With `archive = 0` but `backup` left at its default, every metadata-changing command on the master should still rewrite the VG's metadata backup file. Because SM sets `LVM_SYSTEM_DIR=/etc/lvm/master`, LVM's default backup directory would be `/etc/lvm/master/backup`. I did not verify either point on a host.
- **XenServer lvm2 patch `scan_only_the_device_in_LVM_DEVICE_env.diff`:** "if LVM_DEVICE env variable is set, only the physical device present in the dir will be scanned. This will speed up a lot lvm commands when there are many LVs" — [xcp-ng-rpms/lvm2 SOURCES/scan_only_the_device_in_LVM_DEVICE_env.diff](https://github.com/xcp-ng-rpms/lvm2/blob/5a29d27736634ec544987afa0d5a25c6ed5ceb68/SOURCES/scan_only_the_device_in_LVM_DEVICE_env.diff). SM sets `LVM_DEVICE` — [SM SR.py#L136-L139](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/SR.py#L136-L139).
- **XenServer lvm2 patch CA-410086 (Mark Syms, 2025-04-29), in the XCP-ng 8.3 lvm2 since `2.02.180-18.2.1` (Sep 2025):**
  - Problem statement: "The default configuration of LVM2 results in the `lvs` and `vgs` commands performing a lot of unnecessary work by 1. running stat and lstat on everyting in and below `/dev/` 2. Opening all the block devices it finds and reading the first 128K from them, including the Logical Volumes which form the SR VDIs for the LVM based XenServer SR."
  - Fix: changes `scan = [ "/dev/disk/by-id" ]` and adds reject filters for `dm-name-VG_XenStorage.*`, `dm-uuid-LVM-.*` and `dm-name-XSLocal.*`.
  - Source: [xcp-ng-rpms/lvm2 SOURCES/CA-410086_filter_out_unnecessary_devices](https://github.com/xcp-ng-rpms/lvm2/blob/5a29d27736634ec544987afa0d5a25c6ed5ceb68/SOURCES/CA-410086_filter_out_unnecessary_devices), changelog in [lvm2.spec](https://github.com/xcp-ng-rpms/lvm2/blob/5a29d27736634ec544987afa0d5a25c6ed5ceb68/SPECS/lvm2.spec).
- **Pool hosts:** `get_hosts_attached_on` returns only hosts that have a `host_<ref>` key in the VDI's sm-config, i.e. where the VDI is attached. `call_on_slave` skips the master. `tap_pause`/`tap_unpause` also only contact `host_*` hosts — [SM util.py#L897-L908](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/util.py#L897-L908), [LVMSR.py#L1259-L1269](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1259-L1269), [blktap2.py#L1513-L1555](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/blktap2.py#L1513-L1555). Pool-wide `lvRefreshOnAllSlaves` appears only in journal-undo recovery paths — [SM 3.2.12-8.3 LVHDSR.py#L1182-L1239](https://github.com/xcp-ng/sm/blob/d8ca026a32674e1d76a488c92c6dbb96d3775075/drivers/LVHDSR.py#L1182-L1239).
- **Activation always refreshes:** `LVMCache.activate` calls `activateNoRefcount(lvName, True)`, which runs `lvchange -ay` + `lvchange --refresh` + `dmsetup table` — [SM lvmcache.py#L143-L156](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvmcache.py#L143-L156), [lvutil.py#L685-L698](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L685-L698)
- **Multipath:** the multipath CLI wrapper and DMP code use the same `Fairlock("devicemapper")` as LVM — [SM mpath_cli.py#L39](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/mpath_cli.py#L39), [mpath_dmp.py#L81-L173](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/mpath_dmp.py#L81-L173). `mpathcount` (socket-activated service, `systemd/mpathcount.socket`) queries multipath topology through `mpath_cli` — [SM mpathcount.py#L92-L101](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/mpathcount.py#L92-L101)
- **`lvm-conf` SR other-config key:** it only affects `lvremove` calls ("For now The configuration is only used by 'remove' calls from LVMCache"; example `issue_discards=1`), so it is not a snapshot-speed knob — [commit f13a8954](https://github.com/xcp-ng/sm/commit/f13a895426833145e36049e8f05eb22eeb497059)
- **Older context (thin-LVHD design proposal, never shipped as such):** "When thin-provisioning is enabled we will be modifying the LVM metadata at an increased rate" and the goal was a "fast query/update API so we can (for example) create lots of LVs very fast", using a redo-log "to ensure they operate in O(1) time" — [xapi-project thin LVHD design](https://xapi-project.github.io/xapi/futures/thin-lvhd/thin-lvhd.html). This implies the current direct LVM metadata path is not O(1).

### Inferences
- **LV count** is the main growth factor. Every `lvs`, `vgs`, `lvchange`, `lvcreate`, `lvrename`, `lvresize` and `lvremove` reads, and for changes rewrites, the whole VG text metadata on the PV (and the `/etc/lvm/backup` copy). Per VDI each VG holds at least:
  - one `VHD-*` LV
  - one `<uuid>.cbtlog` LV with CBT
  - for every retained snapshot, another 8 MiB snapshot LV, its cbtlog LV and a base-copy LV, until GC coalesces them

  With ~35 VMs over two SRs and CBT on most VDIs, a few hundred LVs per VG is plausible. Each extra snapshot round temporarily adds about 3 LVs per VDI. Keeping retained snapshots to the minimum CBT needs (the latest one) and letting GC finish keeps LV count, and so per-command time, down.
  - The "full metadata rewrite on every change" point is general LVM2 design knowledge, not verified in this session against lvm2 2.02.180 sources.
- **The 74 % full SR** probably has more LVs (more base copies and snapshots) and therefore slower LVM commands than the 27 % one. Comparing `lvs | wc -l` and per-snapshot times between the two SRs would test this (see Q5).
- **iSCSI latency:** each LVM metadata change is a few synchronous small I/Os to the LUN (O_DIRECT), and each `vhd-util` or `cbt-util` call does a few header I/Os. With 50–60 LVM commands and about 20 other tools per VDI, 1 ms of extra latency costs well under a second per VDI. Process startup, metadata parsing and lock queueing are more likely to dominate on a low-latency all-flash array. Not measured.
- **Number of hosts:** irrelevant for snapshots. Only the host running the VM (if it is not the master) gets 3–4 extra plugin calls. VM placement matters (on master = fewer cross-host calls) more than pool size.
- **Multipath path count:** not on the snapshot path. Path flaps or `mpathcount` runs during a snapshot window can queue in front of LVM commands through the shared Fairlock.

### Gaps
- No public benchmark of `lvs`/`lvcreate` latency vs. LV count for lvm2 2.02.180 with XCP-ng's lvm.conf was found. A Red Hat Bugzilla report about >1,100 LVs appeared in the search results but was not read and is from a much older LVM era, so it is not used here.
- The final `backup` setting in the shipped `/etc/lvm/master/lvm.conf` was not verified on a host (the spec only shows that `archive` is set to 0).
- I did not verify whether the "CP-31079: update default configuration" change (lvm2 `18.3`, Dec 2025) altered scan, filter or backup settings again.

## Q4: Known public reports of slow snapshots on LVM/iSCSI SRs (timings, fixes)

### Takeaway
Public reports with concrete per-snapshot timings on `lvmoiscsi` are scarce. Most public material is about coalesce, space and chain length, not snapshot latency. The best primary evidence is upstream commit messages: the LVM lock rework because of "many minutes" lock waits, the lvm2 scan/filter patches "to speed up a lot lvm commands when there are many LVs", the 60 s pause warning, and recent fixes around pause, unpause and tapdisk.

### Cited Findings
- XCP-ng forum, user tmk (SR type not stated; NFS refers to the backup target): "The Snapshot Duration was somewhere between 2-10 minutes per VM." Vates staff responses addressed other backup phases and the `diskPerVmConcurrency` setting, not snapshot time — [XCP-ng forum topic 11434](https://xcp-ng.org/forum/topic/11434/long-backup-times-via-nfs-to-data-domain-from-xen-orchestra)
- XCP-ng forum (8.2, LargeBlock SR creation on a local 4Kn device): SMlog showed `***** Long LVM call of 'vgs' took 43.6255850792`. A Vates developer (dthenot) said this likely explained the ~10 min SR creation. Later manual `vgs` calls returned immediately. This shows the "Long LVM call" log marker in the field. Different SR type and operation *(8.2, older)* — [XCP-ng forum post 100016](https://xcp-ng.org/forum/post/100016)
- XCP-ng forum, "Switching to XCP-NG, want to hear your problems" (about 2 years old). According to the search-result snippet, a user said snapshots were a pain on a thick LVM iSCSI SR and another replied they moved to NFS ("snapshots coalescale pretty fast"). I did not read the full thread — [XCP-ng forum topic 9093](https://xcp-ng.org/forum/topic/9093/switching-to-xcp-ng-want-to-hear-your-problems)
- GitHub xcp-ng/xcp issue #285 "hanging LVMoISCSISR processes during snapshot operations" (XCP-ng 8.0 era, closed). Only the title was seen in search results; not read *(older)* — [xcp-ng/xcp#285](https://github.com/xcp-ng/xcp/issues/285)
- XCP-ng 8.3 update announcements (search-result snippet, not fully read) mention "A fix for a tapdisk crash happening when tapdisk pauses a VDI when a coalesce is in progress". They also mention a fix for shared LVM SRs (LVMoISCSI, LVMoHBA) where "a failed live leaf coalesce of a QCOW2 image on a slave host caused the SRs to remain in an SR_FAILURE_1200 error state" — [XCP-ng forum topic 9964 (8.3 updates)](https://xcp-ng.org/forum/topic/9964/xcp-ng-8-3-updates-announcements-and-testing?page=35)
- Upstream fixes and motivations relevant to snapshot latency:
  - LVM lock starvation "known to result in a commant taking many minutes to get the lock" → fair lock queue — [125aa9ec](https://github.com/xcp-ng/sm/commit/125aa9eca4900b4acfff232eafd62a3423ffd99b), [a077ecda](https://github.com/xcp-ng/sm/commit/a077ecda407337e5c6d178a6d4ee60c0aea27509)
  - Pause longer than 60 s gets logged — [ec5662b1](https://github.com/xcp-ng/sm/commit/ec5662b143537028ceccf778b330de9a63a97f6c)
  - lvm2 `LVM_DEVICE` scan restriction and CA-410086 scan/filter restriction — [lvm2.spec / SOURCES](https://github.com/xcp-ng-rpms/lvm2/tree/5a29d27736634ec544987afa0d5a25c6ed5ceb68/SOURCES)
  - "CP-312848: allow tapdisk-pause.Pause to be idempotent when safe" (May 2026, recovery after a failed refresh) — [f998adba](https://github.com/xcp-ng/sm/commit/f998adba146597b0166f17664adaeb2b4d376a9e)
- Xen Orchestra docs: snapshot concurrency defaults to 2; "Synchronize snapshots" can snapshot every VM up front; "High concurrency could impact your dom0 and network performances" — [XO docs](https://docs.xen-orchestra.com/backups-and-dr/backup-features-and-settings)
- XCP-ng docs on thick LVM SRs: "a snapshot can reserve up to the full disk size"; coalesce reclaims space asynchronously; "Each snapshot extends the disk chain; a long chain slightly degrades I/O performance." — [docs.xcp-ng.org Snapshots](https://docs.xcp-ng.org/vms/snapshots/)

### Inferences
- No public source reports per-disk snapshot times for `lvmoiscsi` on 8.3 directly comparable to 5–9 s per VDI. The internal measurements are likely more specific than anything public.
- Fixes that have publicly "worked" for related problems are structural: excluding disks ([NOSNAP]/`ignore_vdis`), fewer retained snapshots and shorter chains (letting GC coalesce), moving to file-based SRs (NFS), and newer lvm2/sm builds with the scan and lock fixes. None of these is a configuration knob that makes the per-VDI LVM sequence itself shorter.

### Gaps
- No Citrix/XenServer knowledge-base article with snapshot timings for LVMoISCSI was found (Citrix KB search results were not relevant).
- No Xen Orchestra GitHub issue specifically about slow `VM.snapshot` on LVM SRs was found.
- The 8.3 update-thread items and the xcp-ng/xcp#285 issue were seen only as search snippets.

## Q5: Which logs show per-step timing, and what does a typical SMlog sequence of one snapshot look like?

### Takeaway
`/var/log/SMlog` on the master logs every external command (LVM, vhd-util, cbt-util) and the `pread SUCCESS` line that follows it, plus the pause/unpause and on-slave messages. The SMlog of the host running the VM shows the tap-pause/unpause and on-slave plugin work. `xensource.log` shows the xapi `VM.snapshot` begin/end and each async `VDI.snapshot` task. Diffing timestamps of consecutive SMlog lines for one PID gives per-step timing.

### Cited Findings
- `util.pread` logs the command list before running it and `"  pread SUCCESS"` after it (or `FAILED in util.pread: (rc …)`). LVM calls go through `cmd_lvm` → `util.pread2`/`util.pread` — [SM util.py#L281-L312](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/util.py#L281-L312), [lvutil.py#L145-L195](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L145-L195). SMlog writes through syslog with ident "SM" — [util.py#L223-L226](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/util.py#L223-L226)
- Built-in slow-call markers:
  - `***** Long LVM call of '<cmd>' took <seconds>` (above 15 s) — [lvutil.py#L56, L190-L193](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvutil.py#L185-L195)
  - `WARNING: snapshot paused VM for <n> seconds` (above 60 s) — [LVMSR.py#L1741-L1744](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1741-L1744)
- Log strings emitted in order during one VDI snapshot (from the code). The commands appear between these lines:
  1. `LVMCache created for VG_XenStorage-<sr>`, then `['/sbin/vgs', '--readonly', ...]`, then `LVMCache: will initialize now` / `LVMCache: refreshing` with the `lvs` command — [lvmcache.py#L45-L80](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/lvmcache.py#L45-L80)
  2. `Saving log consistency state of <bool> for vdi: <uuid>` (CBT only), `Pause request for <vdi>`, `Calling tap-pause on host OpaqueRef:...` — [LVMSR.py#L1711-L1725](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1711-L1725), [blktap2.py#L1523-L1529](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/blktap2.py#L1523-L1529)
  3. `LVMVDI._snapshot for <vdi> (type 2)`. The older LVHDSR prints `LVHDVDI._snapshot …` — [LVMSR.py#L1748](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1748)
  4. Activation commands (`lvchange -ay`, `lvchange --refresh`, `dmsetup table`, `DM table for ...`), then `vhd-util scan/query`, `vgs`
  5. `Deactivating VHD-<vdi> on slave OpaqueRef:...` followed by `call-plugin returned: True` (when the VM is on another host) — [LVMSR.py#L1259-L1316](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1259-L1316)
  6. `lvcreate` (journal), `lvchange -an`, `lvrename`, `vhd-util modify -s`, `lvresize`, `lvcreate` + `vhd-util snapshot` ×2, `vhd-util set … hidden`, `lvchange -p r`
  7. `Updating VHD-<vdi>, VHD-<vdi>, VHD-<base> on slave ...`
  8. CBT only: `lvrename …cbtlog`, then repeated `LVMCache: refreshing` + `lvs` + `lvchange -ay` + `cbt-util …` + `lvchange -an`, then `Updating <vdi>.cbtlog on slave ...`
  9. `lvremove` (journal), `Setting virtual_allocation of SR …`, `vgs`, `vdi_clone: introduced 2nd snap VDI: …`, `vdi_clone: introduced base VDI: …` — [LVMSR.py#L1942-L2071](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/LVMSR.py#L1942-L2071)
  10. `Unpause request for <vdi> secondary=None`, `Calling tap-unpause on host …`
- On the VM's host SMlog (plugin side):
  - `on-slave.multi: {...}`, `on-slave.action 1: deactivateNoRefcount` — [on_slave.py#L31-L45](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/on_slave.py#L31-L45)
  - `Pause for <vdi>`, `Calling tap pause with minor N`
  - `Unpause for <vdi>`, `Realpath: …`, `Calling tap unpause with minor N` — [tapdisk_pause.py#L203-L260](https://github.com/xcp-ng/sm/blob/538c3e8b5e3130076bbf4e1cb1b19c287d53b11d/drivers/tapdisk_pause.py#L203-L260)
- xapi debug lines `Snapshot: begin` / `Snapshot: end` bracket the whole `VM.snapshot` in xensource.log — [XAPI xapi_vm_snapshot.ml#L29-L37](https://github.com/xapi-project/xen-api/blob/61de57919e12c9f429acd72d4b1ee79dc203c180/ocaml/xapi/xapi_vm_snapshot.ml#L29-L37). Per-VDI async tasks are created by `Client.Async.VDI.snapshot` — [xapi_vm_clone.ml#L143-L174](https://github.com/xapi-project/xen-api/blob/61de57919e12c9f429acd72d4b1ee79dc203c180/ocaml/xapi/xapi_vm_clone.ml#L143-L174)

### Inferences
- The sequence above is reconstructed from log statements in the code, not copied from a real SMlog. Expect the PID in brackets to identify the `vdi_snapshot` process. A second PID for the same VDI shortly afterwards is xapi's `vdi_update`.
- Per-step breakdown recipe (all read-only):
  1. On the master, take one snapshot and extract its SMlog lines by PID.
  2. Diff the timestamps of each `['/sbin/<lvm-cmd>', …]` line and its following `pread SUCCESS`. That gives execution time plus Fairlock wait, because the lock is taken inside `cmd_lvm` before the command runs.
  3. Count total LVM commands and `LVMCache: refreshing` occurrences per VDI.
  4. Compare the gap between `Calling tap-pause` and `Calling tap-unpause` (the pause window) with the total task time in xensource.log.
- Read-only diagnostics that relate this to LV count (no changes to the pool):
  - `lvs --noheadings VG_XenStorage-<sr-uuid> | wc -l`
  - `vgs -o vg_name,lv_count,vg_mda_size,vg_mda_free VG_XenStorage-<sr-uuid>`
  - `time lvs VG_XenStorage-<sr-uuid> >/dev/null`, run on the master with `LVM_SYSTEM_DIR=/etc/lvm/master LVM_DEVICE=/dev/disk/by-scsid/<SCSIid>` to mimic SM

  If one `lvs` takes, for example, 150 ms and the SMlog shows about 50–60 LVM commands per VDI, LVM alone explains most of the 5–9 s.
- Checking `rpm -q lvm2 sm` against the versions above (lvm2 `2.02.180-18.2.1` or later contains CA-410086; sm `3.2.12-25.x`) confirms which scan and lock fixes are active.

### Gaps
- I did not confirm whether SMlog timestamps on XCP-ng 8.3 have sub-second resolution (depends on the rsyslog template). If they are whole seconds only, per-command timing must come from aggregating many snapshots or from `time`-ing equivalent read-only commands.
- I did not find where tapdisk logs its own pause/drain duration (tapdisk logs to syslog/daemon.log). How long `tap-ctl pause` waits for in-flight I/O to drain on a busy VM is not quantified.
