# Third-party notices

backdafuckup uses the following third-party components. Keep this list in sync with `Directory.Packages.props`.

| Component | License | Used by |
|---|---|---|
| .NET runtime / ASP.NET Core / EF Core (Microsoft) | MIT | all |
| ZstdSharp.Port | MIT | Storage (chunk compression) |
| Polly.Core | BSD-3-Clause | Xen (XAPI retries) |
| Serilog, Serilog.AspNetCore, Serilog.Sinks.File | Apache-2.0 | Server (logging) |
| Npgsql, Npgsql.EntityFrameworkCore.PostgreSQL | PostgreSQL License | Server (catalog database) |
| EFCore.NamingConventions | Apache-2.0 | Server |
| MudBlazor | MIT | Web (UI components) |

Test-only dependencies (not shipped): xUnit (Apache-2.0), Testcontainers (MIT), bUnit (MIT), BenchmarkDotNet (MIT), coverlet (MIT).

Specifications implemented from public documents (no code copied):
- NBD protocol (`doc/proto.md`, public domain)
- XAPI / XenServer changed block tracking developer documentation
