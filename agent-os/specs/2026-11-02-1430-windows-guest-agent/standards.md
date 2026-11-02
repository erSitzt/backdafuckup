# Standards for the Windows Guest Agent

There are no formal standards yet (`agent-os/standards/index.yml` is empty). The conventions of the foundation spec apply (`../2026-11-01-2134-xcpng-backup-foundation/standards.md`), plus the additions below.

---

## Additions for this spec

### Agent code
- The shared protocol targets `netstandard2.0`, so the .NET Framework 4.8 build of the agent can use it.
- Agent code multi-targets `net10.0-windows` and `net48`. Platform differences live behind small interfaces, so the logic can be tested on Linux.
- Native code lives under `native/`. It is plain C++ with a C ABI for P/Invoke, built reproducibly with mingw-w64 in Docker. No vendored binaries.

### Security
- The agent authenticates with its own bearer secret (scheme "Agent"). Cookie auth and the CSRF header do not apply to agent endpoints.
- The agent pins the server certificate (SPKI SHA-256), the same way the server pins XCP-ng hosts.
- Secrets are never logged. The server stores agent secrets only as hashes; Windows credentials go through `CredentialProtector`.
- The agent offers only the operations named in the spec: info, prepare, freeze, thaw, abort.

### Operations against customer systems
- Error messages and session logs name the concrete IP addresses, ports, host names and VM names.
- Nothing is deleted in the hypervisor or guest except objects the product created itself (temporary setup service, setup files, its own snapshots).

### Tests
- Unit tests for every protocol and serialisation format (NDR, tokens).
- Engine tests use an in-process fake agent.
- Anything that needs Windows or a real pool goes into the lab runbook or the opt-in lab tests (`BDFU_LAB_RUN=1`).
