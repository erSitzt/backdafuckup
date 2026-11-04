# Status

| Step | State |
|---|---|
| 1 — one snapshot per storage, `[NOBAK]`, free-space check, timing, change-token hardening | done, deployed on backup01 (2026-11-04) |
| 1 — measure single-VM snapshot times on Alletra | done: 24.7 s (3 disks), 46.9 s (4 disks); still over 10 s |
| 2a — spike: T-SQL snapshot backup on SQL Server Express (sql1 runs 2025 Express, 17.00.1000) | done (2026-11-04): works on Express; per-database suspend, not GROUP; session end resumes; see `spike.md` |
| 2 — T-SQL snapshot backup (approach A) | open |
| 3a — spike: shadow copy readable in the XAPI snapshot | open |
| 3 — in-guest shadow copy with read-time revert (approach B) | open |
| 4 — native SQL backups and PITR | own spec, after step 2 |
