# References — Application-Consistent Snapshots on Slow Storage

## Code in this repository

- `src/Backdafuckup.Xen/XenBackupSession.cs` — snapshot flow: CBT, `[NOBAK]`, gate, free space, freeze, `VM.snapshot`, thaw, timing.
- `src/Backdafuckup.Xen/XenSnapshotGate.cs` — one snapshot per storage.
- `src/Backdafuckup.Server/Agents/AgentQuiescer.cs` — prepare, freeze and thaw through the agent; approaches A and B add their commands here.
- `native/bdfu-vss/bdfu-vss.cpp` — today's VSS requester and hold provider (context, copy-only).
- `src/Backdafuckup.Server/Engine/RestoreSessionRunner.cs` — VM restore; excluded disks are created empty.
- `src/Backdafuckup.Server/Engine/BackupSessionRunner.cs` — consistency per restore point and per SQL database.
- `agent-os/specs/2026-11-03-0026-sql-server-restore/` — attach-based restore and publish, guest metadata v2.

## Public sources

- Microsoft Learn: [Create a Transact-SQL snapshot backup](https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/create-a-transact-sql-snapshot-backup) (SQL Server 2022+, `MODE = COPY_ONLY`, 64 databases, `RESTORE … METADATA_ONLY`, `sys.dm_server_suspend_status`).
- Xen Orchestra source (AGPL-3.0): `@xen-orchestra/xapi/vm.mjs` (`snapshot` with `ignoredVdisTag`, `_httpHook` / `xo:notify-on-snapshot`, `checkpoint`) and `@xen-orchestra/backups/_runners/_vmRunners/_AbstractXapi.mjs` (`[NOBAK]`, `xo-memory-backup`, `xo-offline-backup`).
- XCP-ng forum: [quiesced snapshots removed](https://xcp-ng.org/forum/post/35192); XO blog: [XenServer quiesce snapshots](https://xen-orchestra.com/blog/xenserver-quiesce-snapshots/).
- Veeam community: [XCP-ng plug-in public beta review](https://community.veeam.com/blogs-and-podcasts-57/xcp-ng-plugin-for-veeam-backup-replication-public-beta-review-11677) (no AAIP); [Veeam R&D forum, XCP-ng support](https://forums.veeam.com/viewtopic.php?f=62&t=93030&start=180); [Cloudy Journey, April 2026](https://www.toddpigram.com/2026/04/veeam-and-xcp-ng-agentless-incremental.html).
- libyal: [libvshadow](https://github.com/libyal/libvshadow) (LGPL-3.0+) and its format documentation "Volume Shadow Snapshot (VSS) format".
- Microsoft Learn: Hyper-V backup with VSS in the guest and auto-recovery of the snapshot volume (approach B uses the same principle).

## User input

- Report "MSSQL-Sicherung auf XCP-ng – Zielarchitektur & Spezifikation für die Eigenentwicklung" (2026-11-04). Only the parts based on public sources are used; see `shape.md`.
