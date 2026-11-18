# References for the VMware vSphere Provider

## Similar implementations in this repository

### XCP-ng provider
- **Location:** `src/Backdafuckup.Xen` (`XenProvider.cs`, `XenBackupSession.cs`, `XenRestoreSession.cs`, `XenInventory.cs`, `XenOptions.cs`).
- **Relevance:** the provider and session pattern this spec repeats for vSphere.
- **Key patterns:**
  - snapshot gate and free-space check before the freeze;
  - freeze/thaw around the snapshot (moves to Core);
  - CBT tokens;
  - artifact kinds and crash cleanup;
  - the restore sequence.

### Kubernetes provider
- **Location:** `src/Backdafuckup.Kubernetes` (`KubernetesProvider.cs`, `KubernetesSession.cs`).
- **Relevance:** the second kind added to the core.
- **Key patterns:** optional interfaces on the session, checked with casts, for kind-specific features without changing the Core interfaces. Kind-specific restore validation.

### Linux tape agent
- **Location:** `src/Backdafuckup.TapeAgent`, `src/Backdafuckup.Tape/Agent/TapeAgentProtocol.cs`, `src/Backdafuckup.Server/Tape/TapeAgents.cs`, `scripts/build-tape-agent.sh`, `deploy/linux/backdafuckup-tape-agent.service`.
- **Relevance:** the model for the proxy agent.
- **Key patterns:**
  - outbound WSS with a connect token and SPKI pins;
  - length-prefixed JSON header plus binary framing (moves to a generic `AgentSocketFraming`);
  - a thin agent, with the logic on the server.

### Windows guest agent
- **Location:** `src/Backdafuckup.Agent*`, `src/Backdafuckup.Server/Agents/*`.
- **Relevance:** enrollment, capabilities and hash-based self-update, which the proxy agent adopts. Also VM matching by SMBIOS UUID (`AgentService`) and guest processing (`AgentQuiescer`, `GuestProcessingService`).

### Cluster tunnel and mover
- **Location:** `src/Backdafuckup.Core/DataMover/ClusterTunnel.cs`, `src/Backdafuckup.Server/Movers/*`, `src/Backdafuckup.Mover/*`.
- **Relevance:** a WebSocket tunnel per stream with one-time tokens, and a remote process that reads block devices on Linux.

### NBD client
- **Location:** `src/Backdafuckup.Nbd` (`NbdClient`, `NbdConnectionPool`).
- **Relevance:** the server reads hot-added disks through NBD tunnels. A new `NbdServer` is written from the same public protocol spec.

### Data mover pipeline
- **Location:** `src/Backdafuckup.DataMover/DiskBackupPipeline.cs`, `src/Backdafuckup.Core/DataMover/Abstractions.cs`.
- **Relevance:** `IDiskReader` reads only the dirty chunks. `ChangedExtents.Allocated` and `IExtentDiskWriter` are new.

## External references (public)

- **VMware vSphere Web Services API reference (vim25):** the public online reference, and the WSDL that every vCenter serves at `/sdk/vimService.wsdl`.
- **VMware vSphere documentation:** "Changed Block Tracking", "Virtual Disk Development" (concepts only, no VDDK), "Guest Operations", "HTTP access to datastore files".
- **NBD protocol:** `doc/proto.md` of the NBD project (public domain).
- **govmomi / vcsim** (Apache-2.0): a behaviour reference and an optional CI simulator. No code copied.
- **Our analyses:** `reports/VMware vSphere integration access.md`, `research_notes/VMware vSphere integration access/`.
- **Veeam Help Center** (public web pages): ideas for proxy selection and workflows only.
