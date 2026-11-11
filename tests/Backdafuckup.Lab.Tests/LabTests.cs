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
using Backdafuckup.Core.Tls;
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

    private static string? Env(string name) => LabEnvironment.Get(name);

    /// <summary>BDFU_LAB_TRANSPORT = Automatic (default), NbdOnly or HttpExportOnly.</summary>
    private static DiskTransportMode Transport =>
        Enum.TryParse<DiskTransportMode>(Env("BDFU_LAB_TRANSPORT"), true, out var mode) ? mode : DiskTransportMode.Automatic;

    /// <summary>
    /// Checks changed block tracking on a real pool: the blocks that really differ between two snapshots must lie in the
    /// ranges that backups read (whole bitmap bytes, independent of the bit order), and tells which bit order the pool
    /// uses (XenServer documents most significant bit first). CBT may over-report, never under-report.
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
        var ledger1 = new InMemoryArtifactLedger();
        await using (var first = await session.BeginBackupAsync(Request(vm), log, ledger1, Ct))
        {
            var disk = first.Disks[0];
            before = await HashBlocksAsync(first, disk);
            token1 = first.GetNewChangeToken(disk) ?? throw new InvalidOperationException("CBT is not available for this disk.");
            await CommitAsync(session, first, ledger1, log);
        }

        var wait = int.TryParse(Env("BDFU_LAB_WAIT_SECONDS"), out var seconds) ? seconds : 120;
        LabEnvironment.Say($"Waiting {wait} s – write some data inside the VM now (e.g. dd if=/dev/urandom of=/tmp/x bs=1M count=50; sync).");
        await Task.Delay(TimeSpan.FromSeconds(wait), Ct);

        var ledger2 = new InMemoryArtifactLedger();
        await using var second = await session.BeginBackupAsync(Request(vm), log, ledger2, Ct);
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
        var read = Blocks(CbtBitmap.DecodeByteRanges(Convert.FromBase64String(bitmap), disk2.CapacityBytes));
        var token2 = second.GetNewChangeToken(disk2);
        await CommitAsync(session, second, ledger2, log);
        await session.ReleaseChangeTokensAsync(new[] { token1, token2 }.OfType<ChangeToken>(), log, Ct);

        Assert.SkipWhen(actual.Count == 0, "The VM did not change any data between the snapshots; write data during the wait and run again.");
        var msbCovers = actual.IsSubsetOf(msb);
        var lsbCovers = actual.IsSubsetOf(lsb);
        var report = $"Changed 64 KiB blocks: {actual.Count}; bitmap marks {msb.Count}, backups read {read.Count}. MSB-first covers all: {msbCovers}; LSB-first covers all: {lsbCovers}.";
        LabEnvironment.Say(report);
        Assert.True(actual.IsSubsetOf(read), "Backups would miss changed blocks: " + report);
        Assert.True(msbCovers ^ lsbCovers, "Bit order inconclusive (backups are still complete): " + report);
        LabEnvironment.Say(msbCovers
            ? "==> The pool reports changed blocks most significant bit first, as XenServer documents."
            : "==> The pool reports changed blocks least significant bit first, unlike XenServer's documentation; leave Xen:CbtBitOrder unset.");
    }

    /// <summary>Full backup (transport from BDFU_LAB_TRANSPORT) into a temporary repository, restore to another SR, compare disk hashes.</summary>
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

        var ledger = new InMemoryArtifactLedger();
        await using (var backup = await session.BeginBackupAsync(Request(Env("BDFU_LAB_VM")!, rpId), log, ledger, Ct))
        await using (var writer = repository.BeginWrite(rpId, backup.Vm.VmId, null))
        {
            var disks = new List<ManifestDisk>();
            foreach (var disk in backup.Disks)
            {
                await using var reader = await backup.OpenReaderAsync(disk, new DiskReaderOptions(4, 4, Transport), Ct);
                LabEnvironment.Say($"Disk {disk.Name}: {disk.CapacityBytes} bytes via {reader.TransportName}.");
                var started = DateTime.UtcNow;
                var result = await DiskBackupPipeline.RunAsync(new DiskBackupJob { Reader = reader, Writer = writer, ChunkSize = repository.Info.ChunkSize, Changes = ChangedExtents.Full("lab") }, Ct);
                var seconds = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
                LabEnvironment.Say($"Disk {disk.Name}: read {result.Stats.ReadBytes / 1048576.0:0} MiB in {seconds:0.0} s ({result.Stats.ReadBytes / 1048576.0 / seconds:0} MiB/s), stored {result.Stats.NewStoredBytes / 1048576.0:0} MiB.");
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
            await CommitAsync(session, backup, ledger, log);
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
                Assert.Equal(expected[source], await HashVdiAsync(xapi, restoredDisk));
            }

            LabEnvironment.Say($"Restored VM '{restored.Name}' matches the backup bit for bit.");
        }
        finally
        {
            await XenHypervisorSessionCleanup.DestroyVmAndDisksAsync(lab, restored.VmId);
            await session.ReleaseChangeTokensAsync(tokens, log, Ct);
            await reader2.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Checks the VHD stream parser against the real export: the same snapshot disk exported as VHD (parsed by
    /// backdafuckup) and as raw must give identical 1 MiB block hashes. Works without NBD.
    /// </summary>
    [Fact]
    public async Task Http_export_vhd_matches_raw()
    {
        var lab = await LabConnection.ConnectAsync();
        var log = new ConsoleLog();
        await using var session = lab.Session;
        await using var backup = await session.BeginBackupAsync(Request(Env("BDFU_LAB_VM")!), log, new InMemoryArtifactLedger(), Ct);
        await using var xapi = await lab.LoginAsync();
        foreach (var disk in backup.Disks)
        {
            string[] vhd;
            await using (var source = await backup.OpenReaderAsync(disk, new DiskReaderOptions(Transport: DiskTransportMode.HttpExportOnly), Ct))
            {
                vhd = await HashChunksAsync(Assert.IsAssignableFrom<ISequentialDiskReader>(source));
            }

            var raw = await HashRawExportChunksAsync(xapi, disk.SnapshotDiskId, disk.CapacityBytes);
            var differing = Enumerable.Range(0, raw.Length).Where(i => raw[i] != vhd[i]).ToList();
            Assert.True(differing.Count == 0, $"Disk {disk.Name}: {differing.Count} of {raw.Length} MiB differ between VHD and raw export, first at MiB {differing.FirstOrDefault()}.");
            LabEnvironment.Say($"Disk {disk.Name}: VHD and raw export are identical ({raw.Length} MiB).");
        }
    }

    /// <summary>
    /// Commits the backup like the service does. If XAPI still reports the snapshot disk in use (VDI_IN_USE), the service
    /// defers the cleanup to its background retry; here the test finishes it and reports how long the disk stayed in use.
    /// </summary>
    private static async Task CommitAsync(IHypervisorSession session, IVmBackupSession backup, InMemoryArtifactLedger ledger, IOperationLog log)
    {
        var started = DateTime.UtcNow;
        try
        {
            await backup.CommitAsync(log, Ct);
            return;
        }
        catch (SnapshotCleanupDeferredException ex)
        {
            LabEnvironment.Say($"Commit deferred: {ex.Message}");
        }

        // Snapshot disks first (data_destroy), then the snapshot VM, as in XenBackupSession.CommitAsync.
        var pending = ledger.Items.Where(a => a.State == ArtifactState.CommitPending).OrderBy(a => a.Kind == XenArtifactKinds.VmSnapshot).ToList();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                foreach (var artifact in pending.ToList())
                {
                    await session.CommitArtifactAsync(artifact, log, Ct);
                    pending.Remove(artifact);
                }

                LabEnvironment.Say($"Snapshot committed {(DateTime.UtcNow - started).TotalSeconds:0} s after the backup finished reading.");
                return;
            }
            catch (XenSnapshotInUseException ex) when (attempt < 3)
            {
                LabEnvironment.Say($"Attempt {attempt}: {ex.Message}");
            }
        }
    }

    private static VmBackupRequest Request(string vm, Guid? restorePoint = null) =>
        new(vm, Guid.Empty, Guid.NewGuid(), restorePoint ?? Guid.NewGuid(), "backdafuckup lab test");

    private static HashSet<int> Blocks(IReadOnlyList<DiskExtent> extents) =>
        extents.SelectMany(e => Enumerable.Range((int)(e.Offset / CbtBitmap.BlockSize), (int)((e.Length + CbtBitmap.BlockSize - 1) / CbtBitmap.BlockSize))).ToHashSet();

    private static async Task<byte[][]> HashBlocksAsync(IVmBackupSession backup, SnapshotDisk disk)
    {
        await using var source = await backup.OpenReaderAsync(disk, new DiskReaderOptions(4, 4, Transport), Ct);
        var count = (int)((disk.CapacityBytes + CbtBitmap.BlockSize - 1) / CbtBitmap.BlockSize);
        var hashes = new byte[count][];
        if (source is ISequentialDiskReader sequential)
        {
            return await HashStreamBlocksAsync(sequential, CbtBitmap.BlockSize);
        }

        var reader = (IDiskReader)source;
        await Parallel.ForAsync(0, count, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = Ct }, async (i, ct) =>
        {
            var length = (int)Math.Min(CbtBitmap.BlockSize, disk.CapacityBytes - ((long)i * CbtBitmap.BlockSize));
            var buffer = new byte[length];
            await reader.ReadAsync((long)i * CbtBitmap.BlockSize, buffer, ct);
            hashes[i] = SHA256.HashData(buffer);
        });
        return hashes;
    }

    /// <summary>SHA-256 of a whole VDI, read with a raw HTTP export (works with and without NBD).</summary>
    private static async Task<string> HashVdiAsync(XapiSession xapi, string vdiUuid)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await foreach (var block in RawExportBlocksAsync(xapi, vdiUuid))
        {
            sha.AppendData(block.Span);
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static async Task<string[]> HashRawExportChunksAsync(XapiSession xapi, string vdiUuid, long capacity)
    {
        var hashes = new List<string>();
        await foreach (var block in RawExportBlocksAsync(xapi, vdiUuid))
        {
            hashes.Add(Convert.ToHexString(SHA256.HashData(block.Span)));
        }

        Assert.Equal((capacity + 1048575) / 1048576, hashes.Count);
        return hashes.ToArray();
    }

    /// <summary>The disk in 1 MiB blocks from GET /export_raw_vdi?format=raw (the last block may be shorter).</summary>
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> RawExportBlocksAsync(XapiSession xapi, string vdiUuid)
    {
        var vdiRef = await xapi.TryGetByUuidAsync("VDI", vdiUuid, Ct) ?? throw new InvalidOperationException($"VDI {vdiUuid} not found.");
        using var response = await xapi.Transport.SendHandlerAsync(
            HttpMethod.Get, $"export_raw_vdi?session_id={Uri.EscapeDataString(xapi.SessionRef)}&vdi={Uri.EscapeDataString(vdiRef)}&format=raw", null, Ct);
        Assert.True(response.IsSuccessStatusCode, $"export_raw_vdi of {vdiUuid} answered HTTP {(int)response.StatusCode}.");
        await using var body = await response.Content.ReadAsStreamAsync(Ct);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, Ct);
            if (read == 0)
            {
                yield break;
            }

            yield return buffer.AsMemory(0, read);
        }
    }

    private static async Task<string[]> HashChunksAsync(ISequentialDiskReader reader) =>
        (await HashStreamBlocksAsync(reader, 1024 * 1024)).Select(Convert.ToHexString).ToArray();

    /// <summary>SHA-256 per block of a streamed disk without holding the disk in memory; blocks never streamed are zeros.</summary>
    private static async Task<byte[][]> HashStreamBlocksAsync(ISequentialDiskReader reader, int blockSize)
    {
        var capacity = reader.Capacity;
        var count = (int)((capacity + blockSize - 1) / blockSize);
        var hashes = new byte[count][];
        var buffer = new byte[blockSize];
        long current = -1;

        int Length(long block) => (int)Math.Min(blockSize, capacity - (block * blockSize));

        void Flush()
        {
            if (current >= 0)
            {
                hashes[current] = SHA256.HashData(buffer.AsSpan(0, Length(current)));
                Array.Clear(buffer);
                current = -1;
            }
        }

        await reader.StreamAsync(
            (offset, data, _) =>
            {
                while (!data.IsEmpty)
                {
                    var block = offset / blockSize;
                    if (block != current)
                    {
                        Flush();
                        current = block;
                    }

                    var within = (int)(offset - (block * blockSize));
                    var take = Math.Min(data.Length, blockSize - within);
                    data.Span[..take].CopyTo(buffer.AsSpan(within));
                    offset += take;
                    data = data[take..];
                }

                return ValueTask.CompletedTask;
            },
            Ct);
        Flush();

        for (var i = 0; i < count; i++)
        {
            hashes[i] ??= SHA256.HashData(new byte[Length(i)]);
        }

        return hashes;
    }

    private sealed class ConsoleLog : IOperationLog
    {
        public void Write(LogSeverity severity, string message, string? key = null) => LabEnvironment.Say($"{severity}: {message}");
    }
}

/// <summary>Connection to the lab pool with trust-on-first-use pinning of all pool hosts.</summary>
internal sealed record LabConnection(XenProvider Provider, ConnectionSettings Settings, IHypervisorSession Session)
{
    public static async Task<LabConnection> ConnectAsync()
    {
        // Explicit opt-in per run, never from the settings file: a solution-wide test run must not touch a real pool.
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("BDFU_LAB_RUN") == "1",
            "Lab tests run against a real pool and create and remove snapshots and a VM copy. Set BDFU_LAB_RUN=1 for the run to enable them.");
        var host = LabEnvironment.Get("BDFU_LAB_HOST");
        LabEnvironment.Say($"Lab pool {host ?? "(not set)"}, VM {LabEnvironment.Get("BDFU_LAB_VM") ?? "(not set)"}, transport {LabEnvironment.Get("BDFU_LAB_TRANSPORT") ?? "Automatic"}.");
        Assert.SkipWhen(
            string.IsNullOrEmpty(host) || string.IsNullOrEmpty(LabEnvironment.Get("BDFU_LAB_VM")) || string.IsNullOrEmpty(LabEnvironment.Get("BDFU_LAB_PASSWORD")),
            $"Lab tests need BDFU_LAB_HOST, BDFU_LAB_USER, BDFU_LAB_PASSWORD and BDFU_LAB_VM (environment or {LabEnvironment.FilePath}).");
        var provider = new XenProvider(new XenOptions
        {
            HttpImportFormat = Enum.TryParse<XenHttpImportFormat>(LabEnvironment.Get("BDFU_LAB_IMPORT_FORMAT"), true, out var import) ? import : XenHttpImportFormat.Vhd,
        });
        var credential = new NetworkCredential(LabEnvironment.Get("BDFU_LAB_USER") ?? "root", LabEnvironment.Get("BDFU_LAB_PASSWORD"));
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
                LabEnvironment.Say($"Connected to {result.Platform!.PlatformName} ({result.Platform.ProductName} {result.Platform.ProductVersion}).");
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
