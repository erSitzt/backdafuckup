# Standards for XCP-ng Backup Foundation

No standards existed when this spec was shaped (`agent-os/standards/index.yml` is empty).

This spec sets the following conventions. Once the first code exists, run `/discover-standards` to turn them into formal standards.

---

## Conventions established by this spec

### Code
- Target .NET 10 / C# with nullable reference types enabled, warnings treated as errors, and analyzers on.
- Manage package versions centrally in `Directory.Packages.props`. Restore from nuget.org only; no vendored binaries.
- Every I/O method is async and takes a `CancellationToken` as its last parameter.
- Use `System.Text.Json` with source generation. Do not use Newtonsoft.

### API
- REST under `/api/...`. Errors are RFC 9457 ProblemDetails.
- Every state-changing call needs cookie auth and the header `X-BDFU-Request: 1`.
- Live updates go through SignalR at `/hubs/live`. The UI talks to the server only through REST and SignalR.

### XAPI
- Use one XAPI session per VM task, with originator `"backdafuckup"`.
- Retry idempotent reads and destroys 10× at 10 s intervals. Never retry create or snapshot calls; recover from them with a lookup instead.
- Never write `session_id` or the NBD `exportname` to logs.
- Tag every object we create with `other_config["backdafuckup.*"]`, and record it in the artifact ledger before waiting on it.

### Repository format
- Version the on-disk format: `formatVersion` goes in `repo.json`, in every manifest, and in the chunk and index headers.
- Chunks, indexes and manifests are write-once. `manifest.json` is the commit point.

### Licensing
- The project licence is AGPL-3.0, and contributors sign a CLA.
- Dependencies must be open source. Never use code, binaries or tools obtainable only through a purchased product, partner-gated SDKs, or undocumented APIs.
- Record every dependency in `THIRD-PARTY-NOTICES.md`.

### Tests
- Use xUnit, with test categories `Unit`, `Integration` (Docker / Testcontainers) and `Lab` (opt-in, needs a real XCP-ng pool via `BDFU_LAB_*`).
