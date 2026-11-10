# Standards for Snapshot with Memory

There are no formal standards yet (`agent-os/standards/index.yml` is empty). These conventions apply:
- the foundation spec (`../2026-11-01-2134-xcpng-backup-foundation/standards.md`): REST and SignalR only, the console in MudBlazor;
- the FLR additions (`../2026-11-02-1713-file-level-restore/standards.md`): fail loudly, and messages with context (VM, pool, storage, sizes);
- the integration policy in `agent-os/product/tech-stack.md`: public APIs only; open-source code may be read, but nothing from purchased products.

The additions below apply on top.

---

## Additions for this spec

### Nothing left behind
- Every object a checkpoint creates (the snapshot VM, the disk snapshots, the suspend VDI, `[NOBAK]` disk snapshots) is tagged, recorded in the artifact ledger and removed on commit, rollback or by the background retry.
- Nothing in the pool is deleted by hand.

### Say what the pause costs
- Before a VM is switched to "with memory", the console names the cost:
  - the VM pauses for the RAM write plus all disks;
  - RAM-sized space on the storage and in the backup;
  - `[NOBAK]` disks are captured too.
- The session log names the measured pause.

### No surprise network identities
- A resumed copy never comes up with the original's network connections.

### Placeholders in the repository
- Docs, specs, runbook rows and tests use the placeholders from `docs/history-rewrite.md` (`backup01.corp.example`, `10.16.1.x`, `10.26.x.y`, …), never the real internal names or addresses.
