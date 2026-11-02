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
| SMBLibrary (Tal Aloni) | LGPL-3.0 (used as unmodified library) | Smb (push installation of the guest agent over SMB 2/3) |
| System.Text.Json, System.Management, System.ServiceProcess.ServiceController (Microsoft) | MIT | Guest agent |
| Microsoft.NETFramework.ReferenceAssemblies | MIT | Guest agent (build of the .NET Framework 4.8 variant) |
| LTRData.DiscUtils (Core, Streams, Ntfs, Fat, Lvm, Registry) | MIT | FileLevel (reading partitions and file systems of restore points) |
| DiscUtils.Ext and DiscUtils.Xfs, vendored with changes in `src/ThirdParty` (see their `NOTICE.md`) | MIT | FileLevel (ext2/3/4 and XFS) |
| System.IO.Hashing (Microsoft) | MIT | FileLevel (CRC-32 for ZIP downloads) |
| SSH.NET | MIT | Ssh (file restore into Linux VMs over SSH/SFTP) |
| mingw-w64 runtime (headers, libraries) and GCC runtime libraries | mingw-w64: ZPL / public domain; libgcc, libstdc++: GPL-3.0 with GCC Runtime Library Exception | bdfu-vss.dll (statically linked runtime) |

Build tools (not shipped): mingw-w64 GCC (GPL-3.0) and osslsigncode (GPL-3.0) in the Docker image of `native/bdfu-vss`.

Test-only dependencies (not shipped): xUnit (Apache-2.0), Testcontainers (MIT), bUnit (MIT), BenchmarkDotNet (MIT), coverlet (MIT).

Specifications implemented from public documents (no code copied):
- NBD protocol (`doc/proto.md`, public domain)
- XAPI / XenServer changed block tracking developer documentation
- Microsoft Volume Shadow Copy Service documentation (requester, provider, COM+ registration)
- MS-SCMR (Service Control Manager Remote Protocol) and MS-RPCE (NDR), for the svcctl client of the push installation
- Microsoft VHD image format specification (VHD stream reader and writer)
- PKWARE APPNOTE.TXT (ZIP and ZIP64 format), for the streaming ZIP writer of file-level restore downloads
