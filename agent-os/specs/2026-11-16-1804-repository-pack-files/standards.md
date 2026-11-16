# Standards for Repository Pack Files

No standards are indexed in `agent-os/standards/` yet. The conventions of the earlier specs apply, in particular those of the foundation (`../2026-11-01-2134-xcpng-backup-foundation/`):
- restore points are written once;
- the manifest is the commit point;
- every on-disk format carries a version that older code refuses.

## Additions for this spec

### Nothing is referenced before it is durable
- A pack is flushed and its directory synced before an index delta names it.
- A manifest is written only after every pack it depends on is sealed and its delta is durable.
- A loose chunk is deleted only after its packed copy is durable.

### Formats refuse what they do not know
- **`repo.json` format 2:** older servers refuse it at open, before recovery or GC.
- **Pack, index and footer versions:** readers refuse newer ones.
- **Damaged packs** are reported by path and never deleted automatically.

### Errors name the place
Messages name the repository root, the pack path and the offset, never only a chunk id.

### Both operating systems
- **File sharing:** pack handles are opened so Windows can delete repacked packs.
- **Durability:** fsync on Linux, FlushFileBuffers on Windows.
- **Tests** run on the Ubuntu and Windows CI runners.
