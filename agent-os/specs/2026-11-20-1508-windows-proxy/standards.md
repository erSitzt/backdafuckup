# Standards for the Windows Backup Proxy

The standards of spec 3 apply (`../2026-11-18-1713-storage-snapshot-backup/standards.md`), and those of the earlier specs:
- only public interfaces;
- never destroy what we did not create;
- errors name the place;
- both operating systems.

## Additions for this spec

### Change nothing the server relies on
The agent runs on servers that do other work, such as another product's backups:
- **iSCSI and MPIO:** it never logs in or out, and never changes iSCSI or MPIO settings.
- **Disks:** it changes the attributes only of disks it opens by the WWN of our snapshot. They are set offline and read-only, and not persisted.
- **The one global setting** it may change is an unsafe SAN policy, which it sets to Offline Shared and reports.

### Only by WWN
A disk is chosen only by its NAA identifier and size, never by its disk number. Numbers change after rescans.

### Read-only on Windows
Every disk is opened with `GENERIC_READ` only. A writable open is refused.

### Testable on Linux
Every Windows call sits behind a narrow interface or `CommandRunner`, so the logic is tested on Linux CI:
- P/Invoke behind `IWindowsDisks`;
- PowerShell through `CommandRunner`.
