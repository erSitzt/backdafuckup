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
| Material Design Icons (Pictogrammers, @mdi/svg 7.4.47): the paths of `database`, `database-cog`, `database-clock` and `harddisk` | Apache-2.0 | Web (`AppIcons`: database, SQL Server instance, log backups, disk) |
| SMBLibrary (Tal Aloni) | LGPL-3.0 (used as unmodified library) | Smb (push installation of the guest agent over SMB 2/3) |
| ISCSI and DiskAccessLibrary (Tal Aloni) | LGPL-3.0-or-later (used as unmodified libraries) | Guest agent (publishing restore points into a VM through a loopback iSCSI target) |
| System.Text.Json, System.Management, System.ServiceProcess.ServiceController (Microsoft) | MIT | Guest agent |
| System.DirectoryServices.Protocols (Microsoft) | MIT | Guest agent, .NET 10 build (LDAP to a domain controller; the .NET Framework build uses the built-in assembly) |
| Microsoft.NETFramework.ReferenceAssemblies | MIT | Guest agent (build of the .NET Framework 4.8 variant) |
| LTRData.DiscUtils (Core, Streams, Ntfs, Fat, Lvm, Registry) | MIT | FileLevel (reading partitions and file systems of restore points) |
| DiscUtils.Ext and DiscUtils.Xfs, vendored with changes in `src/ThirdParty` (see their `NOTICE.md`) | MIT | FileLevel (ext2/3/4 and XFS) |
| System.IO.Hashing (Microsoft) | MIT | FileLevel (CRC-32 for ZIP downloads) |
| SSH.NET | MIT | Ssh (file restore into Linux VMs over SSH/SFTP) |
| Microsoft.Data.SqlClient (and its dependencies, incl. System.Security.Cryptography.Pkcs) | MIT | Guest agent, .NET 10 build (SQL Server restores; the .NET Framework build uses the built-in System.Data.SqlClient) |
| Scalar.AspNetCore | MIT | Server (API reference at /api-docs; telemetry and cloud features switched off) |
| OpenTelemetry, OpenTelemetry.Extensions.Hosting, OpenTelemetry.Exporter.Prometheus.AspNetCore | Apache-2.0 | Server (Prometheus metrics at /metrics; pull only, nothing is sent out) |
| mingw-w64 runtime (headers, libraries) and GCC runtime libraries | mingw-w64: ZPL / public domain; libgcc, libstdc++: GPL-3.0 with GCC Runtime Library Exception | bdfu-vss.dll (statically linked runtime) |

Build tools (not shipped): mingw-w64 GCC (GPL-3.0) and osslsigncode (GPL-3.0) in the Docker image of `native/bdfu-vss`.

Test-only dependencies (not shipped): xUnit (Apache-2.0), Testcontainers and Testcontainers.MsSql (MIT), the SQL Server container image (Developer edition, development and test use only), bUnit (MIT), BenchmarkDotNet (MIT), coverlet (MIT).

Specifications implemented from public documents (no code copied):
- NBD protocol (`doc/proto.md`, public domain)
- XAPI / XenServer changed block tracking developer documentation
- Microsoft Volume Shadow Copy Service documentation (requester, provider, COM+ registration)
- MS-SCMR (Service Control Manager Remote Protocol) and MS-RPCE (NDR), for the svcctl client of the push installation
- Microsoft VHD image format specification (VHD stream reader and writer)
- PKWARE APPNOTE.TXT (ZIP and ZIP64 format), for the streaming ZIP writer of file-level restore downloads
