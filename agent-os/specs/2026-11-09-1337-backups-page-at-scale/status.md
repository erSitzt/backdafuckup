# Status

| Task | State | Notes |
|---|---|---|
| 1. Spec documentation | done (2026-11-09) | `plan.md`, `shape.md`, `standards.md`, `references.md`, `visuals/` (before and after) |
| 2. Server and contracts | done (2026-11-09) | `GET /api/restore-points/vms` (`BackupVms`), `ScheduleCalculator.Previous`, JSON compression except `/api/setup`, `/api/auth`, `/api/tokens` |
| 3. Server tests | done (2026-11-09) | `ScheduleCalculatorTests` (Previous, DST), `BackupVmsTests` (states, filter values), `ApiIntegrationTests.Backups` (rows, jobs, deleted job, members without points, every filter, compression) |
| 4. Console | done (2026-11-09) | `Backups.razor` (by job / by VM, filters in the URL, lazy points), `RestorePointTable.razor`; plain HTML rows and one shared menu per page/table |
| 5. Scale check | done (2026-11-09) | see below |
| 6. Docs | done (2026-11-09) | admin guide "Backups & Restore", roadmap, runbook E4/E5, `scripts/dev/scale-catalog.py` |

## Scale check (2026-11-09)

Local instance, headless Chrome in Docker on WSL (no GPU), seed with 280 VMs and 6,092 restore points, then four times as many.

| | Before (280 VMs) | After (285 VMs) | After (1,120 VMs, 24k points) |
|---|---|---|---|
| API | `/api/restore-points` 7.0 MB, 0.5–1 s | `/vms` 73 KB (br), 40–85 ms | 254 KB (br), 140–210 ms |
| Page shown | 176 s | 1.2–1.6 s | 3.1–3.5 s |
| DOM nodes | 546,522 | 5,699 | 20,424 |
| Search, incl. 300 ms debounce | — (clear filter at 100 VMs: 26 s) | 0.9–1.0 s | 1.8 s |
| By VM / Show all | — | 0.85 s / 0.39 s | 1.06 s / 0.99 s |
| Expand a VM (14 points) | — | 0.57 s | 0.79 s |
| Horizontal overflow at 1024 / 390 px | — | 0 / 0 | 0 / 0 |

**Targets:**
- **Met:** first view under 2 s at about 300 VMs and under 5 s at 1,000; the API under 150 ms and 100 KB.
- **Missed:**
  - a filter change takes about 0.6 s after the debounce (target 0.3 s);
  - expanding a VM takes 0.57 s (target 0.5 s);
  - both are rendering in the browser (WebAssembly).

**Found while measuring:**
- **MudMenu cost:** a `MudMenu` per row keeps a popover each, which made 300 popovers and about 50 DOM nodes plus JS interop per row. Rows now use plain HTML and one menu opened at the click position.
- **Narrow screens:** on a phone the filters fill the first screen. A collapsible filter panel would help; not done.
