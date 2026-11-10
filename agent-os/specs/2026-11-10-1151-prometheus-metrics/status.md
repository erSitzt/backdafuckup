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
| Deploy to backup01 and the rke2 scrape | open (needs approval; the user creates the metrics token and applies the scrape config and rules) |

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

## Not covered yet

- Real numbers from backup01: snapshots on alletra-mp01/02, skew of the SQL VMs, HTTP export versus NBD before and after port 10809 is opened.
- The dashboard has not been looked at in Grafana. Its queries were checked against Prometheus only.
