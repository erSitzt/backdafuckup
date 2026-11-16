# backdafuckup tape format, version 1

How backdafuckup writes backups to tape: enough to read them back without the backup server, its database or this
program. All numbers are big-endian. Readers refuse blocks of a newer version.

## Tapes, files and blocks

- A tape holds **files**. Every file ends with one **filemark**. File 0 is the label.
- A file is a run of **blocks**: variable-length tape blocks of up to the set's block size (256 KiB by default; written in the label).
  Every block but the last of a file is full.
- Each block starts with a 32-byte header:

  | Offset | Size | Field |
  |---|---|---|
  | 0 | 4 | `BDTB` |
  | 4 | 1 | format version (1) |
  | 5 | 1 | flags: bit 0 = last block of the file |
  | 6 | 2 | reserved, 0 |
  | 8 | 8 | file id (random, the same in every block of a file) |
  | 16 | 4 | block number in the file, from 0 |
  | 20 | 4 | payload length |
  | 24 | 4 | CRC-32 (IEEE 802.3, polynomial EDB88320h) of the whole block with this field 0 |
  | 28 | 4 | reserved, 0 |

- The payloads of a file's blocks, in order, form a stream of **records**.

## Records

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | type, 4 ASCII letters |
| 4 | 4 | flags: bit 0 = encrypted |
| 8 | 8 | body length |
| 16 | n | body |

Records run across block boundaries. A record's **position** is the block (of its file) and the offset in that block's
payload where its header starts.

Every file starts with `FHDR` and ends with `FTRL`; both are never encrypted.

| Type | Body |
|---|---|
| `FHDR` | JSON: `type` (`label`, `archive`, `restore-point`, `sql`, `catalog`), `fileId`, `setId`, `sequence` (the tape's number in its set, from 1), `fileNumber` (on this tape; the label is 0), `createdUtc`, `copyId` (restore-point and SQL files) |
| `FTRL` | record count (8 bytes, `FHDR` counted, `FTRL` not) and the SHA-256 (32 bytes) of every record before it, headers and bodies as stored |
| `LABL` | the label (below) |
| `CHNK` | a chunk: its id (32 bytes, SHA-256 of its content) and its stored record as in the repository (`BDCK` header, zstd) |
| `AIDX` | where the chunks of this archive file are: chunk locations (below) |
| `ENTR` | a file of a copy: name length (2 bytes), name (UTF-8), content |
| `LOCS` | where every chunk of a copy is: chunk locations (below) |
| `CATL` | the set's catalog: zstd-compressed JSON (below) |

A file whose blocks end (filemark or end of data) before `FTRL`, or whose trailer does not match, is **torn**: it is
ignored. It happens when a tape ends in the middle of a file; the file is then written again on the next tape.

### Chunk locations (`AIDX`, `LOCS`)

zstd-compressed; 50 bytes per chunk, sorted by chunk id:

| Offset | Size | Field |
|---|---|---|
| 0 | 32 | chunk id |
| 32 | 2 | tape (sequence in the set) |
| 34 | 4 | file number on that tape |
| 38 | 4 | block of that file |
| 42 | 4 | offset in that block's payload |
| 46 | 4 | length of the `CHNK` record (header and body as stored) |

To read a chunk: load the tape, position at the file's first block plus the block (the catalog has every file's first
block; without it, space forward by filemarks to the file), skip the offset, read the record.

## The label (file 0)

`FHDR`, `LABL`, `FTRL` in one block, never encrypted. `LABL` is JSON:

```json
{
  "format": "BDFU-TAPE", "version": 1,
  "mediumId": "…", "barcode": "BDF000L9", "name": "BDF000L9",
  "blockSize": 262144, "worm": false, "createdUtc": "…", "server": "…",
  "set": { "id": "…", "sequence": 1, "poolId": "…", "poolName": "Weekly", "createdUtc": "…" },
  "encryption": { "mode": "software", "fingerprint": "3F2A-91C0-7B11-02DE", "kdf": "argon2id",
                  "memoryKiB": 262144, "iterations": 3, "parallelism": 4, "salt": "…" }
}
```

`set` is null on a free tape; `encryption` is null on an unencrypted set.

## Files of a set

- **Archive** (`archive`): `CHNK` records of chunks the set did not have yet, then `AIDX`.
- **Restore point** (`restore-point`): `ENTR` records: `copy.json` (the copy's id, the original restore point, repository,
  job, VM, its time, the set), `manifest.json`, then the restore point's indexes (`disk-*.idx`, `tree-*.idx`) and blobs
  (`*.zst`) byte for byte as in the repository; then `LOCS` with every chunk the restore point needs, wherever in the
  set it is. A copy exists once this file is complete; its archives come before it.
- **SQL backup** (`sql`): the same for a SQL Server backup object.
- **Catalog** (`catalog`), after every run and before every tape change: the set (id, pool, time), its tapes
  (sequence, medium id, name, barcode), every complete file (tape, file number, first block, file id, type, blocks,
  bytes, copy) and every copy (with its manifest and the tapes it needs). The last complete catalog on the set's
  highest tape describes the whole set.

No file spans two tapes. Before a restore-point, SQL or catalog file is started, it must fit: the cartridge's remaining
capacity (from its memory, MAM) minus a reserve. Archives stop at the reserve or at the drive's early warning and go on
in a new archive on the next tape.

## Encryption

- **Keys.** A passphrase gives the key-encryption key (KEK): Argon2id with the label's parameters and salt, 32 bytes.
  Its fingerprint is HMAC-SHA256(KEK, `bdfu-tape-key-id`), the first 8 bytes in hex as `XXXX-XXXX-XXXX-XXXX`. Each set
  has its own key: HKDF-SHA256 (RFC 5869) of the KEK with the set id (16 bytes, big-endian) as salt and the info
  `bdfu-tape-sw-v1` (software) or `bdfu-tape-lto-v1` (drive), 32 bytes.
- **Software encryption.** Bodies of `ENTR`, `LOCS`, `CHNK`, `AIDX` and `CATL` are sealed with AES-256-GCM: a random
  12-byte nonce, the ciphertext, a 16-byte tag. The additional data is the record's header (with the sealed length),
  the set id (16 bytes) and the file id (8 bytes). Chunk ids, file names, manifests and VM names are inside.
- **Drive encryption** (LTO, application-managed): the drive encrypts with the set's `bdfu-tape-lto-v1` key; every block
  carries the key-associated data `BDFU`, the fingerprint (8 bytes) and the set id. The label is written in clear.
- **Key file** (`BDFU-TAPE-KEY`, JSON): the KEK in base64 with its fingerprint and KDF parameters; it replaces the
  passphrase.

## Reading without the server

1. Read the label: format, block size, set, encryption.
2. Space to the end of data and read back to the last complete catalog of the highest tape.
3. Pick a copy; read its restore-point file; read `LOCS`; read the chunks from the archives.
4. Without a catalog, read every file's `FHDR` to find the restore-point files (a scan).
