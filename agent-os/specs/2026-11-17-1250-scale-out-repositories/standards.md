# Standards for Scale-out Repositories

No standards are indexed in `agent-os/standards/` yet. The conventions of the earlier specs apply, in particular those of the foundation (`../2026-11-01-2134-xcpng-backup-foundation/`) and the pack files (`../2026-11-16-1804-repository-pack-files/`):
- restore points are written once;
- the manifest is the commit point;
- nothing is referenced before it is durable.

## Additions for this spec

### One catalog row per id, always
- A restore point or SQL Server backup keeps its id when it moves.
- Its row names exactly one extent.
- A rescan never inserts an id that is known on another extent.

### Nothing is deleted before the switch is durable
- **Target copy:** committed and synced before the transaction that points the row at it.
- **Source copy:** deleted only after that transaction, and only while no reader has it pinned.
- **Cleanup:** what cannot be deleted at once stays in the journal for the cleanup service.

### Compare-and-set instead of long locks
- Retention, delete and the move switch change a row only when it still holds what they read (`WHERE repository_id = … AND state = 'Available'`).
- The loser of a race backs off and cleans up its own copy.

### Readers double-check
- **Order:** open the extent the row names, pin, read the row again.
- **On a change:** release and retry.
- **Holding a point** for a long time (browsers, publishes) keeps the pin, so the source copy stays until it is released.

### Errors name the place
Messages name the scale-out repository, the extent, its path and the state that blocks (maintenance, offline, sealed, evacuating, waiting for a pin or a running backup), never only an id.

### Both operating systems
- Free space comes through `IVolumeSpace` (`DriveInfo` on both).
- The same-volume check compares volume roots (drive letters or mount points).
- The tests run on the Ubuntu and Windows CI runners.
