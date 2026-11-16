# Lab VTL (OpenTofu + Xen Orchestra + Holo VTL)

A virtual tape library for the tape rows of the [lab runbook](../../docs/lab-runbook.md):
- **Holo VTL** ([github.com/Holo-VTL/Holo](https://github.com/Holo-VTL/Holo), MIT), installed from a pinned release whose SHA-256 is checked;
- one Ubuntu 24.04 VM in an existing pool, created through Xen Orchestra with the [`vatesfr/xenorchestra`](https://registry.terraform.io/providers/vatesfr/xenorchestra/latest) provider (MPL-2.0);
- by default an **HPE MSL6480** with two **HPE Ultrium 9** (LTO-9) drives, 24 slots, 4 mail slots and 20 cartridges of 2 GiB. The small cartridges make the end of a tape and tape changes quick to test.

Holo publishes one iSCSI target per device: the changer and each drive. The backup server, or a host with the tape agent, logs in with its iSCSI initiator and then sees ordinary SCSI tape devices.

## What it creates

- One VM (`vm_name`, default `bdfu-lab-vtl`) with the tag `bdfu-lab`:
  - a system disk;
  - an empty second disk that Holo formats for the cartridges.
- At the first boot, cloud-init runs `holo-setup`:
  1. Download the release and check its SHA-256.
  2. Run Holo's installer.
  3. `holo-bootstrap.py` creates the storage pool, the library, the drives, the iSCSI ACL and the cartridges through Holo's REST API.

  It takes 3–5 minutes; `sudo tail -f /var/log/holo-setup.log` on the VM shows it.

## Safety

- **SR:** the VM's disks go only to an SR with the tag `bdfu-lab`. An SR without it is not found, and the run stops before anything is created.
- **Removal:** `tofu destroy` removes exactly this VM and its disks, nothing else in the pool. Run it only yourself, when you mean it; the cartridges are gone with it.
- **API key:** the key of Holo's API and web UI is in the OpenTofu state (`terraform.tfstate`, never committed) and on the VM in `/etc/holo-terraform/api-key`.
- **Plain HTTP:** Holo serves its web UI and API over plain HTTP on port 80, so use them on the lab network only.
- **Who may log in:** without `initiator_iqns`, every initiator may log in to the targets. Name the backup server and the tape agent host there, or set CHAP in Holo's web UI (*iSCSI Security*).

## Prerequisites

- In Xen Orchestra:
  - the lab SR with the tag `bdfu-lab`;
  - a network the backup server reaches on 3260 (iSCSI) and 80 (API);
  - an Ubuntu 24.04 template with cloud-init.
- The VM needs internet access at the first boot (apt, and the release on GitHub).
  - Behind a proxy that breaks TLS (its own certificate for github.com), the download fails with "self-signed certificate in certificate chain". Download the release where TLS is intact instead, check it, and put it on the VM; holo-setup uses it and checks its SHA-256:

    ```bash
    curl -fsSLO "https://github.com/Holo-VTL/Holo/releases/download/Holo%40v1.0.7/holo-vtl-1.0.7-linux-x86_64.tar.gz"
    scp holo-vtl-1.0.7-linux-x86_64.tar.gz ubuntu@<address of the VM>:/tmp/
    ssh ubuntu@<address of the VM> 'sudo mkdir -p /opt/holo-release/1.0.7 && sudo mv /tmp/holo-vtl-*.tar.gz /opt/holo-release/1.0.7/ && sudo /usr/local/sbin/holo-setup'
    ```
- An account that may create VMs:
  - either a pool administrator;
  - or, better, a user with a resource set that holds the lab SR, the lab network and the template. Set its name as `resource_set`; such a user cannot touch anything outside it.

## Use

```bash
cd deploy/vtl
cp vtl.tfvars.example vtl.auto.tfvars         # fill in pool, SR, network, template
export XOA_URL=wss://xo.example.local         # the Xen Orchestra address
export XOA_TOKEN=...                          # or XOA_USER and XOA_PASSWORD; read them from a file

tofu init
tofu plan                                     # shows exactly the VM and disks that would be created
tofu apply
tofu output vm                                # name, id, address
tofu output -raw api_key                      # web UI login and X-HOLO-API-Key
tofu output iscsi_targets
```

For a Xen Orchestra with a self-signed certificate, trust that certificate rather than turning the check off. OpenTofu and its providers read extra trusted certificates from `SSL_CERT_FILE`: point it to a file with the system's CA bundle and the certificate of Xen Orchestra.

**Logging in from a Windows backup server** (as Administrator):

```powershell
New-IscsiTargetPortal -TargetPortalAddress <address of the VM>
Get-IscsiTarget | Connect-IscsiTarget -IsPersistent $true
```

**Logging in from a Linux host** (e.g. the tape agent):

```bash
sudo apt install open-iscsi
sudo iscsiadm -m discovery -t sendtargets -p <address of the VM>
sudo iscsiadm -m node -p <address of the VM> --login
lsscsi -g                                     # the changer (mediumx) and the drives (tape)
```

**Test states through Holo's REST API** (`X-HOLO-API-Key`):

| What | Call |
|---|---|
| Load or unload | `POST /v1/drives/{id}/load` with `{"cartridgeId": "BDF000L09"}`; `POST /v1/drives/{id}/unload` |
| Erase | `POST /v1/cartridges/{id}/erase` with `{"mode": "short"}` |
| Move to or from the mail slots | `POST /v1/cartridges/{id}/export`, `POST /v1/cartridges/{id}/import` |
| Create a cartridge | `POST /v1/cartridges` |
| Read the state | `GET /v1/cartridges/{id}` (`usedBytes`, `lifecycleState`, slot), `GET /v1/drives/{id}` |

**Updating Holo:** set `holo_version` and `holo_sha256`, then run `sudo /usr/local/sbin/holo-setup` on the VM. It installs the new release in upgrade mode and keeps the cartridges.

## What Holo does differently from a real library

Seen on Holo 1.0.7 (runbook rows T1–T6):
- **No early warning.** Its early-warning window is 8 bytes, so writes run into VOLUME OVERFLOW without a warning before. The EOP bit of READ POSITION does not help either. backdafuckup then writes the torn file again on the next cartridge.
- **Less than the cartridge's memory says.** A 2 GiB cartridge overflows at about 1984 MiB, although its MAM reported 2047 MiB free. The MAM remaining capacity follows the writes MiB by MiB.
- **At most 256 KiB per WRITE over iSCSI.** Larger blocks are refused with INVALID FIELD IN CDB (24/00), although READ BLOCK LIMITS reports 16 MiB. Keep the block size at 256 KiB.
- **Mail slots never hold a cartridge.** A move into a mail slot exports the cartridge at once (it leaves the library). `POST /v1/cartridges/{id}/import` puts it straight back into its slot, not into a mail slot.
- **UNIT ATTENTION 28/00 at the next command.** After a load it does not come with TEST UNIT READY but with the next command, e.g. the first WRITE.
- **INQUIRY reads only the low byte of the allocation length,** as in SCSI-2.
- **Not emulated:** hardware encryption (SECURITY PROTOCOL IN/OUT), ILI and the FILEMARK bit in sense data, and TapeAlert entries.

backdafuckup handles each of these; the real library covers what Holo cannot (runbook row T31).
