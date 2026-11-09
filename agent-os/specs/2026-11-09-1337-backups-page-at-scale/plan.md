# Plan: Backups & Restore at scale (200–300 VMs in many jobs)

Spec folder: `agent-os/specs/2026-11-09-1337-backups-page-at-scale/`

## Context

**Measured on 2026-11-09** on a local instance with 280 VMs in 13 jobs and 6,092 restore points (seed `scratchpad/scale-seed.py`, port 9556, headless Chrome):

| VMs | Points | Until shown | DOM nodes | Clear the filter |
|---|---|---|---|---|
| 30 | 482 | 3.5 s | 41k | 2.2 s |
| 100 | 2,046 | 21 s | 187k | 26 s |
| 280 | 6,092 | 176 s | 546k | — |

**Causes:**
1. **Rendering:** `Backups.razor` renders every VM panel with its full table even when it is collapsed: 6,092 rows and 12,184 `MudMenu`s. Style recalculation alone took 143 s, and the text filter re-renders everything on each keystroke.
2. **API:** `GET /api/restore-points` returns every point with its disks, NICs and VM JSON: 7.0 MB, 0.5–1 s, with no response compression.
3. **Overview:**
   - about 19 screen heights;
   - alphabetical only, with no grouping by job;
   - the VM header shows only the job of its newest point;
   - no sorting and no marker for missed backups;
   - a text filter only.

**User decisions:**
- No quick fix; the summary endpoint and the new UI are built (b + c).
- Grouped **by job** by default; switching to **by VM** is always possible.
- **Stale** follows the job's schedule.
- Job members **without a restore point** are shown.
- No visuals; the page follows the style of the existing pages.

## Decisions

### Server: one row per VM, points on demand
**New `GET /api/restore-points/vms`** returns `List<BackupVmDto>`.
- **One row per VM** (`VmNativeId`), with:
  - its jobs as `BackupVmJobDto`: job id, name, exists, schedule kind, enabled, points, latest time, latest point id, state, "expected since";
  - VM-wide values: point count, latest point (id, time, kind, consistency and reason, logical bytes, SQL count, DC flag and AD domain), stored bytes, repository ids, connection id.
  - The rows carry no disks, NICs or VM JSON.
- **Job members without points:** they come from `BackupJobObject` (`NativeId`, `CachedName`), with state `NoPoint`.
- **Without job:** points whose `JobId` is null or whose job was deleted go under a "without job" entry (job id null, `exists = false`).
- **Server-side filters** (query parameters) give the UI and API users one logic:
  - `q`: VM or job name;
  - `jobId` (repeatable), `repositoryId`;
  - `content`: `ad`, `sql`;
  - `consistency`: `application`, `crash`, `warning`;
  - `state`: `stale`, `nopoint`, `ok`;
  - `olderThanDays`.
- **Query:** EF aggregation over `restore_points` (`Available`), grouped by VM and job (count, max(created), sum(new bytes)), plus the latest point per VM (ROW_NUMBER), with no `Include(Disks)`. Target: under 100 ms at 6k points. Measure at 60k too; an index (migration) only if the query takes over 200 ms.

**Points of a VM:** the existing `GET /api/restore-points?vmId=…[&jobId=…]` serves them, unchanged. That keeps the API compatible for scripts and tests.

**Stale** (new static `BackupVmStates`; new `ScheduleCalculator.Previous(schedule, atOrBeforeUtc)`, mirroring `Next`, DST aware):
- `last` is the most recent scheduled start at or before now − 12 h.
- A VM in that job is `Stale` when its newest point in that job is older than `last`, meaning it missed a run that started over 12 h ago.
- Exceptions:
  - jobs without a schedule are `Manual` and never stale;
  - disabled jobs are `JobDisabled`;
  - deleted jobs are `NoJob`.
- A VM counts as stale if one of its scheduled jobs is stale.
- The 12 h grace is a constant with a comment, not a setting.

**Response compression:**
- `AddResponseCompression` with Brotli and Gzip (level Fastest) for `application/json`, `EnableForHttps = true`.
- A custom `IResponseCompressionProvider` (or a path check) skips `/api/setup`, `/api/auth` and `/api/tokens`. Their bodies can carry secrets (BREACH).
- Static assets already come precompressed through `MapStaticAssets`, so they are not affected.

### Console: `Backups.razor` rebuilt
- **Toolbar** (`PageHeader` actions and a filter row that wraps on a phone):
  - search, debounced 300 ms;
  - *Group by* Job | VM;
  - Job (multi-select), Repository;
  - content chips AD, SQL;
  - Consistency;
  - State: stale, no restore point;
  - *Last point older than* 1, 2, 7 or 30 days;
  - *Sort*: name, last point (oldest first), size;
  - *Reset*, and "n of m VMs".
- **URL:** every filter, the grouping and the sort are query parameters (`[SupplyParameterFromQuery]`). Changes call `NavigationManager.NavigateTo(GetUriWithQueryParameters(...), replace: true)`, so links, reload and Back work.
- **By job:**
  - **Group header:** one per job, showing name, schedule (daily 22:00, weekly …, manual, disabled, deleted), VMs, stale, no point, warnings, and a collapse toggle.
  - **VM rows:** a compact row per VM showing:
    - name;
    - last point, relative and absolute;
    - point count, size;
    - chips: AD, SQL, crash/warning;
    - the state marker (stale since, no restore point);
    - *Restore* for the latest point.
  - **Without job:** that group comes last.
- **By VM:** a flat list. Instead of one job, the row shows all of the VM's jobs.
- **Expanding a VM row:**
  - loads its points (in job view, those of that job) and renders the existing points table, moved into the component `RestorePointTable.razor` with Restore, Verify and Delete;
  - a delete reloads that VM and the summary only;
  - only expanded rows render points.
- **No virtualization:** expanded rows have variable heights, which `Virtualize` does not handle well together with grouping. Each group, and the flat list, shows 100 rows plus *Show all n*. With summary rows only, that is about 30 DOM nodes per VM.
- **Menus:** *Restore* stays a `MudMenu` per summary row (≤ 300), measured. If it is still slow, one shared menu.

### Docs
- **Admin guide:** a new section "Backups & Restore": grouping, filters, the stale rule, links with filters, the API endpoint.
- **Roadmap:** Web UI.
- **Runbook:** a console check with the scale seed (see Verification).

## Tasks

1. **Save the spec documentation** in `agent-os/specs/2026-11-09-1337-backups-page-at-scale/`:
   - `plan.md`: this plan;
   - `shape.md`: measurements, decisions, user answers;
   - `standards.md`: the conventions of the earlier specs, plus additions: no full lists to the browser; no secrets in compressed responses; Playwright check at 1400/1024/390;
   - `references.md`: `Backups.razor`, `Sessions.razor` filter, `SqlDatabases.razor` `MudTable`, `ScheduleCalculator`, `OperationEndpoints.MapRestorePoints`, `ApiMapping.ToDto`;
   - `visuals/`: none; the measurement screenshots go here.
2. **Server and contracts:**
   - `BackupVmDto`, `BackupVmJobDto` and the state strings in `src/Backdafuckup.Contracts/Api.cs`;
   - `ScheduleCalculator.Previous`;
   - `BackupVmStates`;
   - the endpoint `/api/restore-points/vms` with filters in `src/Backdafuckup.Server/Api/` (new `BackupVms.cs`, registered in `OperationEndpoints.MapRestorePoints`);
   - response compression in `Program.cs`.
3. **Server tests:**
   - `ScheduleCalculatorTests`: Previous for daily, weekly and DST;
   - state tests: ok, stale, no point, manual, disabled, deleted job, VM in two jobs;
   - an API test `ApiIntegrationTests.Backups.cs`: rows, job members without points, without job, each filter, `Content-Encoding: br` on `/api/restore-points/vms`, no compression on `/api/auth/login` and `/api/tokens`.
4. **Console:**
   - the new `Backups.razor`;
   - `RestorePointTable.razor`, taken out of today's table;
   - the URL parameters;
   - lazy points per VM;
   - *Show all n*.
5. **Scale check:** the seed (280 VMs / 6,092 points, plus a run with 1,000 VMs / ~20k points) on the local instance.
   - Measure with Playwright: first view, filter change, expand, delete.
   - Layout at 1400/1024/390 (no horizontal overflow); screenshots.
6. **Docs:** admin guide, roadmap, runbook check.

**Before task 2:** the H15 change waiting in the working tree (AD explore only for domain admins) is committed on its own, once the user approves.

## Verification

- **Tests:** `dotnet test --project tests/Backdafuckup.Server.Tests` (new tests), then the full suite `--solution backdafuckup.slnx`.
- **Scale targets** (local, headless Chrome, 280 VMs / 6,092 points):
  - first view under 2 s;
  - filter or grouping change under 300 ms after the debounce;
  - expanding a VM under 500 ms;
  - `/api/restore-points/vms` under 150 ms and compressed under 100 KB.
  
  With 1,000 VMs: first view under 5 s.
- **Compatibility:** `GET /api/restore-points?jobId=…|vmId=…` is unchanged. The existing tests at `ApiIntegrationTests.cs:245/455/1599/2033` keep passing.
- **On backup01 after deploy:** the real 4 VMs grouped by job, Restore entries (VM, files, SQL, AD) as before.
