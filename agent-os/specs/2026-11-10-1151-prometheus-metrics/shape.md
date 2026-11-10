# Prometheus Metrics — Shaping Notes

## Scope

The server exposes Prometheus metrics, so that the backup cycle, the pools' storage and the server itself can be measured over time in the existing Prometheus/Grafana.

**In:**
- `GET /metrics` on the HTTPS port (9555), protected by a metrics-only API token (the scrape token).
- **Backup cycle:**
  - snapshot time, wait for the storage gate, disk-to-disk gap (skew), release time;
  - VSS hold and freeze time, consistency;
  - bytes by transport and category, full reads by reason, transport fallbacks, NBD connects and reconnects.
- **Sessions and VMs:** sessions by kind and result, running and queued, per-VM last backup, result, stale and no restore point (the console's rule), last snapshot time and skew.
- **Pool and storage state:** polled read-only from XAPI. Size, used, virtual allocation, minimum free ratio, VDIs by kind, leftover snapshots by owner, disks with and without CBT, poll health.
- **Server and repository:**
  - repository size, free space, stored bytes and restore points;
  - agents online and offline;
  - SQL log backup age;
  - pending snapshot cleanups;
  - the .NET runtime, HTTP and Kestrel meters.
- **Delivered with it:** a Grafana dashboard, Prometheus alert rules (a plain rule group and a `PrometheusRule`), and scrape configuration examples.

**Not in:**
- push or OTLP export, and traces;
- notifications by mail or webhook (the Status & alerting roadmap item; Prometheus Alertmanager covers alerting for now);
- changing how restore points are graded for consistency. That is a separate change from the research report; this spec only measures the skew.

## Why

The research report `reports/XCP-ng snapshot speed and SAN backup.md` (2026-11-10) found that the deciding numbers exist only in session logs. These are the per-disk snapshot cost, the disk-to-disk gap, NBD versus the HTTP fallback, and the free space and VDI count per storage. The next steps all need before/after numbers: opening port 10809, `[NOBAK]` on config drives, VM placement, parallel per-SR snapshots, QCOW2.

## Decisions

### Confirmed by the user (2026-11-10)
- **Scraper:** the existing Prometheus on the rke2 cluster in the Corp LAN (Grafana ingress on 10.26.x).
- **Protection:** a dedicated scrape token that can read `/metrics` only. User API tokens keep full rights, so a leaked scrape config cannot do anything else.
- **Scope of the first version:** all four areas: backup cycle timings, pool and storage state, server and repository, Grafana dashboard plus alerts.

### Technical
- **Instruments:** `System.Diagnostics.Metrics` from the BCL. The meters are `Backdafuckup` (server) and `Backdafuckup.Xen` (provider).
- **Exporter:** `OpenTelemetry.Extensions.Hosting` with `OpenTelemetry.Exporter.Prometheus.AspNetCore` (Apache-2.0).
  - The exporter is still labelled beta. It is the one Microsoft documents for ASP.NET Core and it is released regularly; prometheus-net has had no release since January 2024 (8.2.1).
  - Fallback: our own text exporter on a `MeterListener`.
- **Pull only:** nothing leaves the server.
- **Scrape token:** an API token with the scope `metrics`. The fallback and default authorization policies refuse metrics tokens, and `/metrics` has its own policy.
- **Skew:** `max − min` of `snapshot_time` over the VM snapshot's VDIs (XAPI, 1 s resolution). `VM.snapshot` snapshots the disks one after another (`xapi_vm_clone.ml`, `safe_clone_disks`).
- **No work in the scrape path:** background services refresh cached snapshots of the catalog (every minute) and the pools (every 5 minutes). Gauges only read them.
- **Labels:** pool (connection name), storage (SR name, `+`-joined for several), job, VM, transport, fixed reason codes. No user input beyond names, and no secrets.

## Context

- **Visuals:** none.
- **References:** see `references.md`.
- **Product alignment:** roadmap Phase 1 "Status & alerting" (job status, success/failure reporting). The tech stack names only Serilog for observability so far; it gets an Observability entry.
