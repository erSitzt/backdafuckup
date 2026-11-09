# References for Backups & Restore at Scale

## In this codebase

### Today's page
- **Location:** `src/Backdafuckup.Web/Pages/Backups.razor`
- **Relevance:** the page that is rebuilt.
- **Key patterns:**
  - the points table: consistency chips, the AD and SQL chips, the *Restore* menu (VM, files, SQL, AD) and Verify/Delete;
  - it moves into `RestorePointTable.razor` and is rendered only for an expanded VM.

### The restore point API
- **Location:** `src/Backdafuckup.Server/Api/OperationEndpoints.cs` (`MapRestorePoints`), `src/Backdafuckup.Server/Api/ApiMapping.cs` (`ToDto`), `RestorePointEntity` in `src/Backdafuckup.Server/Data/Entities.cs`.
- **Relevance:** the new `/vms` endpoint sits next to it. `GET /api/restore-points?vmId=…&jobId=…` serves the expanded rows.
- **Key patterns:** only `Available` points; ordered by VM name, then newest first.

### Jobs and schedules
- **Location:**
  - `BackupJob`, `JobSchedule`, `BackupJobObject` (`NativeId`, `CachedName`) in `Entities.cs`;
  - `src/Backdafuckup.Server/Engine/ScheduleCalculator.cs` (`Next`, DST aware);
  - its tests in `tests/Backdafuckup.Server.Tests/ScheduleCalculatorTests.cs`.
- **Relevance:**
  - `Previous` mirrors `Next` for the stale rule;
  - the job members give the "no restore point yet" rows.

### Filters and tables in the console
- **Location:** `src/Backdafuckup.Web/Pages/Sessions.razor` (type filter in `PageHeader` actions), `src/Backdafuckup.Web/Pages/SqlDatabases.razor` (`MudTable` with row click).
- **Relevance:** the toolbar and the compact rows follow their style.

### Dashboard counts
- **Location:** `/api/dashboard` in `OperationEndpoints.cs`.
- **Relevance:** "protected" VMs come from the same restore points. It stays as it is (20 ms with 6,092 points).

## Measurement tools (scratchpad, not in the repo)

- **`scale-seed.py`:** generates the catalog rows (COPY files) for 280 VMs in 13 jobs.
- **`scale-ui/diag.py`, `curve.py`:** Playwright with CDP performance metrics (script, style, layout time, DOM nodes).
