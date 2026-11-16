# Backup to Tape — Shaping Notes

## Scope

Tape jobs copy restore points (and their SQL log backups) from disk repositories onto tapes, in media pools and media sets with deduplication inside a set. Restores work two ways:
- import back into a repository;
- a direct VM restore through a temporary cache.

The tapes carry their own label and catalog in a documented format, so a recovery command can restore without the server or its database. Encryption is optional: by passphrase or by the drive.

## Decisions

### By the user (2026-11-16)
- **Full scope,** tested on a Holo VTL (HPE MSL6480 profile, LTO-9, small cartridges).
- **Format:** our own documented format, not LTFS.
- **Media sets with dedup,** as in Proxmox Backup Server.
- **Restore:**
  - into a repository first;
  - also a direct VM restore. The user accepted that "direct" means a temporary cache with only that VM's chunks, because a deduplicated set cannot be streamed in disk order.
- **Encryption:** both software and hardware, as an option per pool.
- **Also in the first version:**
  - WORM tapes;
  - SQL log backups on tape (point-in-time restore from tape);
  - standalone drives;
  - repository pack files before tape.
- **Defaults:**
  - a tape job copies the newest restore point per VM;
  - disk retention does not wait for tape in v1.
- **UI:** keep it simple, stupid, easy and intuitive. Veeam, Proxmox Backup Server and other standard tape software serve as ideas.

### In the design (2026-11-16, checked against the code)
- **No file spans tapes;** a torn file is written again on the next tape. The catalog is rewritten after every run and before every tape change.
- **Chunk locations live on tape** (`chunks.loc`, archive indexes), not in PostgreSQL.
- **Dedup index:** local, only for the open set.
- **Commit:** manifest-last per group; append-only, so WORM is safe.
- **Imports** get new ids and are never touched by job retention.
- **Copies** move encoded chunks without recompressing them.
- **Keys:** Argon2id → KEK, HKDF per set. Passphrases are never stored, and the key file is downloaded once.
- **Separate entities:** a tape job has its own entity, but lives in the same Jobs list and editor.

## Context

- **Visuals:** mockups in a design canvas (not in the repository):
  - three variants of the tape page;
  - the media pool dialog;
  - the tape job editor (Source step);
  - restore from tape with the tapes needed;
  - the operator banner and restore points marked "on tape".

  **Chosen by the user (2026-11-16): variant B**, tables with filters as in Proxmox Backup Server:
  - the tape page has the tabs *Libraries & drives*, *Media* and *Media pools*;
  - drives are a compact table (drive, library, state, cartridge, job, speed);
  - media are a table with search, pool and location chips, and the actions in a row menu.
  - **plus the slot map of variant A** (user, the same day): each library also shows its slots, mail slots and drives as a map, coloured by state (free, open set, full, expired, WORM, cleaning, foreign, empty); a click on a slot shows the cartridge with its actions.

  The dialogs below the variants (media pool, tape job editor, restore from tape, operator banner, restore points marked *on tape*) apply as drawn.
- **References:** see `references.md`.
- **Product alignment:**
  - roadmap Phase 2 "Tape out: move or copy backups to tape on an HPE MSL tape library";
  - the backlog item "Repository pack files" comes first (`../2026-11-16-1804-repository-pack-files`).
