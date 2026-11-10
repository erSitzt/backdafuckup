# Status

| Task | State |
|---|---|
| 1. Spec documentation | done |
| 2. Packages and host: OpenTelemetry 1.20.1 / Prometheus exporter 1.20.1-beta.1, `/metrics`, policies, `MetricsOptions` | done |
| 3. Scrape token: scope `full`/`metrics` on API tokens, migration `AddApiTokenScope`, 403 with a message, console choice | done |
| 4. Xen facts: `SnapshotFacts` (duration, gate wait, disks, storages, skew from VDI `snapshot_time`), release time, `IBackupTelemetry` (NBD connects, fallbacks, reconnects), full-read reason codes; session log line "Disks of … captured over N s" | done |
| 5. Engine instruments: `BackupMetrics` (snapshots, disks, transports, sessions, VM backups, restores, per-VM last values) | done |
| 6. State gauges: `StateMetrics` + `MetricsStateService` (catalog every minute, pools every 5 minutes, read only via `GetStorageStateAsync`) | done |
| 7. Tests: engine (`EngineIntegrationTests.Metrics.cs`), API (`ApiIntegrationTests.Metrics.cs`) | done |
| 8. Monitoring files: `deploy/monitoring/` (rules, `PrometheusRule`, dashboard, README with scrape configs) | done |
| 9. Docs: admin guide *Monitoring with Prometheus*, port table, API tokens; roadmap; tech stack; third-party notices | done |
| Deploy to backup01 | done 2026-11-10 14:03 (`6694c12`), health 200, `/metrics` 200 with the full API token |
| Fix: SQL log gauges leave out dropped databases | built, tested; not deployed yet |
| The rke2 scrape | done 2026-11-10: Secret `backdafuckup-scrape` (token, `ca.pem`) and `ScrapeConfig` `backdafuckup` in namespace `prometheus` of rke2-infra; target `up` (0.04 s) |
| Grafana dashboard | done 2026-11-10: grafana-rke2, folder *backdafuckup*, uid `backdafuckup-overview`, data source `prom-rke2-infra`; queries return data (no image renderer there, so not looked at as a picture) |
| Alert rules in rke2 | done 2026-11-10 14:55: `PrometheusRule` `backdafuckup` in namespace `prometheus`, 13 rules loaded, all healthy, none active. The Alertmanager routes them to its `null` receiver (only some Kubernetes alerts go to MS Teams), so they are visible in Prometheus, Alertmanager and Grafana but notify no one |

## Checks (2026-11-10)

- **Full suite:** 370 tests, 343 passed, 27 skipped, 0 failed.
- **Local instance** (scale seed, 280 VMs, 6,091 restore points):
  - `/metrics` returns 1,498 lines in the text format 0.0.4.
  - Anonymous access gets 401.
  - Names are without doubled suffixes, and 305 `bdfu_vm_stale` series match the console's rows.
  - `target_info` names the service `backdafuckup`.
- **Prometheus v3.5.0 in Docker:**
  - it scraped the local instance with a metrics token (target `up`);
  - all 36 dashboard queries parsed;
  - all 13 alert rules evaluated without error (`promtool check rules`: SUCCESS).
- **Label names:** `job` and `instance` are taken by Prometheus' target labels, so the metrics use `backup_job` and `sql_instance`. Otherwise Prometheus would rename them to `exported_job` / `exported_instance`.

## First values on backup01 (2026-11-10 14:05)

- **Pool and agents:** pool "10.16.1.231" (the connection's name) read in 0.2 s, `bdfu_pool_poll_success` 1. Five agents online.
- **alletra-mp02:**
  - 3.26 of 4.40 TB used;
  - 55 disks, 10 snapshot disks, 56 `cbt_metadata`, 13 base copies, so 134 logical volumes before the CBT logs.
- **alletra-mp01:** 1.20 TB used; 24 disks, 16 `cbt_metadata`, 1 base copy.
- **Snapshots in the pool:** 4, none of them ours. 26 of 79 disks are without CBT.
- **False alarm found and fixed:**
  - `shop_restored_pit_published` on bdfu-lab-sql1 was dropped after its last log backup (2026-11-09 14:15 UTC). It stayed in the catalog and would have raised *BackdafuckupSqlLogBackupLate* for good.
  - Fix: a database that the newest run of its VM and instance no longer saw (more than one interval later) is left out.

## Not covered yet

- Real numbers from backup01: snapshots on alletra-mp01/02, skew of the SQL VMs, HTTP export versus NBD before and after port 10809 is opened.
- The dashboard has not been looked at in Grafana. Its queries were checked against Prometheus only.
