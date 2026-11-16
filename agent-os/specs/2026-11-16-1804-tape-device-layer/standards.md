# Standards for the Tape Device Layer

No standards are indexed in `agent-os/standards/` yet. The conventions of the earlier specs apply:
- write-once data and versioned formats (foundation);
- errors that name hosts, devices and paths;
- remote components that dial out with pinned TLS (the Kubernetes cluster agent).

## Additions for this spec

### Data on tape is never put at risk by the product
- **Moving the tape:** WRITE, WRITE FILEMARKS, SPACE and LOCATE are never retried blindly. After an error, the position is read and compared.
- **WORM:** cartridges are never erased, relabelled or written before end of data. The check happens before a command is sent.
- **Foreign tapes:** cartridges with data the product did not write are never used unless the user erases them and types the barcode to confirm.
- **Erase** is short only.
- **Cleaning cartridges** are never offered to jobs.

### Devices are found by identity
- Serial numbers and designators (VPD 0x80/0x83) identify changers and drives, never device nodes or bus numbers.

### Only public interfaces
- **Sources:** T10 SPC/SSC/SMC and public vendor SCSI references.
- **Not at runtime:** no vendor SDKs, vendor drivers, mtx, mt or sg3_utils.
- **Holo VTL** is a test target only, and is not shipped.

### Least privilege
- **Linux:** access through group `tape`, never root. The tape agent's unit allows only `char-sg` devices.
- **Agent commands:** the agent accepts commands only for the devices it reported itself.
- **Keys** for hardware encryption are never logged. They travel only inside TLS and are zeroed after use.

### Messages name the device
Every tape message names the library, the drive (serial), the barcode and the host (this server or which agent).

### Lab safety
- `deploy/vtl/` only creates. It uses the `bdfu-lab` SR tag and its own state, and is never destroyed without asking.
- Secrets (API key, credentials) are never committed or printed. Committed files use placeholders only.
