using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Server.Data;

namespace Backdafuckup.Server.Api;

internal static class ApiMapping
{
    public static CertificateDto ToDto(this CertificateProbe p) =>
        new(p.HostAddress, p.Subject, p.Issuer, p.NotBefore, p.NotAfter, p.SpkiSha256, p.Sha256Thumbprint, p.ValidAgainstSystemCa);

    public static ConnectionDto ToDto(this HypervisorConnection c) => new(
        c.Id, c.Kind.ToString(), c.DisplayName, c.Address, c.Port, c.CurrentMasterAddress, c.PlatformName, c.ProductName, c.ProductVersion,
        c.Status.ToString(), c.LastConnectedUtc, c.LastError, c.Credential?.Username ?? string.Empty, c.AllowInsecureNbd,
        c.Hosts.OrderBy(h => h.Address).Select(h => new ConnectionHostDto(h.Address, h.SpkiSha256, h.TrustedUtc)).ToList());

    public static InventoryItemDto ToDto(this InventoryEntry e) => new(e.Kind.ToString(), e.NativeId, e.Name, e.ParentNativeId, e.Properties);

    public static RepositoryDto ToDto(this BackupRepositoryEntity r, int restorePoints, long logicalBytes) => new(
        r.Id, r.Name, r.Kind.ToString(), r.Path, r.RepositoryUuid, r.ChunkSize, r.CompressionLevel, r.MaxConcurrentTasks,
        r.State.ToString(), r.TotalBytes, r.FreeBytes, restorePoints, logicalBytes, r.LastError);

    public static ScheduleDto ToDto(this JobSchedule s) => new(
        s.Kind.ToString(),
        s.TimeOfDay.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        Enum.GetValues<DayOfWeek>().Where(d => (s.DaysOfWeek & (1 << (int)d)) != 0).ToList(),
        s.TimeZoneId);

    public static JobSchedule FromDto(this ScheduleDto s) => new()
    {
        Kind = Enum.TryParse<ScheduleKind>(s.Kind, true, out var kind) ? kind : ScheduleKind.None,
        TimeOfDay = TimeOnly.TryParse(s.TimeOfDay, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : new TimeOnly(22, 0),
        DaysOfWeek = s.Days.Aggregate(0, (mask, day) => mask | (1 << (int)day)),
        TimeZoneId = string.IsNullOrWhiteSpace(s.TimeZoneId) ? "UTC" : s.TimeZoneId,
    };

    public static JobDto ToDto(this BackupJob j, bool running) => new(
        j.Id, j.Name, j.Description, j.Enabled, j.ConnectionId, j.Connection?.DisplayName ?? string.Empty, j.RepositoryId,
        j.Repository?.Name ?? string.Empty, j.RetentionPoints, j.Schedule.ToDto(),
        j.Objects.OrderBy(o => o.Order).Select(o => new JobObjectDto(o.NativeId, o.CachedName, o.WithMemory)).ToList(),
        j.NextRunUtc, j.LastRunUtc, j.LastResult?.ToString(), running, j.MaxParallelVms, j.NbdConnectionsPerDisk, j.RetryCount, j.TransportMode.ToString(), j.MaxParallelDisks,
        j.GuestProcessing, j.GuestFailurePolicy.ToString(), j.GuestCredentialId, j.GuestProcessingMode.ToString(), j.SqlCredentialId, j.SqlSnapshotCopyOnly,
        j.SqlBackup.ToDto());

    public static SqlBackupSettingsDto ToDto(this SqlBackupSettings s) => new(
        s.Enabled, s.LogIntervalMinutes, s.FullSchedule.ToDto(), s.SystemSchedule.ToDto(), s.FullForAllDatabases, s.FullOnChainBreak, [.. s.ExcludedDatabases], s.StagingFolder);

    /// <summary>The settings of a request; the schedules run in the job's time zone.</summary>
    public static SqlBackupSettings FromDto(this SqlBackupSettingsDto? s, string timeZoneId)
    {
        var settings = new SqlBackupSettings();
        if (s is null)
        {
            return settings;
        }

        settings.Enabled = s.Enabled;
        settings.LogIntervalMinutes = s.LogIntervalMinutes;
        if (s.FullSchedule is { } full)
        {
            settings.FullSchedule = full.FromDto();
        }

        if (s.SystemSchedule is { } system)
        {
            settings.SystemSchedule = system.FromDto();
        }

        settings.FullSchedule.TimeZoneId = timeZoneId;
        settings.SystemSchedule.TimeZoneId = timeZoneId;
        settings.FullForAllDatabases = s.FullForAllDatabases;
        settings.FullOnChainBreak = s.FullOnChainBreak;
        settings.ExcludedDatabases = [.. (s.ExcludedDatabases ?? []).Select(d => d.Trim()).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        settings.StagingFolder = string.IsNullOrWhiteSpace(s.StagingFolder) ? null : s.StagingFolder.Trim();
        return settings;
    }

    public static RestorePointDto ToDto(this RestorePointEntity r) => new(
        r.Id, r.RepositoryId, r.JobId, r.JobName, r.ConnectionId, r.VmNativeId, r.VmName, r.CreatedUtc, r.Kind.ToString(), r.State.ToString(),
        r.LogicalBytes, r.NewStoredBytes,
        r.Disks.OrderBy(d => d.DiskKey, StringComparer.Ordinal).Select(d => new RestorePointDiskDto(
            d.DiskKey, d.Name, d.CapacityBytes, d.StorageId, d.StorageName, d.Mode.ToString(), d.FullReason, d.ReadBytes, d.NewStoredBytes)).ToList(),
        (r.Vm?.Nics ?? []).Select(n => new RestorePointNicDto(n.Device, n.Mac, n.NetworkId, n.NetworkName)).ToList(),
        r.Vm?.VCpus ?? 0,
        r.Vm?.MemoryBytes ?? 0,
        r.Vm?.Firmware,
        r.Consistency,
        r.ConsistencyReason,
        r.SqlDatabaseCount,
        r.DomainController == true,
        r.AdDomain,
        VmGenerationIds.IsSet(r.Vm?.GenerationId),
        r.MemoryImageBytes,
        SqlConsistentCount: r.SqlConsistentCount,
        ConsistencyWarning: r.ConsistencyWarning);
}
