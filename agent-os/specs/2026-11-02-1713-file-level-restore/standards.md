# Standards for File-Level Restore

There are no formal standards yet (`agent-os/standards/index.yml` is empty). The conventions of the foundation spec (`../2026-11-01-2134-xcpng-backup-foundation/standards.md`) and of the guest agent spec (`../2026-11-02-1430-windows-guest-agent/standards.md`) apply, plus the additions below.

---

## Additions for this spec

### Untrusted guest data
- Disk images, file systems, file names, registry hives and `/etc/fstab` from a backup are **untrusted input**.
- Parsers walk iteratively, with limits on depth, entry count and time.
- A broken or hostile image must never end the server process.
- File names from a guest never end up in a shell command, an SMB path outside the target folder, or a ZIP entry outside its root (`..`, `\`, absolute paths).

### Fail loudly
- Anything the product cannot read correctly is reported with a reason, never returned as zeros or empty files. This covers unknown file system features, deduplicated or encrypted files, and unsupported volume types.

### Threads
- Synchronous parser work runs on worker threads, never on request threads or in sync-over-async code on the thread pool.
- Kestrel responses are written asynchronously.

### Audit
- Every browse mount is a session. Its log names the user, the restore point, and each download and restore with file counts and sizes.
- Restore targets are named in log lines with host name, IP and port (e.g. `fileserver (10.0.0.5):445`, `web01 (10.0.0.7):22`).

### Dependencies
- Only AGPL-compatible open-source libraries.
- Vendored third-party source lives under `src/ThirdParty/<name>/` with its license file, a NOTICE naming the upstream commit, and a list of our patches.
- Patches are offered upstream.
