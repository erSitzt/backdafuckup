# Standards for Backups & Restore at Scale

There are no formal standards yet (`agent-os/standards/index.yml` is empty). These conventions apply:
- the foundation spec (`../2026-11-01-2134-xcpng-backup-foundation/standards.md`): REST and SignalR only, the console in MudBlazor;
- the FLR additions (`../2026-11-02-1713-file-level-restore/standards.md`): fail loudly, messages with context.

The additions below apply on top.

---

## Additions for this spec

### Lists in the browser
- **Never every row of a growing table:** the console gets summaries and loads details on demand (a VM's points when it is expanded).
- **Rendered means visible:** collapsed content is not rendered. A component per row (menus, tooltips) is counted against the row budget of about 30 DOM nodes per summary row.
- **Long lists are cut** at 100 rows per group, with *Show all n*.
- **Filters:** typing is debounced (300 ms); a filter change re-renders the summary rows only.

### State in the URL
- Filters, grouping and sort are query parameters; a link opens the same view, and reload and Back work.

### API
- **Compatibility:** existing endpoints keep their shape; new views get new endpoints.
- **Filters** are query parameters, documented in the OpenAPI description.
- **Compression:** JSON responses are compressed (Brotli, Gzip). Responses that can carry secrets (`/api/setup`, `/api/auth`, `/api/tokens`) are never compressed (BREACH).

### Checks
- **Playwright** at 1400, 1024 and 390 px: no horizontal overflow, and screenshots in `visuals/`.
- **Scale:** measured with the seed of 280 VMs / 6,092 points and 1,000 VMs; the targets are in `plan.md`.
