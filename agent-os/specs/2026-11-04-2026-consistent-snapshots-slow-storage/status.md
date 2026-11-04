# Status

| Step | State |
|---|---|
| 1 — one snapshot per storage, `[NOBAK]`, free-space check, timing, change-token hardening | built and tested (2026-11-04); not deployed yet |
| 1 — measure single-VM snapshot times on Alletra | open (after deployment, lab job sql1 + sql2) |
| 2a — spike: T-SQL snapshot backup on SQL Server 2022 Express | open |
| 2 — T-SQL snapshot backup (approach A) | open |
| 3a — spike: shadow copy readable in the XAPI snapshot | open |
| 3 — in-guest shadow copy with read-time revert (approach B) | open |
| 4 — native SQL backups and PITR | own spec, after step 2 |
