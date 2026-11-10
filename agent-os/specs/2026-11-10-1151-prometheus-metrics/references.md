# References for Prometheus Metrics

## In this codebase

### Snapshot of a VM for backup
- **Location:** `src/Backdafuckup.Xen/XenBackupSession.cs` (`CreateAsync`, `SnapshotAsync`, `CheckStorageSpaceAsync`, `ReportGuestFreeze`, `CommitAsync`)
- **Relevance:** the snapshot time (`snapshotWatch`), the disks and storages, the free space, and the guest freeze result are all computed here.
- **Key patterns:** the per-storage gate (`src/Backdafuckup.Xen/XenSnapshotGate.cs`, `Lease.Waited`); `[NOBAK]` exclusion via `ignore_vdis`.

### Disk reading and transports
- **Location:** `XenBackupSession.OpenReaderAsync` / `OpenNbdAsync` / `GetChangedExtentsAsync`, `src/Backdafuckup.Xen/XenHttpExport.cs`, `src/Backdafuckup.Nbd/NbdConnectionPool.cs`
- **Relevance:** NBD connect attempts and failures, the HTTP export fallback, the reasons for a full read.

### Per-disk counters and session metrics
- **Location:**
  - `src/Backdafuckup.DataMover/DiskBackupPipeline.cs` (`DiskBackupCounters`);
  - `src/Backdafuckup.Core/Repository/Manifest.cs` (`DiskBackupStats`, `Transport`);
  - `src/Backdafuckup.Server/Engine/BackupSessionRunner.cs` (`VmBackupTask`, `CheckConsistency`, `ToMetrics`);
  - `src/Backdafuckup.Server/Engine/EngineServices.cs` (`SessionRunnerService.RunAsync`, final state);
  - `src/Backdafuckup.Server/Engine/EngineInfrastructure.cs` (`RunningSessions`).
- **Relevance:** where the engine instruments record.

### VM rows with stale rule
- **Location:** `src/Backdafuckup.Server/Api/BackupVms.cs`, `BackupVmStates`, `ScheduleCalculator`
- **Relevance:** the per-VM gauges reuse these rows, so the console and Prometheus agree on "stale" and "no restore point".

### API tokens
- **Location:** `src/Backdafuckup.Server/Api/ApiTokens.cs` (handler, `IsTokenRequest`, endpoints), `ApiTokenEntity` in `src/Backdafuckup.Server/Data/Entities.cs`, `src/Backdafuckup.Web/Pages/ApiTokens.razor`
- **Relevance:** the scrape token is an API token with a scope.

### Fake pool for tests
- **Location:** `tests/Backdafuckup.Testing/Xapi/FakeXenPool.cs`
- **Relevance:** snapshot VDIs need `snapshot_time`; the pool state call needs SR, VDI and VM records; `NbdEnabled` / `NbdReachable` drive the fallback test.

## Outside

- **OpenTelemetry .NET:** `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.Prometheus.AspNetCore` (github.com/open-telemetry/opentelemetry-dotnet, Apache-2.0).
- **Microsoft Learn:** "Collect metrics with Prometheus and Grafana" for ASP.NET Core; built-in meters `System.Runtime`, `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Server.Kestrel`, `System.Net.Http`.
- **Prometheus:** naming best practices; `ScrapeConfig` and `PrometheusRule` CRDs of prometheus-operator.
- **XAPI:** `VDI.snapshot_time`, `VDI.type` (`cbt_metadata`), `VDI.managed`, `SR.virtual_allocation`.
