# Status

| Step | State |
|---|---|
| 1 — one snapshot per storage, `[NOBAK]`, free-space check, timing, change-token hardening | done, deployed on backup01 (2026-11-04) |
| 1 — measure single-VM snapshot times on Alletra | done: 24.7 s (3 disks), 46.9 s (4 disks); still over 10 s |
| 2a — spike: T-SQL snapshot backup on SQL Server Express (sql1 runs 2025 Express, 17.00.1000) | done (2026-11-04): works on Express; per-database suspend, not GROUP; session end resumes; see `spike.md` |
| 2 — T-SQL snapshot backup (approach A) | built (2026-11-04): job method "SQL Server snapshot" with a SQL account; agent job snapshot-suspend (one connection per database); `.bkm` in the restore point; per-database consistency. Tests: engine (fake agent), API (real agent, scripted SQL), SQL Server 2022 container. **Lab 2026-11-04 21:58, bdfu-lab-sql1 (SQL Server 2025 Express):** 3 user databases suspended and recorded in msdb (copy-only, is_snapshot); writes suspended 43.3 s for a 4-disk snapshot on alletra-mp02; `.bkm` of about 34 KB stored. `shop` and `small` application-consistent, `imoltp` too (not restorable: In-Memory container). Published `small` from it, which attached cleanly. VSS fallback for VMs without SQL Server 2022+ (`8aa2132`). |
| 2 — backup type full / copy-only | built (2026-11-05): job setting, full by default for new jobs; log-chain facts and warnings; PITR path checked in container tests (see `plan.md`, step 4). Lab open. |
| 3a — spike: shadow copy readable in the XAPI snapshot | open |
| 3 — in-guest shadow copy with read-time revert (approach B) | open |
| 4 — native SQL backups and PITR | own spec; the restore path with log backups works in container tests (2026-11-05) |
