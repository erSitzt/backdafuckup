# Backups & Restore at Scale — Shaping Notes

## Scope

The page *Backups & Restore* stays usable with 200–300 VMs in many jobs, and points out missed backups.

**In:**
- **Server:**
  - one summary row per VM (`GET /api/restore-points/vms`) with server-side filters;
  - the points of a VM loaded only when it is expanded;
  - JSON response compression.
- **Console:**
  - grouping by job (the default) or by VM;
  - filters: text, job, repository, content (AD, SQL), consistency, state, age;
  - sorting;
  - a marker for VMs that missed a scheduled run, and for job members without any restore point;
  - filters, grouping and sort in the URL;
  - long lists cut at 100 rows per group with *Show all n*.

**Not in:**
- virtualization (expanded rows have variable heights; with summary rows only it is not needed up to about 1,000 VMs);
- a configurable stale threshold;
- the VM pickers of the job editor and the inventory page (not measured with 300 VMs yet).

## Measurements (2026-11-09)

Local instance: 280 VMs in 13 jobs, 6,092 restore points (12 VMs with no point for 9 days, 10 DCs, about 25 SQL VMs, 20 VMs also in a monthly archive job). Headless Chrome in Docker on WSL.

| VMs | Points | Until shown | DOM nodes | Clear the filter |
|---|---|---|---|---|
| 30 | 482 | 3.5 s | 41k | 2.2 s |
| 100 | 2,046 | 21 s | 187k | 26 s |
| 280 | 6,092 | 176 s | 546k | — |

At 280 VMs:
- **Browser time:** 143 s style recalculation, 23 s script, 5 s layout.
- **Rendered while collapsed:** 6,092 table rows and 12,184 menus are in the DOM although every panel is collapsed.
- **API:** `GET /api/restore-points` returns 7.0 MB of JSON, uncompressed, in 0.5–1 s. The dashboard answered in 20 ms.

## Decisions

- **User's choices:**
  - the summary endpoint and the new UI (b + c), no separate quick fix;
  - default grouping by job; by VM is a switch;
  - "stale" follows the job's schedule: a VM missed the most recent scheduled start that is at least 12 h old. Jobs without a schedule never mark; disabled and deleted jobs are named as such;
  - job members without a restore point are shown;
  - no visuals; the style of the existing pages.
- **Filters on the server,** so the console and API users share one logic. Grouping and sorting are in the console, over the returned rows.
- **`GET /api/restore-points` stays as it is** (with `jobId` and `vmId`) for scripts and the expanded rows.
- **No compression of secret-bearing responses** (`/api/setup`, `/api/auth`, `/api/tokens`) because of BREACH.
- **H15 first:** the H15 change (AD explore for domain admins only) is committed on its own, before this work.

## Context

- **Visuals:** none. `visuals/before-280-vms.png` is the old page with the seed data.
- **References:** see `references.md`.
- **Product alignment:** the roadmap's Web UI aims at a console that admins of larger environments can switch to; a list of every restore point does not get there.

## Standards Applied

See `standards.md`.
