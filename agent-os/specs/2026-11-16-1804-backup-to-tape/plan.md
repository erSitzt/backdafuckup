# Plan: backup to tape, on-tape format, restore from tape

Part B of the tape work.
- **Built on:**
  - `../2026-11-16-1804-repository-pack-files` (`IChunkLocator`: chunks in pack order with their stored sizes);
  - `../2026-11-16-1804-tape-device-layer` (drives, changers, block pump, fake library, VTL lab).

## Context

**What exists.** A restore point on disk is:
- `manifest.json`;
- `disk-<key>.idx` (`BDIX` lists of chunk ids; an all-zero id is a zero chunk);
- `tree-<key>.idx` (file trees: `BDTL` listings as chunk streams);
- blobs `<name>.zst` (VM metadata, Kubernetes objects as JSON lines, guest metadata, SQL snapshot, VSS documents).

SQL backup objects are separate (`sql-backups/…/manifest.json` + `file-<key>.idx`). Chunks are SHA-256 of the plaintext, stored as `BDCK` + zstd.

**Gaps found while exploring**
- **No "all chunks of restore point X".** GC mark (`LocalDirectoryRepository.cs:256-314`) and Verify (`:374-456`) each walk the restore point their own way.
- **Writing back** through `IRepositoryWriter`:
  - needs the target's `RepositoryId` and a restore point id that is new there;
  - `PutChunkAsync` recompresses.
- **Ids:** `RestorePointEntity.Id` is a global primary key.
- **SQL indexes** are not exposed: `ISqlBackupReader` is forward-only.
- **VM restore** streams sequentially with `import_raw_vdi` (`XenRestoreSession.cs:588-760`), so a deduplicated media set cannot be restored as a direct stream.

**User decisions (2026-11-16)**
- **Format:** our own documented on-tape format, not LTFS. Every tape carries a label and its catalog. A recovery command works without the server or the database.
- **Media sets with dedup,** as in Proxmox Backup Server: each chunk once per set; a new set starts full.
- **Restore, both ways:**
  - import into a disk repository (then every existing restore works);
  - a direct VM restore through a temporary cache holding only that VM's chunks.
- **Encryption per media pool:** software (passphrase) or LTO hardware.
- **Also in the first version:** WORM pools, SQL log backups, standalone drives.
- **Defaults:**
  - a tape job copies the newest restore point per VM;
  - disk retention does not wait for tape in v1 (the log names restore points a job missed).
- **UI:** simple and intuitive. Mockups were drawn in variants for the user to choose from.

## Decisions

1. **`Backdafuckup.Tape`** (from Part A) gains `Format`, `Crypto` and the open-set index. It has no database. The server and `bdfu-tape-recover` share the reader code.
2. **No file spans two tapes.**
   - Archive files end at a record boundary.
   - Restore-point, SQL and catalog files are built first and only started when they fit. Free space is the MAM remaining capacity (or written bytes against nominal capacity) minus a reserve of max(64 MiB, 1 %).
   - Early warning is a safety net: finish the current record, close the file, write the catalog, change the tape.
   - A physical end of medium inside a file leaves it torn (no trailer); it is written again on the next tape.
3. **Chunk locations live on tape, not in PostgreSQL.**
   - Every restore-point file carries `chunks.loc`; every archive file ends with its index.
   - The database keeps coarse rows only: copies, needed media, the place of each restore-point file, bytes.
   - Dedup while writing uses a local sorted-segment index (`BDCI`), only for a pool's open set, in `<DataDirectory>/tape/open-sets/<set>/`. It is deleted when the set closes. If it is lost, the next run starts a new set.
4. **Manifest-last commit per group:** archives → restore-point and SQL files → one synchronizing filemark → database commit.
   - A group is at most min(16 GiB, capacity/64).
   - A copy exists only when all its chunks are on tape.
5. **Append-only,** which also makes WORM safe. A rewritable tape is reused only after its set expired, by labelling it again at the beginning of tape.
6. **Imports get new ids.**
   - New restore point id, the target `RepositoryId`, `BaseRestorePointId = null`, `JobId = null` (job retention never deletes it).
   - JobName "Imported from tape (<set>)".
   - New `ImportedFromTapeCopyId`.
   - `SessionId` = the import session, so a whole cluster run is still found together.
7. **Encoded chunks are copied, not recompressed.** A new `PutEncodedChunkAsync` decodes, checks SHA-256 and stores the `BDCK` bytes unchanged.
8. **Keys.**
   - **KDF:** Argon2id (Konscious.Security.Cryptography, MIT, fully managed), m = 256 MiB, t = 3, p = 4, 16-byte salt. It gives a key-encryption key (KEK); the .NET BCL has no memory-hard KDF.
   - **Fingerprint:** HMAC-SHA256(KEK, "bdfu-tape-key-id")[0..8], shown as `3F2A-91C0-7B11-02DE`.
   - **Per-set keys:** HKDF-SHA256(KEK, salt = set uuid, info "bdfu-tape-sw-v1" or "bdfu-tape-lto-v1").
   - **Software encryption:** AES-256-GCM per record body with a random 96-bit nonce. The AAD is the record header, set uuid and file id. Chunk ids, manifests and VM names are inside the ciphertext.
   - **Hardware encryption:** the label stays in clear; the set key goes to the drive with U-KAD `BDFU` + fingerprint + set uuid.
   - **On the server:**
     - the KEK is stored with `CredentialProtector`; the passphrase is never stored;
     - "Download key file" (KEK and KDF parameters) is offered once;
     - keys stay as long as sets use them.
   - **The recovery command** reads the KDF parameters and fingerprint from the label. It gets the key from a prompt, `--passphrase-file`, `BDFU_TAPE_PASSPHRASE` or `--key-file`.
9. **A separate `TapeJobEntity`.** `BackupJob` requires a connection and a repository, and its runner is VM-specific. Tape jobs still appear in the Jobs list and in the job editor's kind toggle.
10. **Operator requests** are database rows, pushed live and shown as a banner on every page. The session polls the library or drive and continues by itself; it times out after 12 h.
11. **SQL log backups.** For each copied VM, the SQL backup objects that disk retention keeps for its oldest restore point in the set. The rule moves out of `SqlBackupService.cs:765-843` into `SqlRetentionWindow`; each run adds newer logs.

## On-tape format (`docs/tape-format.md`, version 1)

- **Block:** a 32-byte big-endian header, then the payload.
  - Header: `BDTB`, version, flags (bit 0 = last block of the file), file id (u64), block number (u32), payload length (u32), CRC-32 over header and payload, reserved.
  - Readers refuse newer versions.
- **Records** inside a file: type (4 ASCII), flags (u32), body length (u64), body.
- **Files:** each starts with `FHDR` (in clear: type, ids, sequence, file number) and ends with `FTRL` (record count, SHA-256 over the bodies).
  - A file without `FTRL` is torn and ignored.
  - One filemark follows every file.
- **File 0, the label:** one block, never encrypted. JSON `BDFU-TAPE` with:
  - format version, medium uuid, label or barcode, block size, WORM, time, server;
  - the set (uuid, sequence, pool id and name), or null for a free tape;
  - encryption (mode, fingerprint, KDF name, parameters and salt).
- **Chunk archive:** `CHNK` records (chunk id + `BDCK` bytes), then `AIDX` (zstd: id, block, offset, length).
- **Restore-point file:** `ENTR` records (name + bytes):
  - `copy.json` (copy id; original restore point, repository, job and session; set);
  - `manifest.json`, the `disk-*.idx` and `tree-*.idx` files, the `*.zst` blobs;
  - `chunks.loc` (zstd, sorted entries: id, tape sequence, file, block, offset, stored length).
- **SQL file:** the same, with `file-*.idx`.
- **Catalog,** after every run and before every tape change:
  - a set section: tapes; all copies with their manifests embedded, file locations and needed tape sequences;
  - this tape's file table.

  The last catalog on the highest-sequence tape describes the whole set.
- **Recovery without the server:**
  1. Read the label.
  2. Space to end of data and step back to the newest complete catalog.
  3. Pick a copy, read its restore-point file, then `chunks.loc`, then the archives.
  4. Without any catalog, `scan` reads every file header.
- **Golden images** in `tests/fixtures/tape/` keep version 1 readable forever.

## Catalog (migration `AddTape`)

- **`MediaPoolEntity`:**
  - library and drives, block size;
  - new set every run, daily, weekly, monthly or never (with a time zone);
  - retention in days or forever;
  - WORM only;
  - encryption none, software or hardware, with its key;
  - export never, when a set closes, or after every run.
- **`TapeKeyEntity`.**
- **`MediaSetEntity`:**
  - state: open, closed, expired or overwritten;
  - expiry, key fingerprint, committed index segments.
- **`TapeMediumEntity`:**
  - barcode or label, medium uuid, pool, set, sequence;
  - state: free, writing, full, retained, expired or damaged;
  - location: slot, drive, mail slot or offline;
  - WORM flag, bytes, capacity, MAM snapshot, last file, last catalog.
- **`TapeJobEntity`.**
- **`TapeCopyEntity`, `TapeSqlCopyEntity`:**
  - their own ids; the original ids without foreign keys;
  - denormalized VM and kind fields; the manifest as jsonb;
  - the location of the restore-point file, the needed media, stored bytes.
- **`TapeOperatorRequestEntity`.**
- **Changes to existing entities:**
  - `ImportedFromTapeCopyId` on `RestorePointEntity` and `SqlBackupEntity`;
  - `SessionKind` gains TapeBackup, TapeRestore and TapeMaintenance.

## Tape job run

- **Selection:**
  - source backup jobs and/or VMs;
  - "newest per VM" (default) or "every new restore point", skipping what the open set has;
  - a whole-cluster run's `@cluster` point comes along;
  - SQL objects through `SqlRetentionWindow`;
  - an offline repository gives a warning.
- **Set and tape:**
  - The pool policy decides when a new set starts.
  - First choice: the set's last tape, if it is in the library and appendable.
  - Then an expired tape of the pool.
  - Then a free or blank tape, labelled automatically. On a standalone drive the request says "write `Weekly-007` on the cartridge".
  - Foreign or unknown tapes with data are never used.
- **Pins:** source restore points and SQL objects are pinned, and released per committed group.
- **Pipeline:**
  1. `IChunkLocator.ReadStoredAsync` with prefetch;
  2. ordered encryption workers;
  3. a ring buffer (`Tape:WriteBufferMiB`, default 1024);
  4. one writer thread.

  Filemarks are buffered, except the group's synchronizing one. The log reports how long the drive waited for data.
- **Tape housekeeping:** MAM host attributes when labelling; TapeAlert after the run and on errors.
- **Logs** name the drive, barcode, set and sequence. One task per VM.
- **Triggers:**
  - a schedule through `JobScheduler.TickAsync`;
  - "after the source job" (on Succeeded or Warning), at the `LastResult` hook in `EngineServices.cs`.
- **Dispatch:** `SessionQueue.EnqueueTapeBackupAsync`. `TapeDriveScheduler` is added to `HasCapacity` and the dispatch switch; its reservation is released in `finally`.

## Restore

- **Import into a repository:**
  1. Read the restore-point file and `chunks.loc`.
  2. Read the tapes in sequence order, locating instead of reading through gaps over 256 MiB.
  3. Decode and check each chunk in parallel into `PutEncodedChunkAsync`.
  4. Write the indexes, blobs and manifest as in decision 6.
  5. Catalog like the repository import: `ApplyManifest`, `SqlBackupCatalog.RecordSqlBackupAsync`, `SqlCatalogBackfill.RecordSnapshotFullsAsync`.

  The repository delete guard also covers tape sessions.
- **Direct VM restore:**
  - `RestoreSessionRunner.RestorePointAsync` is split into a resolve step and a core. The core takes a reader and a transient restore point.
  - The reader is a temporary `LocalDirectoryRepository` under `Tape:CacheDirectory` (default `<DataDirectory>/tape-cache`).
  - It is refused when free space is below the stored bytes + 5 %. The cache is deleted in `finally` and at startup.
  - VMs only, and no resume with memory. Kubernetes goes through import.
- **Catalog rebuild** (lost database):
  - A TapeMaintenance session reads the labels and last catalogs, or scans.
  - An unknown key raises a passphrase request.
  - Pools are matched by name, else "Imported <name>" is created.
- **Verify on demand:** the import pipeline with an empty sink.
- **`bdfu-tape-recover`** (`src/Backdafuckup.TapeRecover`, single-file for Windows and Linux):
  - commands: `devices`, `inventory`, `catalog`, `scan`, `restore --to-repository <dir>` (then import that directory in the console), `restore --to-images <dir>` (sparse raw disks);
  - uses a changer, or asks to "insert tape X".

## Console

The layout follows the mockup variant the user picks (see `shape.md`). The pieces:
- **Tape area:** *Backup Infrastructure → Tape*, with the tabs *Libraries* (Part A), *Media* and *Media pools*.
- **Media pool:** one dialog: library, new set (every run / daily / weekly / monthly / never), keep for, WORM only, encryption (none / passphrase with fingerprint and a one-time key file download / drive), export, block size under *Advanced*.
- **Jobs:** "Backup to tape" as a kind in the job editor (Name → Source → Media pool → Schedule → Summary). Source is backup jobs or single VMs, "newest per VM" or "every new", and "include SQL log backups". An estimate shows bytes, tapes and time.
- **Restore points:** "on tape" pills ("tape only", "offline") and the menu items *From tape → Bring back into a repository…* and *→ Restore the VM directly…*.
- **Restore wizard:** the way, the target repository (same chunk size, enough space), and the tapes needed with where each one is.
- **Elsewhere:** the operator banner on every page; a Sessions filter; an icon.

## Tasks

1. **Spec docs** (this folder).
2. **Repository groundwork:**
   - `RestorePointChunks.VisitAsync`, extracted from GC mark and Verify, which both then use it;
   - `ISqlBackupReader.ReadFileIndexAsync`, `ISqlBackupWriter.WriteFileIndexAsync`;
   - `PutEncodedChunkAsync`;
   - `SqlRetentionWindow`.
   - Tests: the visitor yields the old mark set (disks, trees, memory image); corrupt encoded bytes are refused; a SQL index round-trips.
3. **Format:** writer and reader, label, archive, restore-point, SQL and catalog files, the fit check, early warning and torn files; `docs/tape-format.md`.
   - Tests on the fake: early warning in the middle of each file type; spanning 8 MiB tapes; a newer version refused; a WORM rewrite refused; golden images.
4. **Crypto:** `TapeKeyMaterial`, record AES-GCM, LTO key and KAD.
   - Tests: known vectors; wrong passphrase; tampered header or body; no chunk id or VM name in clear.
5. **Open-set index** (`BDCI`).
   - Tests: 1 M random ids; an uncommitted segment is ignored.
6. **Catalog model:** migration `AddTape`, DTOs in `Contracts/Tape.cs`, `ApiMapping`.
   - Tests: create, read, update and delete; validation (a key in use cannot be deleted; hardware encryption needs a capable drive).
7. **Drive scheduler and operator requests:** `HasCapacity` and dispatch, live updates, `StartupRecovery` (stale sessions, requests, caches, uncommitted segments).
   - Tests: a standalone fake drive with the right tape, and with a wrong one (ejected and named).
8. **Tape backup runner.**
   - Tests in `EngineIntegrationTests.Tape.cs` with 64 MiB fake tapes:
     - a run spans 3 tapes with a catalog on each;
     - the second run writes only new chunks; "every new" mode;
     - an encrypted set;
     - a crash mid-group, then the next run appends;
     - `@cluster` points and SQL logs copied.
9. **Media lifecycle:** close, expire, reuse; WORM never reused. Actions: label, mark free (barcode typed), damaged, export, import, new set now.
10. **Import into a repository.**
    - Tests: disk images byte-identical; a file-tree point passes full verify; SQL coverage up to the newest copied log; a whole-cluster restore.
11. **Direct VM restore.**
    - Tests: restore to the fake pool; a cache that is too small is refused.
12. **Catalog rebuild and verify.**
    - Tests: after wiping the tape tables, the rebuild equals the state before.
13. **`bdfu-tape-recover`.**
    - Tests: in-process against the fake device; the images equal the source.
14. **Console** (pieces above). bUnit tests.
15. **Metrics, alerts, docs.**
    - Metrics:
      - `bdfu_tape_written_bytes_total{pool}`, `bdfu_tape_read_bytes_total`;
      - `bdfu_tape_drive_wait_seconds_total{drive}`;
      - `bdfu_tape_media{pool,state}`, `bdfu_tape_pool_usable_media{pool}`, `bdfu_tape_media_sets{pool,state}`;
      - `bdfu_tape_alerts_total{drive,flag}`;
      - `bdfu_tape_operator_requests_pending`.
    - Alerts: TapeNoUsableMedia, TapeJobFailed, TapeOperatorWaiting (30 min), TapeDriveAlert.
    - Admin guide "Tape": pools, sets, keys and the key file, WORM, standalone drives, export, restore, the recovery command, a lost database.
    - THIRD-PARTY-NOTICES (Argon2); runbook rows.

## Verification on the VTL

Holo emulates an HPE MSL6480 with LTO-9 drives and 2 GiB cartridges. Rows T1–T15 are in the device-layer spec.

| Row | Check |
|---|---|
| T16 | First tape job with 2 VMs: the log names drive, barcode and set; the tape ends with a catalog |
| T17 | A 5 GiB VM spans 3 tapes; after import the SHA-256 of the disk images is equal |
| T18 | The second run writes only new chunks; "every new restore point" mode |
| T19 | Weekly policy starts a new set; with 0 days retention the tapes are reused and the old copies disappear |
| T20 | Software encryption: a raw dump shows no VM names; a wrong passphrase is refused; the key file works |
| T21 | WORM pool: appending works; expired WORM tapes are never offered; *Mark free* is disabled |
| T22 | Standalone drive job: banner; a wrong tape is detected |
| T23 | Export when a set closes; an offline tape is asked for during import |
| T24 | SQL point-in-time restore after an import, between two log backups |
| T25 | Direct VM restore into the lab pool; a cache that is too small is refused; the cache is deleted afterwards |
| T26 | Kubernetes namespace and whole-cluster restore through import |
| T27 | Recovery command on a second Linux machine: a repository added to a fresh server; a raw image boots |
| T28 | Lost database: catalog rebuild, then a restore |
| T29 | Server killed mid-run: the next run appends; the torn file is ignored |
| T30 | The drive-wait metric under load |

T31 (device-layer spec) covers hardware encryption and real LTO-9 timing on the real library.

## Risks

- **The VTL is not a real drive.** Early-warning zone, WORM, hardware encryption, MAM and TapeAlert can behave differently; a real LTO-9 run is needed before release.
- **Drive speed** depends on pack files and the read-ahead. The drive-wait metric shows when the drive starves (shoe-shining).
- **Format version 1 is permanent:** a version field, refusal of newer versions, and golden images guard it.
- **Long operator waits hold pins,** which delays disk retention. This is logged.
- **A lost passphrase or key file means lost data.** The console and the admin guide say so plainly.
- **Long or never-closed sets** can need many tapes for one restore; the wizard lists them.
