# Plan: tape device layer (SSC/SMC, local and through a tape agent)

Part A of the tape work.
- **Comes after:** `../2026-11-16-1804-repository-pack-files`.
- **Followed by:** `../2026-11-16-1804-backup-to-tape`, which builds media pools, jobs and restores on this layer.
- **Ends at:** a library the console can drive, and a block pump that tape jobs write to and read from.

## Context

**User decisions (2026-11-16)**
- **Hardware:** the production library is an HPE MSL6480 with LTO-9 drives. The code stays vendor-neutral.
- **Devices:** libraries with a changer, and standalone drives where the operator changes tapes when the console asks.
- **Access:** directly on the server (Windows SCSI pass-through, Linux SG_IO) and through a Linux tape agent on a host where a library is attached.
- **Sources:** only the public T10 standards (SPC-4, SSC-3/4/5, SMC-3) and the public HPE/IBM SCSI references.
  - No mtx, mt or sg3_utils at runtime.
  - No vendor SDKs.
  - No vendor drivers required.
- **Encryption:** software or LTO hardware encryption, chosen per media pool.
- **WORM:** cartridges are never overwritten.
- **Lab:** Holo VTL 1.0.7 (MIT) on XCP-ng, built with OpenTofu. Profile `hpe-msl6480` with `hpe-ultrium-9-scsi`.

**What Holo emulates** (from its public sources)
- **Profiles:**
  - changers: `hp-msl2024`, `hp-msl4048`, `hp-msl8096`, `hp-msl6000-series`, `hpe-msl3040`, `hpe-msl6480`;
  - the only LTO-9 drive is `hpe-ultrium-9-scsi` (INQUIRY `HPE` / `Ultrium 9-SCSI`).
- **Drive commands:**
  - READ/WRITE 6/16, WRITE FILEMARKS, SPACE 6/16, LOCATE;
  - READ POSITION (short, long, extended), READ BLOCK LIMITS (max 8 MiB);
  - mode pages compression, device configuration and medium configuration;
  - LOG SENSE pages 0x0C, 0x17, 0x2E and 0x31;
  - READ/WRITE ATTRIBUTE, PERSISTENT RESERVE.
  - A WORM overwrite is refused with 07/30/0C.
- **Changer commands:**
  - READ ELEMENT STATUS with VolTag (32-byte, not padded to 8), DVCID and AVolTag;
  - MOVE MEDIUM, EXCHANGE MEDIUM;
  - INITIALIZE ELEMENT STATUS (ignores ranges).
  - Touching an IE element raises UA 28/01.
- **Missing in Holo:**
  - SECURITY PROTOCOL IN/OUT, so no hardware encryption;
  - early-warning sense: only the EOP bit of READ POSITION, then VOLUME OVERFLOW 0D/00/00 with EOM;
  - ILI and the FILEMARK bit: a filemark comes as 00/00/01, blank check as 08/00/05;
  - TapeAlert entries: the page is always empty.
- **Install and API:**
  - `scripts/install.sh` always installs the latest release, so pinning a version needs the release tarball and an offline install.
  - The REST API is under `/v1/{libraries,drives,cartridges,targets/publications}` with `X-HOLO-API-Key`.

## Decisions

1. **One new project, `src/Backdafuckup.Tape`** (net10.0, `AllowUnsafeBlocks`).
   - Folders `Scsi` (CDBs, sense, transports) and `Devices` (drive, changer, block pump). Part B adds `Format` and `Crypto`.
   - Native calls are guarded with `[SupportedOSPlatform]` (CA1416 is an error).
2. **The agent works at the SCSI level; the logic stays on the server.**
   - The agent forwards CDBs and runs the block pump.
   - Sense handling, WORM guards, encryption and inventory exist once, on the server.
3. **Variable-block mode, buffered mode on, SEW=1.**
   - Block size: 256 KiB by default, settable per pool up to 1 MiB, then capped at READ BLOCK LIMITS and the transport maximum. It is recorded in the tape label.
   - 256 KiB is the default limit of the Microsoft iSCSI initiator, and every host can read it back, which matters for the recovery command.
   - Drive compression is off; the data is already zstd.
4. **Reads use SILI=1,** and the block length comes from the transport's residual (sg `resid`, Windows `DataTransferLength`). ILI/INFORMATION is only a fallback.
5. **End of tape.**
   - Early warning is seen as EOM in the sense data (00/00/02, 00/00/07), or as the EOP bit of READ POSITION, polled every 1 GiB (Holo needs this).
   - VOLUME OVERFLOW without an early warning is reported to the caller, which treats the current file as torn (Part B).
6. **No blind retries** of WRITE, WRITE FILEMARKS, SPACE or LOCATE. After a transport error or UA 06/29, READ POSITION is compared with the expected position; anything else fails.
7. **WORM.**
   - Detected through MODE SENSE medium configuration (WORMM), MAM attribute 0x0408 = 0x80, and the barcode as a hint only.
   - Erase, relabel and any write before end of data are refused before anything reaches the drive.
8. **Hardware encryption, application-managed (AME).**
   - Capabilities from SPIN 0x20/0x0010.
   - Set with SPOUT 0x0010: AES-256-GCM (0x00010014), CKOD=1, scope LOCAL, the key id in U-KAD.
   - Status from SPIN 0x0020/0x0021.
   - Keys are never logged and are zeroed after use. Through the agent they travel only on the TLS control socket.
   - The MSL6480 must be set to application-managed encryption.
9. **Devices are identified by serial (VPD 0x80/0x83), never by device node.** DVCID maps drive elements to drives; without it the user maps them once by hand.
10. **The tape agent is Linux first.** The same code builds for Windows; a Windows package comes on demand.
11. **A changer and its drives sit on one host.** Libraries split across hosts come later.

## SCSI commands

| Set | Command | Purpose |
|---|---|---|
| SPC | TEST UNIT READY 00, REQUEST SENSE 03 | readiness, becoming-ready polling |
| | INQUIRY 12 with VPD 00/80/83/B0 | type, serial, designators, WORM capability |
| | MODE SENSE/SELECT(10) 5A/55 (6-byte as fallback) | header (WP, medium type, buffered mode, block length); pages 0x0F compression, 0x10 SEW, 0x10/01 PEW, 0x1D WORMM; changer 0x1D element addresses, 0x1F capabilities |
| | LOG SENSE 4D | 0x2E TapeAlert, 0x17 volume statistics, 0x0C sequential access, 0x31 capacity fallback |
| | READ/WRITE ATTRIBUTE 8C/8D | MAM read (0000/0001/0400/0401/0408); write 0800–0803 (vendor, application, version, text label), 0806 barcode, 0808 pool |
| | SECURITY PROTOCOL IN/OUT A2/B5 | protocol 00 (list), 20 (tape encryption) |
| | REPORT SUPPORTED OPERATION CODES A3/0C with RCTD | the drive's own timeouts |
| | PREVENT ALLOW MEDIUM REMOVAL 1E | lock the drive during a job; release the mail slots |
| SSC | READ BLOCK LIMITS 05, REPORT DENSITY SUPPORT 44 | maximum block size; generation and writability |
| | READ(6) 08 / WRITE(6) 0A, 16-byte 88/8A | variable blocks |
| | WRITE FILEMARKS 10 | filemarks; count 0 flushes |
| | SPACE(16) 91, LOCATE(16) 92, READ POSITION 34 | append at end of data, seek, position checks, EOP |
| | REWIND 01, LOAD UNLOAD 1B | with IMMED, then TEST UNIT READY polling |
| | ERASE 19 (short only) | end of data at the beginning of tape; never a long erase |
| SMC | READ ELEMENT STATUS B8 with VolTag, DVCID, per element type, in chunks | inventory |
| | MOVE MEDIUM A5, INITIALIZE ELEMENT STATUS 07/37 | moves; full rescan on demand |

**Sense decoding**
- Fixed (70/71) and descriptor (72/73) formats; the FILEMARK, EOM and ILI bits; INFORMATION.
- Every message names the barcode and the drive:

  | Sense | Meaning |
  |---|---|
  | 02/3A | no medium |
  | 02/04/01 | becoming ready, so poll |
  | 02/04/02 | needs LOAD |
  | 02/30/03 | cleaning cartridge |
  | 06/28, 06/29 | medium may have changed or reset, so read the state again |
  | 07/27 | write-protected |
  | 07/30/0C | WORM |
  | 08/00/05 | end of data |
  | 0D | volume overflow |
  | 03/11, 03/0C, 03/30, 03/31, 03/53 | medium errors |
  | 04 | hardware error |
  | 05/3B/0D, 05/3B/0E | destination full or source empty, so re-inventory |
  | 05/53/02 | removal prevented, so ALLOW and retry |
  | 05/74 | encryption |

**Timeouts** come from RCTD when offered. Otherwise:
- status 60 s, READ/WRITE 20 min, MOVE 15 min;
- SPACE, LOCATE and REWIND 3 h;
- LOAD is polled up to 3 h, because a new LTO-9 cartridge calibrates on its first load. The console says so.

## Transports

**Interface:** `IScsiDevice.ExecuteAsync(ScsiCommand) → ScsiResult(status, sense, residual)`.
- `ScsiTransportException` is kept apart from CHECK CONDITION.
- Buffers are 4 KiB-aligned native memory (`NativeMemory.AlignedAlloc`), one ring per drive session.
- A tracing decorator logs CDBs and sense, never SPOUT or WRITE ATTRIBUTE data.

**Linux**
- **Open:** `SG_IO` (0x2285) on `/dev/sgN`, opened `O_RDWR|O_NONBLOCK|O_EXCL`. EBUSY is reported as "in use by another process".
- **`sg_io_hdr`:** explicit layout (88 bytes on LP64, asserted by a test). The timeout is in ms; `host_status` and `driver_status` become transport errors.
- **Transfer limit:** from `BLKSECTGET` and `SG_GET_SG_TABLESIZE`; `SG_SET_RESERVED_SIZE` is set to the block size.
- **Discovery:** reads sysfs without opening devices: `/sys/class/scsi_generic/sg*/device/{type,vendor,model,vpd_pg80,vpd_pg83}`, types 1 and 8 only, plus the iSCSI target name for display.
- **Never `/dev/st*`:** closing it rewinds the tape.
- **Access:** group `tape`, not root. `deploy/linux/backdafuckup.service` gains `SupplementaryGroups=tape`.

**Windows** (plain DeviceIoControl, no SetupAPI or CIM)
- **Discovery:** `IOCTL_SCSI_GET_INQUIRY_DATA` on `\\.\Scsi0:` … `\\.\Scsi63:` lists every LUN with its type and a claimed flag. For claimed LUNs, `IOCTL_SCSI_GET_ADDRESS` on `\\.\TapeN` / `\\.\ChangerN` finds the class device.
- **I/O:** `IOCTL_SCSI_PASS_THROUGH_DIRECT` (56-byte struct on x64, sense offset asserted by a test; the timeout is in seconds).
  - A claimed device goes through its class handle, opened exclusively.
  - An unclaimed device goes through the adapter handle with Path/Target/Lun.
- **Drivers:** without vendor drivers, LTO drives stay unclaimed and work through the adapter. With HPE or IBM drivers they work through `\\.\TapeN`. Adapter pass-through to a LUN that the in-box changer driver claimed is checked in the lab.
- **Limits:** `IOCTL_STORAGE_QUERY_PROPERTY(StorageAdapterProperty)` gives min(MaximumTransferLength, (MaximumPhysicalPages−1)·4 KiB) and AlignmentMask.
- **Rights:** the service runs as LocalSystem.

## Library model

- **Entities:**
  - `TapeHost`: this server or an agent.
  - `TapeLibrary`: changer serial, or none for a standalone drive.
  - `TapeDrive`: serial and element address.
  - `TapeCartridge`:
    - barcode, an opaque string of up to 32 characters;
    - MAM serial, type (data, WORM or cleaning) and generation;
    - location and state: free, in use, full, foreign or unknown;
    - used and remaining capacity, label id, key id.
- **Elements** come from mode page 0x1D. Cartridges with `CLN*` barcodes or the MAM type "cleaning" are never offered to jobs.
- **Locks:**
  - one lease per drive (FIFO, persisted so the console shows the holder, cleared at server start);
  - one robotics lock per library;
  - the OS-level exclusive open underneath.
- **Load:** ALLOW → MOVE → poll TEST UNIT READY (UA, becoming ready, LOAD on 02/04/02) → MAM and label.
- **Unload:** UNLOAD → MOVE back to the source slot (SVALID) or a free slot.
- **Inventory** runs:
  - when a library is added, and on demand;
  - after each move, and after UA 28/01;
  - as a mail-slot poll every 30 s, which shows "n cartridges in mail slots → Import" (one click, not automatic).
- **Standalone drive:** a request becomes an operator prompt. The drive is polled with TEST UNIT READY every 10 s, and the cartridge is identified by its label or MAM.

## Tape agent

- **Program:** `src/Backdafuckup.TapeAgent`.
- **Unit:** `deploy/linux/backdafuckup-tape-agent.service` with its own user, `SupplementaryGroups=tape`, `DevicePolicy=closed` and `DeviceAllow=char-sg rw`.
- **Enrollment** and connect token as for the cluster agents.
  - The token is protected by `CredentialProtector` and compared in fixed time.
  - The agent pins the server with `CertificatePinning` and reconnects with back-off.
- **Control:** a WebSocket at `/api/agent/tape-hosts/{id}/control`.
  - hello with the devices found in sysfs;
  - `scsi` (CDB, data ≤ 1 MiB, timeout) and `rescan`.
  - The agent accepts commands only for the type 1 and type 8 devices it reported itself.
- **Data:** one WebSocket per transfer with a one-time token. Binary `BDFUTAP1`, in the style of the mover stream.
  - Server to agent: blocks, filemarks, end.
  - Agent to server: acks (blocks, position, early warning), errors with sense, heartbeats.
  - At most 64 MiB unacknowledged; TCP gives the backpressure. Reads work the other way round.
- **Local drives** run the same `TapeBlockPump` in-process, so `ITapeHost.OpenDrive()` looks the same for local and agent libraries.
- **Monitoring:** the gauge `bdfu_tape_agent_connected{agent}` and the alert TapeAgentDisconnected.

## FakeTapeLibrary

`tests/Backdafuckup.Testing/Tape/FakeTapeLibrary.cs` and `FakeScsiDevice : IScsiDevice` parse the real CDBs. Runs on both CI OSes.
- **Model:**
  - unusual element addresses; barcodes of 6, 8 and 9 characters;
  - capacity with an early-warning zone and PEW;
  - blocks, filemarks and end of data;
  - WORM, write protect, MAM and compression;
  - encryption state (a wrong key gives the right sense);
  - cleaning cartridges.
- **Injection:**
  - a UA queue, "becoming ready" for N polls;
  - chosen sense codes and transport drops;
  - a `HoloQuirks` mode: no early-warning sense, 0D/00/00, no ILI or FILEMARK bit.

## Console (libraries)

- **Entry:** *Backup Infrastructure → Tape*, with the Libraries tab. The exact layout follows the variant the user picks from the mockups (see `shape.md`).
- **Library list:**
  - host (this server, or an agent with its online state) and model;
  - drives busy/total, slots used/total, mail slots, warnings.
- **Add library:** pick a host, scan, check the changers with their mapped drives and the standalone drives, give a name.
- **Tape agents:** create a token, copy the install command, see the online state.
- **Library page:**
  - drive cards: empty, loaded (barcode), writing (link to the job), needs cleaning, TapeAlert flags;
  - cartridge actions: Inventory, Load, Unload, Import, Export, Label, Clean drive.
  - *Mark free* and *Erase* need the barcode typed in and are refused for WORM.

## Tasks

1. **SCSI core:** CDB builders, `SenseData`, page parsers, `ScsiCommandRunner` (timeouts, UA policy, no blind retries).
   - Tests: byte patterns from the standards' tables, both sense formats, Holo sense samples.
2. **`FakeTapeLibrary`.**
   - Tests: the fake against task 1.
3. **Drive, changer and block pump:** label and MAM, WORM guards, hardware encryption, TapeAlert and statistics.
   - Tests on the fake: a full tape with and without early-warning sense; overflow; filemark and end-of-data reads; WORM refusals; encryption with a wrong key; UA after load; mail slots; cleaning cartridges skipped; DVCID mapping and its fallback.
4. **Linux transport.**
   - Tests: struct layout; a sysfs fixture tree (`tests/fixtures/sysfs-tape`) including renumbering after an iSCSI re-login; a `Lab`-trait test.
5. **Windows transport.**
   - Tests: struct layout; `SCSI_ADAPTER_BUS_INFO` fixture buffers; a `Lab`-trait test.
6. **Server model:** entities and migration, local `TapeHosts`, `TapeLibraries`, locks, inventory, `/api/tape/*`.
   - Tests: `ApiIntegrationTests.Tape.cs` with the fake; two jobs competing for one drive.
7. **Tape agent:** protocol, endpoints, program, `scripts/build-tape-agent.sh`, systemd unit.
   - In-process tests against the test server with the fake: inventory; a 2 GiB write and read with a checksum; early warning mid-transfer; a disconnect (offline, the transfer fails); a bad token; a device the agent did not report.
8. **Console:** library list, Add library, library page, tape agents.
   - Tests: API tests for every action, including the refusals.
9. **Telemetry and docs:** metrics, alerts, the admin guide (rights, drivers, block size and the iSCSI limit, AME on the MSL6480).
10. **`deploy/vtl/`:** its own OpenTofu root.
    - `versions.tf`: provider `vatesfr/xenorchestra` pinned.
    - `variables.tf`:
      - the SR is found by tag `bdfu-lab`, as in `deploy/lab`;
      - `holo_version` = 1.0.7 and the tarball's sha256;
      - profile `hpe-msl6480` with `hpe-ultrium-9-scsi`;
      - `drive_count` 1–4, 24 slots, 4 mail slots, 20 cartridges of 2 GiB, prefix `BDF`;
      - `allowed_initiators` and a sensitive `api_key`.
    - `main.tf`: one Ubuntu 24.04 VM with a data disk. cloud-init installs Holo offline from the pinned tarball, then an idempotent setup script creates pool, library, drives, ACL and cartridges through the REST API.
    - `outputs.tf`: portal, target IQNs, `api_key` (sensitive).
    - `README.md` repeats the safety rules of `deploy/lab`. State and tfvars are git-ignored. Placeholders only.

## Lab

**iSCSI login**
- **Windows server:** `New-IscsiTargetPortal`, then `Connect-IscsiTarget -IsPersistent $true`.
- **Linux agent host:** `iscsiadm -m discovery -t st -p <portal>`, then `iscsiadm -m node -l`.

**Runbook rows**

| Row | Check |
|---|---|
| T1 | `tofu apply`: changer and 2 drives published; 20 cartridges `BDF000L09…` |
| T2 | Windows without vendor driver: scan by serial, drives mapped by DVCID, 9-character barcodes, 4 empty mail slots |
| T3 | Load, unload, move, UA handling |
| T4 | Label: MAM and the label at the beginning of tape read back |
| T5 | 4 GiB write and read with checksum; 1 MiB blocks against the 256 KiB iSCSI limit give a clear message |
| T6 | A 2 GiB cartridge filled: EOP seen, overflow handled, nothing lost, BLANK CHECK at end of data |
| T7 | WORM cartridge: detected; appending works; erase and relabel refused |
| T8 | Import and export through the mail slots, using the Holo API |
| T9 | A `CLN` cartridge is never offered |
| T10 | iSCSI logout and login: sg devices renumbered, everything found again by serial; a write during the drop fails clearly |
| T11 | Linux agent: enroll, inventory, T5 through the agent; killed → offline and alert; reconnects |
| T12 | Standalone drive (drive target only): operator prompt; load through the Holo API → identified |
| T13 | Two actions on one drive: the second waits; the console names the holder |
| T14 | Encryption on Holo: shown as "not supported by this drive" |
| T15 | Linux server with local SG_IO: T2–T6 |

**Not testable on Holo:** hardware encryption, TapeAlert flags, early-warning sense, ILI, LTO-9 calibration and timings, cleaning, firmware quirks, SAS/FC limits. They are row T31, open until there is time on the real library. Rows T16–T30 (jobs and restores) are in `../2026-11-16-1804-backup-to-tape`.

## Risks

- **Adapter pass-through** when the Windows in-box driver claims the changer (checked in T2).
- **Block-size portability:** a tape written with 1 MiB blocks cannot be read on a host limited to 256 KiB. The message says so; the admin guide explains it.
- **Holo 1.0.7** REST bodies and MAM attribute ids are checked during task 10.
