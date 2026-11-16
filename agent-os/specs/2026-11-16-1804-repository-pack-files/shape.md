# Repository Pack Files — Shaping Notes

## Scope

Chunks are stored in pack files instead of one file per chunk:
- an index that scales to tens of millions of chunks with little RAM;
- crash-safe writes with concurrent writers;
- GC that repacks;
- verify over packs;
- an explicit, resumable conversion of existing repositories.

The tape job, and later SMB/NFS/S3 targets and offsite copies, read chunks in pack order.

## Decisions

### By the user (2026-11-16)
- **Before tape:** pack files come before the tape work, as the roadmap backlog asks.
- **Conversion:** a button per repository, never automatic.
  - After the update, format 1 repositories run unchanged.
  - New repositories start in format 2.
  - The production-like server converts a copy first, then the real repositories outside backup windows.

### In the design (2026-11-16, checked against the code)
- **Format:**
  - `BDPK` packs with a self-describing trailer, so the index can always be rebuilt.
  - Chunk ids and `BDCK` records are unchanged inside packs.
  - `repo.json` format 2, which older servers already refuse at open.
- **Index:** sorted segments with a fan-out table and a Bloom filter (Kopia-like levels).
  - About 32 MiB of RAM at 20 M chunks.
  - No mmap, so SMB works later.
  - The pack registry built from `packs/` is authoritative.
- **Concurrency:**
  - one open pack per writer;
  - commit dependencies on other writers' open packs;
  - the existing stripe locks, `_protected` set and late marks also guard repacking.
- **GC:**
  - a prefix table for the mark (about 320 MB at 20 M chunks instead of about 1 GB);
  - repacks packs that are at least 20 % dead or tiny;
  - candidates are marked *retiring* before anything is copied.
- **Migration:** a resumable maintenance session that packs loose chunks newest restore point first, so a point's chunks sit together for tape reads.
- **No downgrade tool.**

## Context

- **Visuals:** none (the console only gains a format column and a *Convert* button).
- **References:** see `references.md`.
- **Product alignment:** roadmap backlog "Repository pack files (before the SMB/NFS/S3 targets and tape out)"; Phase 2 "Tape out".
