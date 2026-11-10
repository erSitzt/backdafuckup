# Standards for Prometheus Metrics

There are no formal standards yet (`agent-os/standards/index.yml` is empty). These conventions apply:
- the foundation spec (`../2026-11-01-2134-xcpng-backup-foundation/standards.md`): REST and SignalR only, the console in MudBlazor;
- the FLR additions (`../2026-11-02-1713-file-level-restore/standards.md`): fail loudly, and messages with context (IPs, ports, hosts, paths);
- the integration policy in `agent-os/product/tech-stack.md`: open-source dependencies compatible with AGPL-3.0, each recorded in `THIRD-PARTY-NOTICES.md`; nothing from purchased products.

The additions below apply on top.

---

## Additions for this spec

### Metric names and labels
- **Names:** the prefix `bdfu_`, snake case, base units (`_seconds`, `_bytes`), `_total` for counters. That follows the Prometheus naming guide.
- **Bounded labels:** label values come from a known, small set: pool, storage, job and VM names, transport, fixed reason codes. Never free text, error messages, ids that grow without bound, or paths.
- **No secrets:** no label or value carries a credential, token, key or password.

### The scrape path
- **No queries while scraping:** a scrape never reaches the database, XAPI or a guest agent. Background services refresh cached state, and gauges read it.
- **One snapshot per refresh:** the cached state is replaced as a whole, so one scrape never mixes two refreshes.

### Access
- **Only with a token or a sign-in:** `/metrics` needs a signed-in user, a full API token or a metrics-only token. A metrics-only token is refused everywhere else (403, naming the token's scope).
- **TLS stays on:** the scrape configuration examples verify the server certificate (`ca_file`). They never use `insecure_skip_verify`.

### Pools
- **Read-only:** the pool poller only reads (`get_all_records`). It never changes, deletes or rescans anything in the pool.
