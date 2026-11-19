# Spikes: instant VM recovery on vSphere

Live checks against a vCenter 7.0.3 with ESXi 7.0.3 hosts, the backup server on Windows Server 2025 (the product itself
after tasks 1–8, not a throwaway server). Addresses below are placeholders: the backup server `192.0.2.10`, the host's
VMkernel NICs `192.0.2.21` (same subnet as the server), `198.51.100.21` and two storage networks.

## S1: mount and boot (2026-11-19) — passed

Setup: `InstantRecovery:Enabled` on; Windows firewall rules for TCP/UDP 111, TCP 2049 and TCP/UDP 20048, bound to the
server's program and limited to the two test hosts' VMkernel addresses; the server's own portmapper (port 111 was free:
no "Server for NFS" role). The host check (`GET /api/instant-recoveries/host`) named the four VMkernel addresses, 0 of
32 NFS datastores mounted (`NFS.MaxVolumes` = 32), and the server address that routes to the host (`192.0.2.10`).

| VM | Restore point | Mount → power-on | Guest up | Written while booting |
|---|---|---|---|---|
| Ubuntu 64-bit, 10 GiB disk (+ one disk left out of the backup, served empty) | incremental | 1 s | Tools running, heartbeat green, within 2 min (first look at 124 s uptime) | 14 MiB |
| Windows, 40 GiB disk | full | 1 s | Tools running after 24 s, heartbeat green after about 55 s | 51 MiB |

- `CreateNasDatastore` (NFS 3, read-write) from the Windows server worked at the first try; ESXi mounted from the
  VMkernel NIC in the server's subnet.
- `CreateVM_Task` with the descriptors on the NFS datastore (`createType="vmfs"`, flat extent) and host-local swap: no
  question from ESXi, the VM powered on at once.
- NICs disconnected, new MAC addresses, the note on the VM says what it is.
- Stop: power off, `UnregisterVM`, `RemoveDatastore`: nothing left in vCenter, the overlay deleted on the server.

Still open from S1: logging every RPC and a packet capture on the host (procedures, write stability, rsize/wsize,
nConnect, `.lck` files, READDIRPLUS).

## S3: restart under a running VM (2026-11-19) — passed for a clean restart

A clean `Restart-Service` of the backup server while the Ubuntu VM ran from it: health answered again after 35 s; the
session logged the stop, resumed by itself and served the same export with the same file handles. The guest did not
notice: uptime went on (244 s before, 304 s after), Tools running, heartbeat green.

Still open: a killed process (no clean stop), and outages of 90 and 180 s (guest disk timeouts).

## S4: Windows server — partly

Port 111 binds (own portmapper), the firewall rules work program-bound. Throughput (target 300 MB/s sequential) not
measured yet.

## S2: Storage vMotion — not done yet

Needs a production datastore to move a test VM to, and its removal afterwards.
