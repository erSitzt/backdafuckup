# backdafuckup – Administrator Guide

## Requirements

| Item | Notes |
|---|---|
| Backup server | Linux (systemd) or Windows Server, x64. Must **not** run in dom0 of an XCP-ng host. A VM in the pool or a separate machine both work. |
| .NET | Either publish self-contained (no runtime needed) or install the ASP.NET Core 10 runtime. |
| PostgreSQL | 15 or later, for the catalog (jobs, restore points, history). |
| Repository storage | A directory on a volume mounted on the backup server, e.g. an iSCSI LUN from an HPE MSA. |
| XCP-ng / XenServer | XCP-ng 8.3 or XenServer 8, with a pool admin account for the connection. |
| NBD network | Recommended: a pool network with the NBD purpose that the backup server can reach. Without it, backups fall back to the slower HTTP export. |

### Disk transports

Each job has a **Disk transport** setting (job editor → Storage → Advanced):

| Setting | Behaviour |
|---|---|
| Automatic (default) | NBD if the pool offers a reachable NBD endpoint, otherwise the HTTP export, with a warning in the session log. |
| NBD only | Fails the VM if NBD cannot be used. |
| HTTP export only | Always uses the HTTP export. |

- **NBD** (TCP 10809, TLS): reads each disk over several parallel connections and, for incrementals, only the changed blocks.
- **HTTP export** (`export_raw_vdi` over the XAPI HTTPS port 443): needs no network setup. It uses one stream per disk and transfers the whole allocated disk on every backup, also for incrementals. The repository still stores only new data.

The session log shows how the transport was chosen:
- the pool networks with NBD purpose and the hosts' IP addresses on them;
- the result of `VDI.get_nbd_info`;
- every NBD connection attempt, with address, port, host, network, duration and error;
- the HTTP request and the format of the exported stream.

The HTTP export uses the dynamic VHD format by default, so only allocated blocks are transferred. If a VHD stream is ever rejected, `"Xen": { "HttpExportFormat": "Raw" }` exports every byte of the disk unprocessed. The lab test `Http_export_vhd_matches_raw` compares both formats on your pool.

On a 1 Gbit/s link, one HTTP export stream reached about 60 MB/s on a test pool, roughly half the line rate. backdafuckup's own processing was not the limit: the VHD parser handles several GB/s.

### Parallel processing

A job processes several VMs at the same time ("VMs processed in parallel", default 2), and within a VM several disks ("Disks per VM processed in parallel", default 2). Each disk is its own stream: one HTTP export, or the configured number of NBD connections.

Parallel streams help as long as the network has room. On a 1 Gbit/s link (about 110 MB/s of data), two HTTP export streams already reach the line rate; more streams only share it. For NBD, keep the total number of connections in mind: VMs × disks × "NBD connections per disk" should not exceed the 16 connections XAPI allows.

### Restore upload

Restores write each disk with `import_raw_vdi` over HTTPS to the pool master, or for local storage to the host that has it attached. By default the disk is sent as a dynamic VHD that contains only the 2 MiB blocks with data. Empty regions are neither transferred nor written. The session log names the target host and how much is sent, and the session page shows the restored and transferred bytes.

`"Xen": { "HttpImportFormat": "Raw" }` sends every byte of the disk instead. Use it if a host rejects the VHD upload. The lab test `Backup_and_restore_roundtrip` uses the format from `BDFU_LAB_IMPORT_FORMAT` (`Vhd` by default, or `Raw`).

### Enable NBD on a pool network

Backups read VM disks fastest over NBD (TCP 10809) with TLS:

```bash
xe network-list                                    # find the network UUID
xe network-param-add param-name=purpose param-key=nbd uuid=<network-uuid>
```

The backup server must reach that network on port 10809. Use `insecure_nbd` (no TLS) only in isolated test labs; in that case also tick "Allow unencrypted NBD" for the pool in the console.

If NBD connections fail with "Connection refused", run these commands on a host. They show which network has the NBD purpose, which IP each host has on it, and whether `xapi-nbd` listens:

```bash
xe network-list params=uuid,name-label,purpose
xe pif-list params=host-name-label,network-name-label,IP
systemctl status xapi-nbd
ss -ltnp | grep 10809
```

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

## Guest agent (application-consistent backups)

XCP-ng / XenServer removed the quiesced snapshot in 8.1, so a VM snapshot is crash-consistent: like a power failure. For application-consistent backups of Windows VMs (SQL Server, Active Directory, Exchange, other VSS-aware applications) install the **backdafuckup agent** in the VMs and switch on **Guest processing** in the job (Advanced → Guest processing).

**How it works**
1. Before the snapshot, the agent gathers the VSS writer metadata (volumes, writers, SQL databases with their files). It goes into the restore point as `guest-metadata`.
2. Right before `VM.snapshot` the agent starts a VSS snapshot set. The writers flush and freeze. Then our own VSS provider (`bdfu-vss.dll`) holds the writes and reports "frozen".
3. The server takes the VM snapshot and thaws the guest right away. The session log shows the freeze, snapshot and hold times and the writer states.
4. **10 s limit:** Windows releases held writes after **10 seconds** at the latest (`VSS_E_HOLD_WRITES_TIMEOUT`). Only the VM snapshot runs inside that window, so it must finish within 10 s. Otherwise the backup is crash-consistent and the session ends with a warning, or the VM fails if the job says so.
5. **Copy-only:** the backup is a VSS copy-only backup (`VSS_BT_COPY`). The SQL Server backup chain (differential base, log backups) of other backup products stays untouched. Logs are not truncated.

Restore points show their consistency ("application", or "crash" plus the reason) under *Backups & Restore*.

**Requirements**
- Windows Server 2016 or later, or Windows 10/11, for the .NET 10 build (self-contained, no runtime needed). Older systems such as Server 2012 R2 get the .NET Framework 4.8 build, which needs .NET Framework 4.8 installed.
- The VM reaches the backup server over HTTPS (port 9555). The agent connects outbound only; nothing has to be opened in the VM.
- The guest tools (XCP-ng or Citrix VM Tools) report the VM's IP addresses, which the push installation uses.
- The agent identifies its VM by the BIOS serial number, which XCP-ng sets to the VM UUID. No mapping is needed.

**Setup**
1. *Guest Agents → Settings*: enter the address under which the VMs reach the server, e.g. `https://backup.example.local:9555/`.
   - The agents pin the server's own certificate (SPKI SHA-256), so the self-signed certificate works.
   - With a certificate from `Kestrel:Endpoints`, Windows certificate validation applies. You can also configure pins with `Agent:ServerPins`.
2. **Push installation:** add an administrator account under *Windows Credentials*, select the VMs under *Guest Agents* and choose *Install agent*.
   - The server connects over **SMB 445** with NTLMv2 to the first reachable IP address of the VM.
   - It copies the agent to `C:\Program Files\backdafuckup Agent` (via `C$`, otherwise `ADMIN$`), creates the service **BackdafuckupAgent** (Local System, automatic start) and starts it.
   - If the .NET 10 build does not keep running, it replaces it with the .NET Framework 4.8 build.
   - A job with *Install the agent where it is missing* does the same during the backup.
3. **Without push** (group policy, software distribution): *Guest Agents → Download setup* gives a ZIP with both builds, `agent.json` and `Install-Agent.cmd`.
   - Run the script as administrator or Local System, e.g. as a computer startup script.
   - On a computer that already has the agent it returns at once (the agent updates itself); `--force` reinstalls.
   - The ZIP contains the enrollment secret: give only the computers that need it read access. *Renew secret* invalidates it for new installations.

**Accounts for the push installation**
- Domain accounts in the local Administrators group work directly: `DOMAIN\user` or `user@domain`.
- Local accounts: UAC filters network logons of local administrators. Only the built-in `.\Administrator` works without changes; other local accounts need `LocalAccountTokenFilterPolicy=1`, which weakens UAC.
- SMB signing is supported. If NTLM is blocked by policy, use the setup package.

**What the agent does on the guest**
- **Folder:** `%ProgramFiles%\backdafuckup Agent`, readable by SYSTEM and administrators only. It holds the program, `agent.json` (server address, pin, enrollment secret), `agent.key` (the agent's own key) and `agent.log`.
- **Service:** `BackdafuckupAgent`, with recovery set to restart. It registers the VSS provider "backdafuckup VSS provider" as COM+ application (Local System).
  - The provider only takes part in backdafuckup's own freeze. Other backup products keep using the Windows system provider.
- **Server connection:** a long poll to the server (one open HTTPS request). The server only offers the commands *info, prepare, freeze, thaw, abort*; no files and no commands are executed.
- **Self-update:** after a server update the agent downloads the new files (SHA-256 checked) and restarts. This never happens during a backup.
- **Remove:** `"%ProgramFiles%\backdafuckup Agent\backdafuckup-agent.exe" uninstall` removes the service, VSS provider and folder.

**Endpoint protection:** a push installation looks like lateral movement to EDR products (file copied to an admin share, service created remotely). See [Microsoft Defender for Endpoint](defender.md).

## Data and backups of the server itself

| Path (Linux / Windows) | Content |
|---|---|
| `/var/lib/backdafuckup` / `%ProgramData%\backdafuckup` | Encryption key ring (`keys/`), HTTPS certificate, setup token |
| `/var/log/backdafuckup` | Log files (30 days) |
| PostgreSQL | Catalog |
| Repository directories | Backup data (self-describing) |

- **Key ring:** back up the `keys/` folder. Without it, the stored pool passwords must be re-entered.
- **Repositories:** they are self-describing. After a loss of the server or the database, add the repository path again on a new installation. The existing restore points are imported automatically.

## Session statistics

Every backup session shows live statistics for the session, for each VM and for each disk. They are kept in the history. Each disk also writes a summary line to the session log.

| Metric | Meaning |
|---|---|
| Processed | The part of the disks that was dealt with. It splits into the four categories below, which together add up to Processed. |
| Unchanged | Not processed, because change tracking reports no change since the previous restore point. |
| Empty | All-zero data. It is not stored. |
| Already stored | Data that already was in the repository, from this backup, an earlier one or another VM. |
| New | New data before compression. |
| Read | Bytes received from the source. With NBD incrementals this is only the changed data. With the HTTP export it is the whole allocated disk. |
| Transferred | Bytes written to the repository, after compression. |
| Processing rate | Processed divided by the transfer time. For the session, the session run time is used instead. |
| Dedupe | (Already stored + New) / New |
| Compression | New / Transferred |
| Bottleneck | The busiest stage, with the share of time it was busy. **Source** is reading from the hypervisor, **Processing** is hashing and compression, **Target** is writing to the repository. |

## Lab verification

The opt-in lab tests run against a real pool. Use a test VM; they create and delete snapshots and a restored copy of the VM.

```bash
export BDFU_LAB_HOST=xcp-host.example.local       # pool master or member
export BDFU_LAB_USER=root
export BDFU_LAB_PASSWORD='...'
export BDFU_LAB_VM=<uuid of a small, running test VM>
export BDFU_LAB_TARGET_SR=<uuid of an SR for the restored copy>
export BDFU_LAB_WAIT_SECONDS=120                   # time for the VM to change some blocks
export BDFU_LAB_TRANSPORT=Automatic                # or NbdOnly / HttpExportOnly
BDFU_LAB_RUN=1 dotnet test --project tests/Backdafuckup.Lab.Tests --filter-trait "Category=Lab"
```

The lab tests are skipped unless `BDFU_LAB_RUN=1` is set for the run, so that a solution-wide `dotnet test` never touches a real pool.

Instead of exporting the variables, you can put the same `KEY=VALUE` lines into `~/.config/backdafuckup/lab.env` (or the file named by `BDFU_LAB_ENV_FILE`). Then the password is not on the command line or in the shell history; protect the file with `chmod 600`. Environment variables take precedence over the file.

To run a single test, add `--filter-method "*Cbt_bit_order_spike"`; add `--output detailed` to see the test output (progress, results) also for passed tests. With the HTTP export, each test reads the whole allocated disk once or twice, so a small VM keeps the run short. If XAPI still reports a snapshot disk in use after the backup, the tests wait for it like the service and print how long it took.

- **`Cbt_bit_order_spike`:** takes two snapshots, compares the real block differences with the CBT bitmap, and prints the bit order to configure. Make sure the VM writes some data during the wait, e.g. `dd if=/dev/urandom of=/tmp/x bs=1M count=50; sync`.
- **`Backup_and_restore_roundtrip`:** backs up the VM with the selected transport into a temporary repository, restores it as `<name>-bdfu-labtest` to the target SR, and compares SHA-256 hashes of the restore point and the restored disks (read back with a raw HTTP export).
- **`Http_export_vhd_matches_raw`:** exports a snapshot of the VM as VHD (parsed by backdafuckup) and as raw, and checks that both give the same data. It works without NBD.

## Required XAPI permissions

The connection account needs to create and delete snapshots, enable CBT, use NBD or the HTTP export, create VDIs and import VMs. With RBAC, use **Pool Admin** (simplest) or **Pool Operator**. Enabling the NBD network purpose requires Pool Admin.
