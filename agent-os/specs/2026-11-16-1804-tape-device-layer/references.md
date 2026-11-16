# References for the Tape Device Layer

## Code in this repository

### Remote components and channels
- **Cluster agent:** `src/Backdafuckup.Server/Clusters/ClusterAgents.cs` (connect token, fixed-time compare, hello, gauge `bdfu_k8s_agent_connected`) and `ClusterAgentEndpoints.cs` (control WebSocket); `src/Backdafuckup.Mover/ClusterAgent.cs` (reconnect loop).
- **Movers:** `src/Backdafuckup.Server/Movers/MoverChannels.cs` and `MoverEndpoints.cs` (one-time token channels); `src/Backdafuckup.Core/DataMover/MoverStream.cs` (record framing with backpressure).
- **Guest agent:** `src/Backdafuckup.Server/Agents/AgentEndpoints.cs` and `AgentSettingsStore.cs` (enrollment, server pins).
- **TLS pinning:** `src/Backdafuckup.Core/Tls/CertificatePinning.cs`.

### Native code and platforms
- **`LibraryImport` patterns:** `src/Backdafuckup.Storage/AtomicFile.cs`, `FileSystemSync.cs`.
- **Linux-only classes:** `src/Backdafuckup.Mover/VolumeFiles.cs` with `[SupportedOSPlatform("linux")]`.
- **Windows iSCSI initiator cmdlets:** `src/Backdafuckup.Agent/WindowsPublishHost.cs`.

### Server and console
- **Entities:** `src/Backdafuckup.Server/Data/Entities.cs`.
- **Endpoints:** `src/Backdafuckup.Server/Api/InfrastructureEndpoints.cs` (the repository CRUD pattern).
- **Navigation:** `src/Backdafuckup.Web/Layout/NavMenu.razor` ("Backup Infrastructure").
- **List page pattern:** `src/Backdafuckup.Web/Pages/Repositories.razor`.
- **Metrics and alerts:** `src/Backdafuckup.Server/Telemetry/BackupMetrics.cs`, `StateMetrics.cs`; `deploy/monitoring/backdafuckup.rules.yml`, `prometheusrule.yaml`.

### Deployment and lab
- **Linux unit:** `deploy/linux/backdafuckup.service`.
- **Windows service install:** `deploy/windows/install-service.ps1`.
- **Lab rules to reuse:** `deploy/lab/` (OpenTofu, SR tag `bdfu-lab`, own state).
- **Mover build:** `scripts/build-mover.sh` (template for `build-tape-agent.sh`).

### Tests
- `tests/Backdafuckup.Testing/` (shared fakes).
- `tests/Backdafuckup.Server.Tests/ApiIntegrationTests*.cs` (in-process agents against the test server).
- `LabTests.cs` (`Lab` trait).

## External references (public documentation)

- **T10 standards:** SPC-4 (INQUIRY, MODE, LOG, READ/WRITE ATTRIBUTE, SECURITY PROTOCOL), SSC-3/4/5 (tape), SMC-3 (changer). Drafts are publicly available.
- **Vendor references:**
  - the HPE StoreEver LTO Ultrium SCSI interface reference (LTO-9);
  - the HPE MSL6480 user and SCSI reference;
  - the IBM LTO SCSI reference (TapeAlert, MAM).
- **Linux:**
  - `sg(4)` and the SG_IO `sg_io_hdr` interface;
  - sysfs `scsi_generic`, `vpd_pg80`, `vpd_pg83`;
  - `st(4)`, only to know why it is avoided (rewind on close).
- **Windows:**
  - `IOCTL_SCSI_PASS_THROUGH_DIRECT`, `IOCTL_SCSI_GET_INQUIRY_DATA`, `IOCTL_SCSI_GET_ADDRESS`;
  - `IOCTL_STORAGE_QUERY_PROPERTY` (adapter limits);
  - the iSCSI initiator cmdlets (`New-IscsiTargetPortal`, `Connect-IscsiTarget`).
- **Holo VTL** (MIT, public sources and releases): emulation profiles, supported commands, REST API. Test target only.
- **Proxmox Backup Server** (AGPL-3.0) documentation on tape: drive and changer handling, identity by serial. Read for behaviour only.
- **Bareos and Bacula** documentation on block sizes and end-of-medium handling. Read for behaviour only.

Nothing is copied from purchased products.
