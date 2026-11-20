# Third-party notices

backdafuckup uses the following third-party components. Keep this list in sync with `Directory.Packages.props`.

| Component | License | Used by |
|---|---|---|
| .NET runtime / ASP.NET Core / EF Core (Microsoft) | MIT | all |
| ZstdSharp.Port | MIT | Storage (chunk compression), Tape (indexes and catalogs on tape) |
| Konscious.Security.Cryptography.Argon2 | MIT | Tape (Argon2id key derivation from tape passphrases) |
| Polly.Core | BSD-3-Clause | Xen (XAPI retries) |
| KubernetesClient (kubernetes-client/csharp) with Fractions and YamlDotNet | Apache-2.0; Fractions and YamlDotNet MIT | Kubernetes (the cluster API: inventory, snapshots, mover pods) |
| Serilog, Serilog.AspNetCore, Serilog.Sinks.File | Apache-2.0 | Server (logging) |
| Npgsql, Npgsql.EntityFrameworkCore.PostgreSQL | PostgreSQL License | Server (catalog database) |
| EFCore.NamingConventions | Apache-2.0 | Server |
| MudBlazor | MIT | Web (UI components) |
| Material Design Icons (Pictogrammers, @mdi/svg 7.4.47): the paths of `database`, `database-cog`, `database-clock`, `harddisk` and `kubernetes` | Apache-2.0 | Web (`AppIcons`: database, SQL Server instance, log backups, disk, Kubernetes) |
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
| OpenVmfs (.NET binding and its native library `openvmfs_c`; the native library links only the Rust standard library, the Windows build also the mingw-w64 runtime and libgcc, see the package's `THIRD-PARTY-NOTICES.txt`) | Apache-2.0; Rust standard library MIT OR Apache-2.0; mingw-w64 ZPL / public domain, libgcc GPL-3.0 with GCC Runtime Library Exception | Vmfs (reading VMFS6 datastores from array snapshot LUNs) |
| Mover image base `mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled` (Ubuntu 24.04, chiseled): glibc 2.39 (`libc6`), GCC 14 runtime libraries (`libgcc-s1`, `libstdc++6`), OpenSSL 3.0 (`libssl3t64`, `openssl` configuration), `ca-certificates`, `base-files` | glibc: LGPL-2.1-or-later; libgcc, libstdc++: GPL-3.0 with GCC Runtime Library Exception; OpenSSL: Apache-2.0; ca-certificates: MPL-2.0 (certificate data), GPL-2.0-or-later (scripts); base-files: GPL-2.0-or-later. Their copyright files are in the image under `/usr/share/doc`; sources from Ubuntu (Launchpad) | Mover (the container image the server pushes to the user's registry, unmodified base layers) |
| mingw-w64 runtime (headers, libraries) and GCC runtime libraries | mingw-w64: ZPL / public domain; libgcc, libstdc++: GPL-3.0 with GCC Runtime Library Exception | bdfu-vss.dll (statically linked runtime) |

Build tools (not shipped): mingw-w64 GCC (GPL-3.0) and osslsigncode (GPL-3.0) in the Docker image of `native/bdfu-vss`.

Test-only dependencies (not shipped): xUnit (Apache-2.0), Testcontainers and Testcontainers.MsSql (MIT), the SQL Server container image (Developer edition, development and test use only), bUnit (MIT), BenchmarkDotNet (MIT), coverlet (MIT). Container images built only for interoperability tests, from Debian packages: nbdkit (BSD-3-Clause), the libnfs utilities (LGPL-2.1-or-later), rpcbind (BSD-3-Clause), nfs-common (the Linux NFS client utilities, GPL-2.0-or-later) and fio (GPL-2.0-only), the last two in a privileged container that mounts the instant recovery NFS server with the kernel's client. None of them is shipped or linked. The VMFS6 images in `tests/fixtures/vmfs6` come from the OpenVmfs repository (Apache-2.0); they are test data only.

Specifications implemented from public documents (no code copied):
- NBD protocol (`doc/proto.md`, public domain)
- ONC RPC (RFC 5531), XDR (RFC 4506), the portmapper (RFC 1833), NFS version 3 and MOUNT version 3 (RFC 1813)
- XAPI / XenServer changed block tracking developer documentation
- Microsoft Volume Shadow Copy Service documentation (requester, provider, COM+ registration)
- MS-SCMR (Service Control Manager Remote Protocol) and MS-RPCE (NDR), for the svcctl client of the push installation
- VMware vSphere Web Services API reference (vim25 WSDL and XSD as vCenter serves them), for our own vim25 client
- HPE Alletra Storage MP WS-APIv3 OpenAPI description (version 2.5.0), for the shapes and field names of the array client
- Microsoft VHD image format specification (VHD stream reader and writer)
- PKWARE APPNOTE.TXT (ZIP and ZIP64 format), for the streaming ZIP writer of file-level restore downloads
