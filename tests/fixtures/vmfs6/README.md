# VMFS6 test images

Partial images of VMFS6 datastores written by ESXi, taken unchanged from the OpenVmfs repository (Apache-2.0, the
same licence as OpenVmfs itself), and the hashes ESXi computed of their disks. `VmfsFixtures` in
`tests/Backdafuckup.Testing/Arrays` reads them; reading outside the captured ranges is an error.

| Image | What it holds |
|---|---|
| `fixture-chain-esxi8-v1` | ESXi 8.0 U3: a disk `guest/t` with three VM snapshots and guest writes and discards between them; `truth/sha256.txt` has the base and ESXi's consolidated clone of each level (`clone8/atN`) |
| `fixture-big-v1` | ESXi 7.0.3: a 17 TiB datastore whose test disks `t/thin` and `t/lazy` lie above 16 TiB, behind the gap in the logical volume |
| `fixture-inuse-v1` | ESXi 7.0.3: an array snapshot taken about a second after a VM snapshot; `probe/m.vmdk` is the disk under it, `probe/m-000001.vmdk` the delta the VM was writing to (locked exclusively) |

Format: `NAME.ranges` is `size BYTES`, then one `OFFSET LENGTH` line per captured range of the device;
`NAME.parts.zst` holds the bytes of those ranges, concatenated in that order, zstd-compressed.

A committed image is never replaced; a changed one comes as a new version (`-v2`).
