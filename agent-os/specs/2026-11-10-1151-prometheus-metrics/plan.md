# Plan: Prometheus metrics

Spec folder: `agent-os/specs/2026-11-10-1151-prometheus-metrics/`

## Context

**Why now.** The research report `reports/XCP-ng snapshot speed and SAN backup.md` showed that the numbers we need already exist, but only in session logs that are hard to compare:
- snapshot time per storage;
- wait for the storage gate;
- how long VSS held the writes;
- the gap between the first and last disk capture;
- which transport each disk used, and NBD failures;
- bytes read versus bytes changed;
- free space and VDI count per storage.

The goal is to measure them over time in the user's existing Prometheus/Grafana. Examples:
- the effect of opening port 10809;
- `[NOBAK]` on config drives;
- VM placement;
- QCOW2 tests.

**Current state** (from exploring the code):
- No metrics code exists: no `System.Diagnostics.Metrics`, no OpenTelemetry, no Prometheus. `/health` is anonymous and has no checks.
- Most values are computed but thrown away or only logged:
  - `snapshotTime`, the gate's `Lease.Waited`, and the free space from `CheckStorageSpaceAsync` in `XenBackupSession.CreateAsync`;
  - `GuestFreezeResult.HeldTime` / `FreezeTime`;
  - `DiskBackupCounters`, and `DiskBackupStats.Transport`;
  - the NBD failures in `OpenNbdAsync`.
- The disk-to-disk gap is not measured yet. XAPI sets `snapshot_time` on each snapshot VDI, so the gap is `max − min` over the VM snapshot's VDIs, at 1 s resolution.

**User decisions (2026-11-10):**
- Scraped by the existing Prometheus on the rke2 cluster in the Corp LAN.
- Protected by a dedicated scrape token.
- The first version covers all four areas:
  - backup cycle timings;
  - pool and storage state;
  - server and repository;
  - Grafana dashboard and alert rules.

## Decisions

### Library and endpoint
- **Instruments:** BCL `System.Diagnostics.Metrics` (`Meter` from `IMeterFactory`, `Counter`, `Histogram`, `ObservableGauge`), so the exporter can be swapped later.
- **Exporter:** `OpenTelemetry.Extensions.Hosting` (stable 1.20.x) plus `OpenTelemetry.Exporter.Prometheus.AspNetCore` (1.20.x-beta, Apache-2.0).
  - That exporter is still labelled beta, but it is the one Microsoft documents for ASP.NET Core and it is actively released.
  - prometheus-net has had no release since January 2024.
  - Fallback if the beta causes trouble: our own small text exporter on a `MeterListener`.
- **Pull only:** no OTLP and no push. Nothing leaves the server, which matches the on-premises rule.
- **Meters:**
  - our own: `Backdafuckup` (server/engine) and `Backdafuckup.Xen` (provider: NBD connects, fallback);
  - built in: `System.Runtime`, `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Server.Kestrel`, `System.Net.Http`, `Npgsql`.
- **Histogram buckets:** set per instrument (snapshot and wait seconds: 1, 2, 5, 8, 10, 15, 20, 30, 45, 60, 90, 120, 300). The 10 s bucket marks the VSS limit.
- **Endpoint:** `GET /metrics` on the existing HTTPS port 9555, via `app.MapPrometheusScrapingEndpoint("/metrics").RequireAuthorization("Metrics")`.
- **Configuration:** `"Metrics": { "Enabled": true, "CatalogInterval": "00:01:00", "PoolInterval": "00:05:00" }`.

### Scrape token (API token with a scope)
- `ApiTokenEntity` gets `Scope` (`full` by default, or `metrics`), in migration `AddApiTokenScope`.
- `CreateApiTokenDto` and `ApiTokenDto` carry the scope.
- The console page `ApiTokens.razor` gets a choice: "Full API access" or "Metrics only (Prometheus)".
- `ApiTokenAuthenticationHandler` adds the claim `bdfu:token-scope`.
- **Authorization:**
  - The fallback **and** the default policy become "authenticated, and not a metrics-only token". `LiveHub`'s `[Authorize]` uses the default policy.
  - The new policy `Metrics` allows signed-in users, full tokens and metrics tokens.
  - A metrics token anywhere else gets 403 with a message that names the token's scope.
- Tokens still cannot create tokens.

### What is measured
Prefix `bdfu_`. The `pool` label is the connection name. `storage` is the SR name label; for VMs spanning several SRs it is the sorted names joined with `+`.

**Backup cycle.** Recorded in the server from facts the Xen session now exposes on `IVmBackupSession`:
- new neutral record `SnapshotFacts` in Core: `Duration`, `GateWait`, `DiskCount`, `Storages`, `DiskSkew`, `ReleaseDuration`;
- `GuestFreezeResult` already exists.

| Metric | Type | Labels |
|---|---|---|
| `bdfu_snapshot_duration_seconds` | histogram | pool, storage, disks |
| `bdfu_snapshot_gate_wait_seconds` | histogram | pool, storage |
| `bdfu_snapshot_disk_skew_seconds` (VMs with ≥ 2 disks) | histogram | pool, storage |
| `bdfu_snapshot_release_seconds` (`VM.destroy` + `data_destroy`) | histogram | pool, storage |
| `bdfu_guest_writes_held_seconds`, `bdfu_guest_freeze_seconds` | histogram | method (vss, sql) |
| `bdfu_snapshots_total` | counter | pool, storage, consistency (application, crash), within_hold (true/false) |
| `bdfu_backup_disk_bytes_total` | counter | pool, storage, transport, category (read, unchanged, zero, deduped, new, stored) |
| `bdfu_backup_disk_read_seconds_total` | counter | pool, storage, transport |
| `bdfu_backup_full_reads_total` | counter | pool, reason (fixed codes from `ChangedExtents.Full`) |
| `bdfu_transport_fallbacks_total` | counter | pool, from, to |
| `bdfu_nbd_connect_seconds` | histogram | pool, host, result (ok, timeout, refused, tls) |
| `bdfu_nbd_reconnects_total` | counter | pool, host |

The session log gets one new line: "Disks captured over X s (first … , last …)", naming the VM and the storages.

**Sessions and VMs:**
- `bdfu_sessions_total{kind,result}` and `bdfu_session_duration_seconds{kind,result}`.
- `bdfu_sessions_running{kind}` and `bdfu_sessions_queued{kind}`, from `RunningSessions` and the queue.
- `bdfu_vm_backups_total{job,result}`.
- Per-VM gauges, refreshed from the catalog every `CatalogInterval`. They reuse the rows of `/api/restore-points/vms` (`BackupVms.cs`, `BackupVmStates`), so the console's stale rule and Prometheus agree:
  - `bdfu_vm_last_backup_timestamp_seconds{job,vm}`;
  - `bdfu_vm_last_backup_result{job,vm,result}`, with value 1 on the current result;
  - `bdfu_vm_stale{job,vm}`;
  - `bdfu_vm_no_restore_point{job,vm}`;
  - `bdfu_vm_restore_points{job,vm}`;
  - `bdfu_vm_last_snapshot_seconds{vm}`;
  - `bdfu_vm_last_disk_skew_seconds{vm}`.

**Pool and storage state.** A new background service polls each connection every `PoolInterval` through a new provider call, `IHypervisorProvider.GetStorageStateAsync`. The Xen implementation is read-only and uses `get_all_records` of SR, VDI and VM:
- `bdfu_storage_size_bytes`, `bdfu_storage_used_bytes`, `bdfu_storage_virtual_allocation_bytes` (pool, storage, type);
- `bdfu_storage_min_free_ratio`, from `Xen:MinStorageFreePercent`, for alerts;
- `bdfu_storage_vdis{pool,storage,kind}`, where kind is disk, snapshot, cbt_metadata or unmanaged. Unmanaged covers base copies, and the count stands in for the LV count;
- `bdfu_pool_snapshots{pool,owner}` (owner is backdafuckup by our `other_config` tag, or other) and `bdfu_pool_snapshot_oldest_seconds{pool,owner}`;
- `bdfu_pool_vm_disks{pool,cbt}`;
- `bdfu_pool_poll_success{pool}` and `bdfu_pool_poll_seconds{pool}`.

**Server and repository** (refreshed every `CatalogInterval`):
- `bdfu_repository_size_bytes`, `bdfu_repository_free_bytes` (`GetStorageStats`), `bdfu_repository_stored_bytes`, `bdfu_repository_restore_points` (repository);
- `bdfu_agents{state}` (online, offline; offline means `LastContactUtc` older than 10 min) and `bdfu_agent_last_contact_timestamp_seconds{agent}`;
- `bdfu_sql_last_log_backup_timestamp_seconds{vm,instance,database}` and `bdfu_sql_log_backup_interval_seconds{…}`;
- `bdfu_artifacts_pending` (snapshots waiting for cleanup);
- the built-in .NET runtime, HTTP and Kestrel meters.

The scrape path never queries the database or XAPI. Gauges read a cached snapshot that the background service replaces as a whole.

### Grafana and alerts (`deploy/monitoring/`)
- `grafana-dashboard.json`, with panels for:
  - results and stale VMs;
  - snapshot time and skew per storage (percentiles plus a heatmap with the 10 s line);
  - gate wait;
  - transport mix and NBD failures;
  - bytes read versus changed;
  - storage free %, VDI counts and leftover snapshots;
  - repository free space;
  - sessions;
  - runtime.
- `backdafuckup.rules.yml` (a plain rule group) and `prometheusrule.yaml` (the `PrometheusRule` CRD for kube-prometheus-stack). Alerts:
  - scrape down;
  - a VM's last backup failed;
  - a VM is stale or has no restore point;
  - storage free space under the minimum (critical) or under twice the minimum (warning);
  - a transport fallback within 24 h;
  - one of our snapshots older than 6 h;
  - the pool poll failing;
  - repository free space under 10 %;
  - an agent offline;
  - SQL log backup lag over twice the interval.
- `scrape-config.md`, with:
  - a `ScrapeConfig` CRD and a plain `prometheus.yml` job for `backup01.corp.example:9555`, scheme https;
  - `authorization.credentials_file` from a Secret;
  - `tls_config.ca_file` = the server certificate (public part). TLS verification stays on.

## Tasks

1. **Save the spec documentation** in `agent-os/specs/2026-11-10-1151-prometheus-metrics/`:
   - `plan.md`;
   - `shape.md`: context, the user's answers, the library decision;
   - `standards.md`: earlier conventions, plus no metric queries in the scrape path, bounded label values, and no secrets in labels;
   - `references.md`: `XenBackupSession.CreateAsync`, `XenSnapshotGate`, `BackupSessionRunner`/`VmBackupTask`, `DiskBackupCounters`, `BackupVms.cs`, `ApiTokens.cs`, `FakeXenPool`.
2. **Packages and host:**
   - add both OpenTelemetry packages to `Directory.Packages.props` and `THIRD-PARTY-NOTICES.md`;
   - `Program.cs`: `AddOpenTelemetry().WithMetrics(...)` with the meters and bucket views, plus the `/metrics` mapping and the `Metrics` policy;
   - `MetricsOptions`.
3. **Scrape token:** the scope on entity, DTOs, handler claim and policies; the migration; the console token page.
4. **Xen facts:**
   - `SnapshotFacts` in Core and on `IVmBackupSession`;
   - measure the gate wait (also when there was no wait), the release time, and the skew from VDI `snapshot_time`, plus the new log line;
   - `Backdafuckup.Xen` meter for NBD connects, reconnects and fallback;
   - full-read reason codes;
   - `FakeXenPool` sets `snapshot_time` on snapshot VDIs.
5. **Engine instruments:** a `BackupMetrics` class (singleton, `IMeterFactory`) that records at the end of each VM task, at session end, and from `RestoreSessionRunner`.
6. **State gauges:**
   - `MetricsStateService` (BackgroundService) for the catalog and repositories, reusing the `BackupVms.cs` row logic;
   - `PoolStatePoller` with `IHypervisorProvider.GetStorageStateAsync` (Xen implementation and the fake pool's records).
7. **Tests:**
   - Engine, with a `MeterListener`: a backup records snapshot, skew, bytes by transport and session result; an NBD-off pool records the fallback.
   - API:
     - `/metrics` without a token gives 401;
     - a full token and a metrics token give 200 with `bdfu_` series;
     - a metrics token on `/api/jobs` gives 403;
     - a metrics token cannot create tokens.
   - Pool state against `FakeXenPool`.
8. **Monitoring files:** dashboard JSON, rules, `scrape-config.md`.
9. **Docs:**
   - admin guide: a new section "Monitoring with Prometheus" (scrape token, scrape config, TLS CA, metric list, alerts, dashboard) and a note in the port table;
   - roadmap: Status & alerting;
   - tech stack: Observability.

## Verification

- **Tests:** `dotnet test --project tests/Backdafuckup.Server.Tests` for the new tests, then the full suite with `--solution backdafuckup.slnx`.
- **Locally:** run the server against the fake/dev setup and `curl` `/metrics` with a metrics token. Check:
  - the series names;
  - histogram buckets including 10 s;
  - no label holding a secret.
  
  Lint the rule file with `promtool check rules` (in the `prom/prometheus` Docker image).
- **On backup01 after deploy** (ask first, outside running sessions):
  - read `/metrics` with the existing API token (full scope);
  - after the next scheduled backups, check snapshot, skew and transport series for dc1/dc2 and sql1/sql2;
  - storage and VDI gauges for alletra-mp01/02 against the inventory.
- **User:**
  - create a metrics-only token in the console;
  - add the scrape config and rules in rke2;
  - import the dashboard.
