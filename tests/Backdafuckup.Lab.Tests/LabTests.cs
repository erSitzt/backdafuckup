using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.DataMover;
using Backdafuckup.Nbd;
using Backdafuckup.Storage;
using Backdafuckup.Xen;
using Backdafuckup.Xen.Tls;
using Backdafuckup.Xen.Xapi;

namespace Backdafuckup.Lab.Tests;

/// <summary>
/// Opt-in tests against a real XCP-ng / XenServer pool (environment variables BDFU_LAB_*; see docs/admin-guide.md).
/// They create and remove snapshots and a restored copy of the test VM.
/// </summary>
[Trait("Category", "Lab")]
public sealed class LabTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>
    /// Determines the bit order of VDI.list_changed_blocks: compares the blocks that really differ between two snapshots
    /// with the bitmap decoded both ways. The correct order covers every changed block (CBT may over-report).
    /// </summary>
    [Fact]
    public async Task Cbt_bit_order_spike()
    {
        var lab = await LabConnection.ConnectAsync();
        var log = new ConsoleLog();
        await using var session = lab.Session;
        var vm = Env("BDFU_LAB_VM")!;

        ChangeToken token1;
        byte[][] before;
        await using (var first = await session.BeginBackupAsync(Request(vm), log, new InMemoryArtifactLedger(), Ct))
        {
            var disk = first.Disks[0];
            before = await HashBlocksAsync(first, disk);
            token1 = first.GetNewChangeToken(disk) ?? throw new InvalidOperationException("CBT is not available for this disk.");
            await first.CommitAsync(log, Ct);
        }

        var wait = int.TryParse(Env("BDFU_LAB_WAIT_SECONDS"), out var seconds) ? seconds : 120;
        TestContext.Current.SendDiagnosticMessage($"Waiting {wait} s – write some data inside the VM now (e.g. dd if=/dev/urandom of=/tmp/x bs=1M count=50; sync).");
        await Task.Delay(TimeSpan.FromSeconds(wait), Ct);

        await using var second = await session.BeginBackupAsync(Request(vm), log, new InMemoryArtifactLedger(), Ct);
        var disk2 = second.Disks[0];
        string bitmap;
        await using (var xapi = await lab.LoginAsync())
        {
            var baseRef = await xapi.TryGetByUuidAsync("VDI", token1.Value, Ct) ?? throw new InvalidOperationException("CBT metadata of the first snapshot is gone.");
            var toRef = await xapi.TryGetByUuidAsync("VDI", disk2.SnapshotDiskId, Ct);
            bitmap = await xapi.CallStringAsync("VDI.list_changed_blocks", Ct, baseRef, toRef);
        }

        var after = await HashBlocksAsync(second, disk2);
        var actual = Enumerable.Range(0, before.Length).Where(i => !before[i].AsSpan().SequenceEqual(after[i])).ToHashSet();
        var msb = Blocks(CbtBitmap.DecodeBase64(bitmap, disk2.CapacityBytes, CbtBitOrder.MostSignificantBitFirst));
        var lsb = Blocks(CbtBitmap.DecodeBase64(bitmap, disk2.CapacityBytes, CbtBitOrder.LeastSignificantBitFirst));
        var token2 = second.GetNewChangeToken(disk2);
        await second.CommitAsync(log, Ct);
        await session.ReleaseChangeTokensAsync(new[] { token1, token2 }.OfType<ChangeToken>(), log, Ct);

        Assert.SkipWhen(actual.Count == 0, "The VM did not change any data between the snapshots; write data during the wait and run again.");
        var msbCovers = actual.IsSubsetOf(msb);
        var lsbCovers = actual.IsSubsetOf(lsb);
        var report = $"Changed 64 KiB blocks: {actual.Count}; bitmap marks {msb.Count}. MSB-first covers all: {msbCovers}; LSB-first covers all: {lsbCovers}.";
        TestContext.Current.SendDiagnosticMessage(report);
        Assert.True(msbCovers ^ lsbCovers, "Inconclusive: " + report);
        TestContext.Current.SendDiagnosticMessage($"==> Configure \"Xen\": {{ \"CbtBitOrder\": \"{(msbCovers ? nameof(CbtBitOrder.MostSignificantBitFirst) : nameof(CbtBitOrder.LeastSignificantBitFirst))}\" }}");
    }

    /// <summary>Full backup over NBD into a temporary repository, restore to another SR, compare disk hashes.</summary>
    [Fact]
    public async Task Backup_and_restore_roundtrip()
    {
        var lab = await LabConnection.ConnectAsync();
        var targetSr = Env("BDFU_LAB_TARGET_SR");
        Assert.SkipWhen(string.IsNullOrEmpty(targetSr), "BDFU_LAB_TARGET_SR is not set.");
        var log = new ConsoleLog();
        await using var session = lab.Session;
        var root = Path.Combine(Path.GetTempPath(), "bdfu-lab", Guid.NewGuid().ToString("N"));
        await using var repository = await LocalDirectoryRepository.CreateAsync(root, Ct);
        var rpId = Guid.CreateVersion7();
        var tokens = new List<ChangeToken>();
        RestorePointManifest manifest;
        VmMetadataBlob metadata;

        await using (var backup = await session.BeginBackupAsync(Request(Env("BDFU_LAB_VM")!, rpId), log, new InMemoryArtifactLedger(), Ct))
        await using (var writer = repository.BeginWrite(rpId, backup.Vm.VmId, null))
        {
            var disks = new List<ManifestDisk>();
            foreach (var disk in backup.Disks)
            {
                await using var reader = await backup.OpenReaderAsync(disk, new DiskReaderOptions(4, 4), Ct);
                TestContext.Current.SendDiagnosticMessage($"Disk {disk.Name}: {disk.CapacityBytes} bytes, NBD max request {reader.MaxRequestBytes} bytes.");
                var started = DateTime.UtcNow;
                var result = await DiskBackupPipeline.RunAsync(new DiskBackupJob { Reader = reader, Writer = writer, ChunkSize = repository.Info.ChunkSize, Changes = ChangedExtents.Full("lab") }, Ct);
                var seconds = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
                TestContext.Current.SendDiagnosticMessage($"Disk {disk.Name}: read {result.Stats.ReadBytes / 1048576.0:0} MiB in {seconds:0.0} s ({result.Stats.ReadBytes / 1048576.0 / seconds:0} MiB/s), stored {result.Stats.NewStoredBytes / 1048576.0:0} MiB.");
                disks.Add(new ManifestDisk
                {
                    Key = disk.Key, SourceDiskId = disk.SourceDiskId, Name = disk.Name, CapacityBytes = disk.CapacityBytes, StorageId = disk.StorageId,
                    Index = await writer.WriteDiskIndexAsync(disk.Key, result.Index, Ct), Stats = result.Stats, ChangeToken = backup.GetNewChangeToken(disk),
                });
            }

            metadata = backup.Metadata;
            manifest = new RestorePointManifest
            {
                RestorePointId = rpId, RepositoryId = repository.Info.RepositoryId, CreatedUtc = DateTimeOffset.UtcNow, Kind = RestorePointKind.Full,
                Source = new ManifestSource(HypervisorKind.XenServer, session.Platform.PlatformId, backup.Vm.VmId, backup.Vm.Name, backup.SnapshotId),
                Vm = backup.Vm, Chunking = new ManifestChunking(repository.Info.ChunkSize, "sha256", "zstd"), Disks = disks,
            };
            await writer.CommitAsync(manifest, Ct);
            await backup.CommitAsync(log, Ct);
            tokens.AddRange(disks.Select(d => d.ChangeToken).OfType<ChangeToken>());
        }

        var expected = new Dictionary<string, string>();
        await using var reader2 = await repository.OpenRestorePointAsync(rpId, Ct);
        foreach (var disk in manifest.Disks)
        {
            var image = await RestorePointDiskImage.OpenAsync(reader2, disk.Key, Ct);
            await using var stream = image.OpenSequential();
            expected[disk.SourceDiskId] = Convert.ToHexString(await SHA256.HashDataAsync(stream, Ct));
        }

        var name = $"{manifest.Vm.Name}-bdfu-labtest";
        var plan = new VmRestorePlan
        {
            Mode = RestoreMode.AlternateLocation, Manifest = manifest, Metadata = metadata, VmName = name,
            DiskStorage = manifest.Disks.ToDictionary(d => d.Key, _ => targetSr!), RegenerateMacs = true, SessionId = Guid.NewGuid(),
        };
        var mapping = new Dictionary<string, string>();
        RestoredVm restored;
        await using (var restore = await session.BeginRestoreAsync(plan, log, new InMemoryArtifactLedger(), Ct))
        {
            var validation = await restore.ValidateAsync(Ct);
            Assert.True(validation.Ok, string.Join("; ", validation.Errors));
            foreach (var disk in manifest.Disks)
            {
                await using var writer = await restore.CreateDiskAsync(new RestoreDiskTarget(disk.Key, disk.SourceDiskId, disk.Name, disk.CapacityBytes, targetSr!), Ct);
                await writer.WriteAsync(await RestorePointDiskImage.OpenAsync(reader2, disk.Key, Ct), null, Ct);
                mapping[disk.SourceDiskId] = writer.DiskId;
            }

            await restore.CreateVmAsync(mapping, Ct);
            restored = await restore.CompleteAsync(Ct);
        }

        try
        {
            await using var xapi = await lab.LoginAsync();
            foreach (var (source, restoredDisk) in mapping)
            {
                Assert.Equal(expected[source], await HashVdiOverNbdAsync(xapi, lab, restoredDisk));
            }

            TestContext.Current.SendDiagnosticMessage($"Restored VM '{restored.Name}' matches the backup bit for bit.");
        }
        finally
        {
            await XenHypervisorSessionCleanup.DestroyVmAndDisksAsync(lab, restored.VmId);
            await session.ReleaseChangeTokensAsync(tokens, log, Ct);
            await reader2.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static VmBackupRequest Request(string vm, Guid? restorePoint = null) =>
        new(vm, Guid.Empty, Guid.NewGuid(), restorePoint ?? Guid.NewGuid(), "backdafuckup lab test");

    private static HashSet<int> Blocks(IReadOnlyList<DiskExtent> extents) =>
        extents.SelectMany(e => Enumerable.Range((int)(e.Offset / CbtBitmap.BlockSize), (int)((e.Length + CbtBitmap.BlockSize - 1) / CbtBitmap.BlockSize))).ToHashSet();

    private static async Task<byte[][]> HashBlocksAsync(IVmBackupSession backup, SnapshotDisk disk)
    {
        await using var reader = await backup.OpenReaderAsync(disk, new DiskReaderOptions(4, 4), Ct);
        var count = (int)((disk.CapacityBytes + CbtBitmap.BlockSize - 1) / CbtBitmap.BlockSize);
        var hashes = new byte[count][];
        await Parallel.ForAsync(0, count, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = Ct }, async (i, ct) =>
        {
            var length = (int)Math.Min(CbtBitmap.BlockSize, disk.CapacityBytes - ((long)i * CbtBitmap.BlockSize));
            var buffer = new byte[length];
            await reader.ReadAsync((long)i * CbtBitmap.BlockSize, buffer, ct);
            hashes[i] = SHA256.HashData(buffer);
        });
        return hashes;
    }

    private static async Task<string> HashVdiOverNbdAsync(XapiSession xapi, LabConnection lab, string vdiUuid)
    {
        var vdiRef = await xapi.TryGetByUuidAsync("VDI", vdiUuid, Ct) ?? throw new InvalidOperationException("Restored disk not found.");
        var info = ((JsonArray)(await xapi.CallAsync("VDI.get_nbd_info", Ct, vdiRef))!).OfType<JsonObject>().First();
        var pem = info.Str("cert");
        var options = new NbdConnectOptions
        {
            Host = info.Str("address"),
            Port = (int)info.Long("port"),
            ExportName = info.Str("exportname"),
            TlsMode = string.IsNullOrEmpty(pem) ? NbdTlsMode.Disabled : NbdTlsMode.Required,
            TlsTargetHost = info.Str("subject"),
            ServerCertificateValidation = string.IsNullOrEmpty(pem) ? null : CertificatePinning.CreateValidator(TlsTrustMode.Pinned, [CertificatePinning.SpkiSha256FromPem(pem)]),
        };
        await using var client = await NbdClient.ConnectAsync(options, Ct);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[4 * 1024 * 1024];
        for (long offset = 0; offset < client.Size; offset += buffer.Length)
        {
            var length = (int)Math.Min(buffer.Length, client.Size - offset);
            await client.ReadAsync(offset, buffer.AsMemory(0, length), Ct);
            sha.AppendData(buffer, 0, length);
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private sealed class ConsoleLog : IOperationLog
    {
        public void Write(LogSeverity severity, string message, string? key = null) => TestContext.Current.SendDiagnosticMessage($"{severity}: {message}");
    }
}

/// <summary>Connection to the lab pool with trust-on-first-use pinning of all pool hosts.</summary>
internal sealed record LabConnection(XenProvider Provider, ConnectionSettings Settings, IHypervisorSession Session)
{
    public static async Task<LabConnection> ConnectAsync()
    {
        var host = Environment.GetEnvironmentVariable("BDFU_LAB_HOST");
        Assert.SkipWhen(string.IsNullOrEmpty(host) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BDFU_LAB_VM")),
            "Lab tests need BDFU_LAB_HOST, BDFU_LAB_USER, BDFU_LAB_PASSWORD and BDFU_LAB_VM.");
        var provider = new XenProvider(new XenOptions { CbtBitOrder = CbtBitOrder.MostSignificantBitFirst });
        var credential = new NetworkCredential(Environment.GetEnvironmentVariable("BDFU_LAB_USER") ?? "root", Environment.GetEnvironmentVariable("BDFU_LAB_PASSWORD"));
        var pins = new List<PinnedCertificate>();
        var probe = await provider.ProbeCertificateAsync(host!, 443, TestContext.Current.CancellationToken);
        pins.Add(new PinnedCertificate(probe.HostAddress, probe.SpkiSha256, probe.Sha256Thumbprint, probe.Pem));

        for (var i = 0; i < 3; i++)
        {
            var settings = new ConnectionSettings(Guid.NewGuid(), HypervisorKind.XenServer, host!, 443, credential, TlsTrustMode.Pinned, pins, new Dictionary<string, string>());
            var result = await provider.TestConnectionAsync(settings, TestContext.Current.CancellationToken);
            if (result.Success)
            {
                settings = settings with { Pins = [.. pins, .. result.HostPins] };
                TestContext.Current.SendDiagnosticMessage($"Connected to {result.Platform!.PlatformName} ({result.Platform.ProductName} {result.Platform.ProductVersion}).");
                return new LabConnection(provider, settings, await provider.OpenAsync(settings, TestContext.Current.CancellationToken));
            }

            Assert.True(result.UntrustedHosts.Count > 0, result.Error);
            pins.AddRange(result.UntrustedHosts.Select(p => new PinnedCertificate(p.HostAddress, p.SpkiSha256, p.Sha256Thumbprint, p.Pem)));
        }

        throw new InvalidOperationException("Could not establish trust to the pool.");
    }

    public Task<XapiSession> LoginAsync() => XapiSession.LoginAsync(
        new XapiLoginOptions
        {
            Address = Session.Platform.MasterAddress,
            Username = Settings.Credential.UserName,
            Password = Settings.Credential.Password,
            TransportFactory = h => new XapiTransport(h, 443, CertificatePinning.CreateValidator(TlsTrustMode.Pinned, Settings.Pins.Select(p => p.SpkiSha256))),
        },
        TestContext.Current.CancellationToken);
}

internal static class XenHypervisorSessionCleanup
{
    public static async Task DestroyVmAndDisksAsync(LabConnection lab, string vmUuid)
    {
        await using var xapi = await lab.LoginAsync();
        var ct = CancellationToken.None;
        var vmRef = await xapi.TryGetByUuidAsync("VM", vmUuid, ct);
        if (vmRef is null)
        {
            return;
        }

        var vm = await xapi.GetRecordAsync("VM", vmRef, ct);
        var vdis = new List<string>();
        foreach (var vbd in vm.Set("VBDs"))
        {
            var record = await xapi.GetRecordAsync("VBD", vbd, ct);
            if (record.Str("type") == "Disk" && !XapiRecord.IsNullRef(record.Str("VDI")))
            {
                vdis.Add(record.Str("VDI"));
            }
        }

        await xapi.CallAsync("VM.destroy", ct, vmRef);
        foreach (var vdi in vdis)
        {
            await xapi.CallAsync("VDI.destroy", ct, vdi);
        }
    }
}
