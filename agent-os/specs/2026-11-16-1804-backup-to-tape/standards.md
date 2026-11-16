# Standards for Backup to Tape

No standards are indexed in `agent-os/standards/` yet. These apply:
- the conventions of the earlier specs:
  - write-once restore points;
  - the manifest as the commit point;
  - versioned formats;
  - errors that name hosts, devices and paths;
- the standards of `../2026-11-16-1804-tape-device-layer/standards.md`:
  - tape data never put at risk;
  - devices found by identity;
  - public interfaces only.

## Additions for this spec

### A copy exists only when it is complete
- **Order:** chunks first, then the restore-point file, then a synchronizing filemark, then the database row.
- **Torn files:** a file without its trailer is ignored by every reader.

### Tapes describe themselves
- **Every tape** has a label and ends with a catalog.
- **The last catalog of a set** describes the whole set.
- **The format is documented** in `docs/tape-format.md`. Readers refuse newer versions, and golden images keep version 1 readable.
- **The recovery command** uses the same reader code as the server.

### Append only
- Tapes are only appended to. A rewritable tape is relabelled at its beginning only after its set expired.
- WORM tapes are never reused, erased or relabelled.

### Keys are the user's
- **Passphrases** are never stored or logged. The server keeps only the protected key-encryption key.
- **The key file** is offered once.
- **Without the passphrase or the key file the data is gone,** and the console says so where encryption is chosen.
- **Clear text on tape:** with software encryption, nothing identifies VMs, chunks or manifests except the label (key fingerprint, set, pool name).

### Imports never collide
- Restore points brought back from tape get new ids and the target repository's id.
- Job retention never deletes them.

### Messages name tapes and places
Session logs and operator requests name the drive, the barcode, the set and sequence, the slot or mail slot, and the library or agent.
