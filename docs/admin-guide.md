# backdafuckup – Administrator Guide

## Requirements

| Item | Notes |
|---|---|
| Backup server | Linux (systemd) or Windows Server, x64. Must **not** run in dom0 of an XCP-ng host. A VM in the pool or a separate machine both work. |
| .NET | Either publish self-contained (no runtime needed) or install the ASP.NET Core 10 runtime. |
| PostgreSQL | 15 or later, for the catalog (jobs, restore points, history). |
| Repository storage | A directory on a volume mounted on the backup server, e.g. an iSCSI LUN from an HPE MSA. |
| XCP-ng / XenServer | XCP-ng 8.3 or XenServer 8, with a pool admin account for the connection. |
| NBD network | At least one pool network must have the NBD purpose, and it must reach the storage of the VMs to be backed up. |

### Enable NBD on a pool network

Backups read VM disks over NBD (TCP 10809) with TLS:

```bash
xe network-list                                    # find the network UUID
xe network-param-add param-name=purpose param-key=nbd uuid=<network-uuid>
```

The backup server must reach that network on port 10809. Use `insecure_nbd` (no TLS) only in isolated test labs; in that case also tick "Allow unencrypted NBD" for the pool in the console.

### Changed block tracking (CBT)

backdafuckup enables CBT on every disk it backs up. While CBT is enabled on a disk, **storage live migration** of that disk is not possible. When a disk is reset, for example after a host crash, the next backup reads it in full.

**Important for the first deployment:** incremental backups stay disabled until the bit order of the CBT bitmap has been verified on your platform with the lab test (see "Lab verification" below). Set `"Xen": { "CbtBitOrder": "MostSignificantBitFirst" }` (or `LeastSignificantBitFirst`), as determined by the test. Until then every backup is a full read; the data is still deduplicated in the repository.

## Installation (Linux)

```bash
dotnet publish src/Backdafuckup.Server -c Release -r linux-x64 --self-contained -o publish/linux
sudo useradd --system --home /var/lib/backdafuckup backdafuckup
sudo mkdir -p /opt/backdafuckup /etc/backdafuckup && sudo cp -r publish/linux/* /opt/backdafuckup/
sudo cp deploy/linux/appsettings.example.json /etc/backdafuckup/appsettings.json   # edit the connection string
sudo cp deploy/linux/backdafuckup.service /etc/systemd/system/                      # adjust ReadWritePaths
sudo systemctl daemon-reload && sudo systemctl enable --now backdafuckup
sudo journalctl -u backdafuckup | grep "setup token"
```

Then open `https://<server>:9555`, enter the setup token and create the administrator. The server uses a self-signed certificate. To use your own, configure `Kestrel:Certificates:Default` or a full `Kestrel:Endpoints` section.

## Installation (Windows)

```powershell
dotnet publish src/Backdafuckup.Server -c Release -r win-x64 --self-contained -o publish\win
Copy-Item deploy\windows\install-service.ps1 publish\win\
# As Administrator:
.\publish\win\install-service.ps1
```

Configuration goes in `%ProgramData%\backdafuckup\appsettings.json`.

## Data and backups of the server itself

| Path (Linux / Windows) | Content |
|---|---|
| `/var/lib/backdafuckup` / `%ProgramData%\backdafuckup` | Encryption key ring (`keys/`), HTTPS certificate, setup token |
| `/var/log/backdafuckup` | Log files (30 days) |
| PostgreSQL | Catalog |
| Repository directories | Backup data (self-describing) |

- **Key ring:** back up the `keys/` folder. Without it, the stored pool passwords must be re-entered.
- **Repositories:** they are self-describing. After a loss of the server or the database, add the repository path again on a new installation. The existing restore points are imported automatically.

## Lab verification

The opt-in lab tests run against a real pool. Use a test VM; they create and delete snapshots and a restored copy of the VM.

```bash
export BDFU_LAB_HOST=xcp-host.example.local       # pool master or member
export BDFU_LAB_USER=root
export BDFU_LAB_PASSWORD='...'
export BDFU_LAB_VM=<uuid of a small, running test VM>
export BDFU_LAB_TARGET_SR=<uuid of an SR for the restored copy>
export BDFU_LAB_WAIT_SECONDS=120                   # time for the VM to change some blocks
dotnet test --project tests/Backdafuckup.Lab.Tests --filter-trait "Category=Lab"
```

- **`Cbt_bit_order_spike`:** takes two snapshots, compares the real block differences with the CBT bitmap, and prints the bit order to configure. Make sure the VM writes some data during the wait, e.g. `dd if=/dev/urandom of=/tmp/x bs=1M count=50; sync`.
- **`Backup_and_restore_roundtrip`:** backs up the VM through NBD into a temporary repository, restores it as `<name>-bdfu-labtest` to the target SR, and compares SHA-256 hashes of the original snapshot and the restored disks.

## Required XAPI permissions

The connection account needs to create and delete snapshots, enable CBT, use NBD, create VDIs and import VMs. With RBAC, use **Pool Admin** (simplest) or **Pool Operator**. Enabling the NBD network purpose requires Pool Admin.
