# Status

| Task | State | Notes |
|---|---|---|
| 1. Spec documentation | done (2026-11-06) | `plan.md`, `shape.md`, `standards.md`, `references.md`, `visuals/` (none) |
| 2. Lab forest via OpenTofu | done (2026-11-06), apply open | `deploy/lab`: `ad_forest` (first DC of a new forest, test data: OU Lab with 30 users, groups incl. nested, computers, GPOs A/B/C with links; Recycle Bin off by default, `ad_recycle_bin`), `ad_replica` (another DC). Rendered and parsed with PowerShell's parser; not applied yet. |
| 3. Spikes | open | needs the lab DCs. Also check that the NTDS writer's component is added (`DescribeWriters` adds only selectable components) and that `repadmin /showbackup` shows the VSS full backup. |
| 4. DC awareness | done (2026-11-06), lab check open | Agent: domain role and domain in the host info (WMI), capability `ad-backup-v1`, at prepare on a DC the directory facts (`WindowsAdFacts`: NTDS writer files or registry, SYSVOL, rootDSE over LDAP, invocationId, NetBIOS name, lifetimes, Recycle Bin). Server: `agents.domain_role`/`domain`, `AgentDto.DomainController`; DCs get VSS full (`CopyOnly = false`), unless the job takes SQL log backups and the VM's latest restore point has SQL databases (then copy-only with a warning); guest metadata section `ActiveDirectory` (additive, format stays v2) with consistency from the NTDS writer; manifest and `restore_points.domain_controller`/`ad_domain`, backfilled from the guest metadata (directory section or NTDS writer); "AD" chip on restore points. The Restore menu entry follows with the AD page (task 10). Tests: two engine tests (DC with VSS full and backfill; DC with SQL log backups), API agent check. |
| 5. Safe DC VM restore | done (2026-11-06), lab check open | XAPI gives every metadata import that is not a live migration or a template a fresh `generation_id` (xen-api `ocaml/xapi/import.ml`, `do_import`: "Generate a fresh genid for normal VM imports"), also with `restore=true`. The manifest keeps the VM's `generation_id` (`VmDescriptor.GenerationId`); the restore of a DC restore point logs the non-authoritative restore, compares the new VM's id (warning if it is missing, `0:0` or unchanged) and warns when a copy at another location is powered on. Restore wizard: info for DC restore points, warning for a powered-on copy. Fake pool: fresh id on import, `ImportKeepsGenerationId` for the warning path. |
| 6. Agent AD layer | open | |
| 7. Server explore sessions | open | |
| 8. Object restore | open | |
| 9. GPO restore | open | |
| 10. Console | open | |
| 11. Docs | open | |
| 12. Lab checks | open | |
