using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backdafuckup.Agent.Protocol;
using DiskAccessLibrary;
using ISCSI.Server;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// The disk content of a publish, fetched from the backup server block by block over the agent's HTTPS channel. Keeps
/// the last blocks in memory and reads ahead when the guest reads sequentially. Called from the iSCSI target's command
/// thread (one command at a time), so it blocks on the download there.
/// </summary>
public sealed class PublishBlockSource : IDisposable
{
    private const int CacheBlocks = 32;
    private const int ReadAhead = 4;

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly int _blockSize;
    private readonly object _lock = new object();
    private readonly Dictionary<long, LinkedListNode<KeyValuePair<long, byte[]>>> _cache = new Dictionary<long, LinkedListNode<KeyValuePair<long, byte[]>>>();
    private readonly LinkedList<KeyValuePair<long, byte[]>> _lru = new LinkedList<KeyValuePair<long, byte[]>>();
    private readonly Dictionary<long, Task<byte[]>> _inflight = new Dictionary<long, Task<byte[]>>();
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private long _last = -2;
    private long _fetched;

    /// <param name="url">Block URL without the block number, e.g. <c>api/agent/publishes/{id}/disks/0/blocks/</c>.</param>
    public PublishBlockSource(HttpClient http, string url, long size, int blockSize)
    {
        _http = http;
        _url = url;
        Size = size;
        _blockSize = blockSize;
    }

    public long Size { get; }

    public long FetchedBytes => Interlocked.Read(ref _fetched);

    /// <summary>Reads <paramref name="count"/> bytes at <paramref name="offset"/>; throws <see cref="IOException"/> if a block cannot be fetched.</summary>
    public void Read(long offset, byte[] buffer, int index, int count)
    {
        while (count > 0)
        {
            var block = offset / _blockSize;
            var inner = (int)(offset % _blockSize);
            var data = Block(block);
            var n = Math.Min(count, data.Length - inner);
            if (n <= 0)
            {
                throw new IOException($"Reading beyond the end of the published disk (offset {offset}).");
            }

            Buffer.BlockCopy(data, inner, buffer, index, n);
            offset += n;
            index += n;
            count -= n;
        }
    }

    public void Dispose() => _stop.Cancel();

    private byte[] Block(long block)
    {
        Task<byte[]> task;
        lock (_lock)
        {
            var sequential = block == _last + 1;
            _last = block;
            if (_cache.TryGetValue(block, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                if (sequential)
                {
                    Prefetch(block);
                }

                return node.Value.Value;
            }

            task = Start(block);
            if (sequential)
            {
                Prefetch(block);
            }
        }

        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (!(ex is IOException))
        {
            throw new IOException($"Block {block} could not be fetched from the backup server: {ex.Message}", ex);
        }
    }

    /// <summary>Under the lock: starts the next blocks of a sequential read.</summary>
    private void Prefetch(long block)
    {
        for (var i = 1; i <= ReadAhead; i++)
        {
            var next = block + i;
            if (next * (long)_blockSize >= Size)
            {
                break;
            }

            if (!_cache.ContainsKey(next))
            {
                Start(next);
            }
        }
    }

    /// <summary>Under the lock.</summary>
    private Task<byte[]> Start(long block)
    {
        if (_inflight.TryGetValue(block, out var running))
        {
            return running;
        }

        var task = FetchAsync(block);
        if (!task.IsCompleted)
        {
            _inflight[block] = task;
        }

        return task;
    }

    private async Task<byte[]> FetchAsync(long block)
    {
        try
        {
            var expected = (int)Math.Min(_blockSize, Size - block * _blockSize);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using (var response = await _http.GetAsync(_url + block.ToString(CultureInfo.InvariantCulture), HttpCompletionOption.ResponseHeadersRead, _stop.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new IOException($"The backup server answered {(int)response.StatusCode} for block {block}.");
                        }

                        var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        if (data.Length != expected)
                        {
                            throw new IOException($"Block {block} has {data.Length} bytes instead of {expected}.");
                        }

                        Interlocked.Add(ref _fetched, data.Length);
                        lock (_lock)
                        {
                            if (!_cache.ContainsKey(block))
                            {
                                _cache[block] = _lru.AddFirst(new KeyValuePair<long, byte[]>(block, data));
                                while (_lru.Count > CacheBlocks)
                                {
                                    _cache.Remove(_lru.Last!.Value.Key);
                                    _lru.RemoveLast();
                                }
                            }
                        }

                        return data;
                    }
                }
                catch (Exception ex) when (attempt < 3 && !_stop.IsCancellationRequested && (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException))
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), _stop.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _inflight.Remove(block);
            }
        }
    }
}

/// <summary>Shared limit of the overlay files of one publish.</summary>
public sealed class OverlayBudget
{
    private long _used;

    public OverlayBudget(long limit) => Limit = limit;

    public long Limit { get; }

    public long Used => Interlocked.Read(ref _used);

    public bool TryTake(long bytes)
    {
        if (Interlocked.Add(ref _used, bytes) <= Limit)
        {
            return true;
        }

        Interlocked.Add(ref _used, -bytes);
        return false;
    }
}

/// <summary>
/// A published disk: reads come from the backup (<see cref="PublishBlockSource"/>), writes go to an overlay file in the
/// guest that is deleted when the disk is closed. The backup never changes. Errors are <see cref="IOException"/>s, which
/// the iSCSI target reports as medium errors (any other exception would end its command thread).
/// </summary>
public sealed class CowDisk : Disk, IDisposable
{
    public const int OverlayBlock = 64 * 1024;
    private const int Sector = 512;

    private readonly PublishBlockSource _source;
    private readonly FileStream _overlay;
    private readonly OverlayBudget _budget;
    private readonly Dictionary<long, long> _map = new Dictionary<long, long>();
    private readonly object _gate = new object();

    public CowDisk(PublishBlockSource source, string overlayPath, OverlayBudget budget)
    {
        _source = source;
        _budget = budget;
        _overlay = new FileStream(overlayPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
    }

    public override int BytesPerSector => Sector;

    public override long Size => _source.Size;

    /// <summary>Bytes fetched from the backup server so far.</summary>
    public long FetchedBytes => _source.FetchedBytes;

    public long OverlayBytes
    {
        get
        {
            lock (_gate)
            {
                return (long)_map.Count * OverlayBlock;
            }
        }
    }

    public override byte[] ReadSectors(long sectorIndex, int sectorCount)
    {
        var (offset, length) = Range(sectorIndex, sectorCount);
        var result = new byte[length];
        try
        {
            var done = 0;
            while (done < length)
            {
                var position = offset + done;
                var block = position / OverlayBlock;
                var inner = (int)(position % OverlayBlock);
                var n = Math.Min(length - done, OverlayBlock - inner);
                var fromOverlay = false;
                lock (_gate)
                {
                    if (_map.TryGetValue(block, out var at))
                    {
                        _overlay.Position = at + inner;
                        ReadExactly(_overlay, result, done, n);
                        fromOverlay = true;
                    }
                }

                if (!fromOverlay)
                {
                    _source.Read(position, result, done, n);
                }

                done += n;
            }
        }
        catch (Exception ex) when (!(ex is IOException))
        {
            throw new IOException($"Reading the published disk failed: {ex.Message}", ex);
        }

        return result;
    }

    public override void WriteSectors(long sectorIndex, byte[] data)
    {
        var (offset, length) = Range(sectorIndex, data.Length / Sector);
        try
        {
            lock (_gate)
            {
                var done = 0;
                while (done < length)
                {
                    var position = offset + done;
                    var block = position / OverlayBlock;
                    var inner = (int)(position % OverlayBlock);
                    var n = Math.Min(length - done, OverlayBlock - inner);
                    if (!_map.TryGetValue(block, out var at))
                    {
                        if (!_budget.TryTake(OverlayBlock))
                        {
                            throw new IOException($"The overlay of the publish is full ({_budget.Limit / (1024 * 1024)} MiB): writes to the published volumes fail.");
                        }

                        // Copy-on-write: the block as it is in the backup first, then the new data over it.
                        var start = block * OverlayBlock;
                        var copy = new byte[OverlayBlock];
                        _source.Read(start, copy, 0, (int)Math.Min(OverlayBlock, Size - start));
                        at = _overlay.Length;
                        _overlay.Position = at;
                        _overlay.Write(copy, 0, copy.Length);
                        _map[block] = at;
                    }

                    _overlay.Position = at + inner;
                    _overlay.Write(data, done, n);
                    done += n;
                }
            }
        }
        catch (Exception ex) when (!(ex is IOException))
        {
            throw new IOException($"Writing to the published disk failed: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _overlay.Dispose();
        }

        _source.Dispose();
    }

    private (long Offset, int Length) Range(long sectorIndex, int sectorCount)
    {
        var offset = sectorIndex * Sector;
        var length = (long)sectorCount * Sector;
        if (sectorIndex < 0 || sectorCount < 0 || offset + length > Size)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorIndex), $"Sectors {sectorIndex}+{sectorCount} are beyond the end of the disk.");
        }

        return (offset, (int)length);
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            var n = stream.Read(buffer, offset, count);
            if (n <= 0)
            {
                throw new IOException("The overlay file is shorter than expected.");
            }

            offset += n;
            count -= n;
        }
    }
}

/// <summary>
/// Gives a published disk a new identity (in the overlay), so that Windows sees no collision with the disk it was copied
/// from: a new MBR signature, or new GPT disk and partition GUIDs (both headers, CRCs fixed) with "no drive letter" on
/// every GPT partition.
/// </summary>
public static class DiskIdentity
{
    private const int Sector = 512;

    public static string Patch(Disk disk)
    {
        var mbr = disk.ReadSectors(0, 1);
        if (mbr[510] != 0x55 || mbr[511] != 0xAA)
        {
            return "no partition table";
        }

        if (mbr[450] != 0xEE)
        {
            var signature = new byte[4];
            Random(signature);
            Buffer.BlockCopy(signature, 0, mbr, 0x1B8, 4);
            disk.WriteSectors(0, mbr);
            return $"MBR signature {BitConverter.ToUInt32(signature, 0):X8}";
        }

        var header = disk.ReadSectors(1, 1);
        if (Encoding.ASCII.GetString(header, 0, 8) != "EFI PART")
        {
            return "GPT without a valid header; not changed";
        }

        var entriesLba = BitConverter.ToInt64(header, 72);
        var count = BitConverter.ToInt32(header, 80);
        var entrySize = BitConverter.ToInt32(header, 84);
        var backupLba = BitConverter.ToInt64(header, 32);
        if (count <= 0 || entrySize < 128 || (long)count * entrySize > 1 << 20)
        {
            return "GPT with an unusual partition array; not changed";
        }

        var entrySectors = (int)(((long)count * entrySize + Sector - 1) / Sector);
        var entries = disk.ReadSectors(entriesLba, entrySectors);
        for (var i = 0; i < count; i++)
        {
            var entry = i * entrySize;
            if (entries.Skip(entry).Take(16).All(b => b == 0))
            {
                continue;
            }

            Buffer.BlockCopy(Guid.NewGuid().ToByteArray(), 0, entries, entry + 16, 16);
            var attributes = BitConverter.ToUInt64(entries, entry + 48) | 0x8000000000000000UL;
            Buffer.BlockCopy(BitConverter.GetBytes(attributes), 0, entries, entry + 48, 8);
        }

        var entriesCrc = Crc32(entries, 0, count * entrySize);
        var diskGuid = Guid.NewGuid();
        WriteHeader(disk, 1, header, diskGuid, entriesCrc);
        disk.WriteSectors(entriesLba, entries);
        if (backupLba > 1 && backupLba < disk.TotalSectors)
        {
            var backup = disk.ReadSectors(backupLba, 1);
            if (Encoding.ASCII.GetString(backup, 0, 8) == "EFI PART")
            {
                disk.WriteSectors(BitConverter.ToInt64(backup, 72), entries);
                WriteHeader(disk, backupLba, backup, diskGuid, entriesCrc);
            }
        }

        return $"GPT disk GUID {diskGuid}, new partition GUIDs, no drive letters";
    }

    private static void WriteHeader(Disk disk, long lba, byte[] header, Guid diskGuid, uint entriesCrc)
    {
        Buffer.BlockCopy(diskGuid.ToByteArray(), 0, header, 56, 16);
        Buffer.BlockCopy(BitConverter.GetBytes(entriesCrc), 0, header, 88, 4);
        var size = Math.Min(BitConverter.ToInt32(header, 12), Sector);
        Buffer.BlockCopy(BitConverter.GetBytes(0u), 0, header, 16, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(Crc32(header, 0, size)), 0, header, 16, 4);
        disk.WriteSectors(lba, header);
    }

    private static void Random(byte[] bytes)
    {
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
    }

    internal static uint Crc32(byte[] data, int offset, int count)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = offset; i < offset + count; i++)
        {
            crc ^= data[i];
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}

/// <summary>
/// The iSCSI target of one publish: listens on 127.0.0.1 on a free port, serves the disks as LUN 0, 1, …, gives each a
/// serial number by which the guest finds it, and lets only the authorized initiator log in.
/// </summary>
public sealed class PublishTarget : IDisposable
{
    private readonly ISCSIServer _server = new ISCSIServer();

    public PublishTarget(string targetName, IList<Disk> disks, string serialPrefix, Func<IPEndPoint, string, bool> authorize, Action<string> log)
    {
        TargetName = targetName;
        SerialPrefix = serialPrefix;
        var target = new ISCSITarget(targetName, new List<Disk>(disks));
        target.OnAuthorizationRequest += (_, e) =>
        {
            e.Accept = authorize(e.InitiatorEndPoint, e.InitiatorName);
            if (!e.Accept)
            {
                log($"Refused an iSCSI login from {e.InitiatorName} at {e.InitiatorEndPoint}: not the guest's iSCSI initiator.");
            }
        };
        target.OnUnitSerialNumberInquiry += (_, e) => e.Page.ProductSerialNumber = Serial(serialPrefix, (ushort)e.LUN);
        _server.AddTarget(target);
        _server.OnLogEntry += (_, e) =>
        {
            if (e.Severity <= ISCSI.Logging.Severity.Error)
            {
                log($"iSCSI target: {e.Message}");
            }
        };
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _server.Start(new IPEndPoint(IPAddress.Loopback, Port));
    }

    public string TargetName { get; }

    public string SerialPrefix { get; }

    public int Port { get; }

    /// <summary>The serial number of LUN <paramref name="lun"/>, e.g. BDFU1A2B3C4D-0.</summary>
    public static string Serial(string prefix, int lun) => prefix + "-" + lun.ToString(CultureInfo.InvariantCulture);

    public void Dispose() => _server.Stop();
}

/// <summary>The guest side of publishing: the iSCSI initiator and the disk management of Windows.</summary>
public interface IPublishHost
{
    /// <summary>
    /// Folder for the journal, the overlays and the mount points, e.g. <c>%ProgramFiles%\backdafuckup Agent\publish</c>.
    /// Only SYSTEM and administrators may read it: the overlays hold what was written to the published volumes.
    /// </summary>
    string WorkFolder { get; }

    /// <summary>Only the guest's own iSCSI initiator may log in to the loopback target.</summary>
    bool Authorize(IPEndPoint initiator, string initiatorName);

    /// <summary>Logs the initiator in to the target (not persistent: a reboot drops it).</summary>
    void Attach(int port, string targetName, Action<string> log);

    /// <summary>The disk number of the disk with this SCSI serial number (waits briefly for it to arrive).</summary>
    int FindDisk(string serial, Action<string> log);

    /// <summary>Brings the disk online and writable (the writes go to the overlay).</summary>
    void Online(int disk, Action<string> log);

    /// <summary>Mounts the partition that starts at <paramref name="offset"/> into the empty folder, without a drive letter.</summary>
    void Mount(int disk, long offset, string folder, Action<string> log);

    void Unmount(string folder, Action<string> log);

    void Offline(int disk, Action<string> log);

    /// <summary>Logs out of the target and removes its portal.</summary>
    void Detach(int port, string targetName, Action<string> log);
}

/// <summary>Publishes that are not cleaned up yet, so that a restart of the agent or the VM can finish the cleanup.</summary>
public sealed class PublishJournal
{
    private readonly string _file;
    private readonly object _lock = new object();

    public PublishJournal(string folder) => _file = Path.Combine(folder, "publishes.json");

    public List<Entry> Read()
    {
        lock (_lock)
        {
            try
            {
                return File.Exists(_file) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_file), AgentJson.Options) ?? new List<Entry>() : new List<Entry>();
            }
            catch (JsonException)
            {
                return new List<Entry>();
            }
        }
    }

    public void Add(Entry entry) => Change(list => list.Add(entry));

    public void Remove(Guid publishId) => Change(list => list.RemoveAll(e => e.PublishId == publishId));

    private void Change(Action<List<Entry>> change)
    {
        lock (_lock)
        {
            var list = Read();
            change(list);
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(list, AgentJson.Options));
        }
    }

    public sealed class Entry
    {
        public Guid PublishId { get; set; }

        public string TargetName { get; set; } = string.Empty;

        public int Port { get; set; }

        public string Folder { get; set; } = string.Empty;

        public List<string> Mounts { get; set; } = new List<string>();
    }
}

/// <summary>
/// One publish in the guest: fetches what to publish, starts the loopback target with copy-on-write disks, lets the
/// guest's initiator log in, mounts the volumes, then reports every few seconds until the server says stop (or answers
/// 404, or stays silent for <see cref="SilenceLimit"/>), and removes everything again.
/// </summary>
public sealed class PublishRunner
{
    public static readonly TimeSpan SilenceLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly Guid _id;
    private readonly IPublishHost _host;
    private readonly AgentLog _log;
    private readonly PublishJournal _journal;
    private readonly List<string> _lines = new List<string>();
    private readonly PublishStatus _status = new PublishStatus();

    public PublishRunner(HttpClient http, Guid id, IPublishHost host, AgentLog log)
    {
        _http = http;
        _id = id;
        _host = host;
        _log = log;
        _journal = new PublishJournal(host.WorkFolder);
    }

    private string Base => AgentProtocol.PublishesPath + "/" + _id.ToString("D");

    /// <summary>
    /// Removes what publishes left behind when the agent or the VM stopped in the middle, or what a teardown could not
    /// remove (from the journal). Runs at agent start and before every publish; an entry stays until it is clean.
    /// </summary>
    public static void CleanupStale(IPublishHost host, AgentLog log)
    {
        var journal = new PublishJournal(host.WorkFolder);
        foreach (var entry in journal.Read())
        {
            void Log(string line) => log.Write($"Publish {entry.PublishId} (left over): {line}");
            var clean = true;
            foreach (var mount in entry.Mounts.AsEnumerable().Reverse())
            {
                clean &= Try(() => host.Unmount(mount, Log), Log);
            }

            if (entry.TargetName.Length > 0)
            {
                clean &= Try(() => host.Detach(entry.Port, entry.TargetName, Log), Log);
            }

            clean &= DeleteFolders(entry.Folder, entry.Mounts, Log);
            if (clean)
            {
                journal.Remove(entry.PublishId);
                Log("cleaned up.");
            }
            else
            {
                Log("not everything could be removed; the agent tries again before its next publish and when it starts.");
            }
        }
    }

    public async Task RunAsync(CancellationToken stop)
    {
        PublishTarget? target = null;
        var disks = new List<CowDisk>();
        var diskNumbers = new List<int>();
        var mounts = new List<string>();
        var entry = new PublishJournal.Entry { PublishId = _id };
        try
        {
            CleanupStale(_host, _log);
            var info = await GetAsync(stop).ConfigureAwait(false);
            entry.Folder = Path.Combine(_host.WorkFolder, _id.ToString("N").Substring(0, 12));
            Directory.CreateDirectory(entry.Folder);
            Write($"publishing {info.Title}: {info.Disks.Count} disk(s), {info.Volumes.Count} volume(s); writes go to an overlay in {entry.Folder} (at most {info.MaxOverlayBytes / (1024 * 1024)} MiB).");
            var budget = new OverlayBudget(info.MaxOverlayBytes);
            foreach (var d in info.Disks)
            {
                var source = new PublishBlockSource(_http, $"{Base}/disks/{d.Index.ToString(CultureInfo.InvariantCulture)}/blocks/", d.SizeBytes, info.BlockSize);
                var disk = new CowDisk(source, Path.Combine(entry.Folder, $"overlay-{d.Index}.bin"), budget);
                disks.Add(disk);
                Write($"disk {d.Index} ({d.SizeBytes / (1024 * 1024)} MiB): {DiskIdentity.Patch(disk)}.");
                await CheckpointAsync(disks, stop).ConfigureAwait(false);
            }

            var serialPrefix = "BDFU" + _id.ToString("N").Substring(0, 8).ToUpperInvariant();
            target = new PublishTarget($"iqn.2026-11.de.backdafuckup:publish-{_id:N}", disks.Cast<Disk>().ToList(), serialPrefix, _host.Authorize, Write);
            entry.TargetName = target.TargetName;
            entry.Port = target.Port;
            _journal.Add(entry);
            Write($"iSCSI target {target.TargetName} on 127.0.0.1:{target.Port}.");
            _host.Attach(target.Port, target.TargetName, Write);
            for (var i = 0; i < disks.Count; i++)
            {
                var number = _host.FindDisk(PublishTarget.Serial(serialPrefix, i), Write);
                diskNumbers.Add(number);
                _host.Online(number, Write);
                await CheckpointAsync(disks, stop).ConfigureAwait(false);
            }

            foreach (var volume in info.Volumes)
            {
                var folder = Path.Combine(entry.Folder, SafeName(volume.Name));
                Directory.CreateDirectory(folder);
                mounts.Add(folder);
                entry.Mounts = new List<string>(mounts);
                _journal.Remove(_id);
                _journal.Add(entry);
                _host.Mount(diskNumbers[volume.DiskIndex], volume.StartOffset, folder, Write);
                _status.Mounts.Add(new PublishMount { Name = volume.Name, Path = folder });
                await CheckpointAsync(disks, stop).ConfigureAwait(false);
            }

            _status.State = PublishStates.Mounted;
            Write("mounted.");
            var lastContact = DateTime.UtcNow;
            while (!stop.IsCancellationRequested)
            {
                var answer = await ReportAsync(disks, stop).ConfigureAwait(false);
                if (answer == Answer.Stop)
                {
                    Write("unpublishing as the server asked.");
                    break;
                }

                if (answer == Answer.Ok)
                {
                    lastContact = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastContact > SilenceLimit)
                {
                    Write($"the backup server has not answered for {SilenceLimit.TotalMinutes:0} minutes; unpublishing.");
                    break;
                }

                await Delay(Heartbeat, stop).ConfigureAwait(false);
            }

            if (stop.IsCancellationRequested)
            {
                Write("the agent service is stopping; unpublishing.");
            }
        }
        catch (StopRequestedException)
        {
            Write("the server asked to stop before the volumes were mounted.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            Write("the agent service is stopping; unpublishing.");
        }
        catch (Exception ex) when (!(ex is OutOfMemoryException))
        {
            _status.State = PublishStates.Failed;
            _status.Error = ex.Message;
            Write($"failed: {ex.Message}");
        }
        finally
        {
            // In reverse; a failed step is logged and the rest still runs. Offline only matters while the session exists.
            var clean = true;
            foreach (var mount in mounts.AsEnumerable().Reverse())
            {
                clean &= Try(() => _host.Unmount(mount, Write), Write);
            }

            foreach (var number in diskNumbers)
            {
                Try(() => _host.Offline(number, Write), Write);
            }

            if (target is not null)
            {
                // Also after a failed attach: the portal may have been added before the login failed.
                clean &= Try(() => _host.Detach(target.Port, target.TargetName, Write), Write);
            }

            target?.Dispose();
            foreach (var disk in disks)
            {
                disk.Dispose();
            }

            if (entry.Folder.Length > 0)
            {
                clean &= DeleteFolders(entry.Folder, mounts, Write);
            }

            if (_status.State != PublishStates.Failed)
            {
                _status.State = PublishStates.Ended;
            }

            _status.Mounts.Clear();
            if (clean)
            {
                _journal.Remove(_id);
                Write("everything is removed again.");
            }
            else
            {
                Write("not everything could be removed (see above); the agent tries again before its next publish and when it starts.");
            }
            for (var attempt = 1; attempt <= 5 && await ReportAsync(disks, CancellationToken.None).ConfigureAwait(false) == Answer.NoContact; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
    }

    private sealed class StopRequestedException : Exception
    {
    }

    private enum Answer
    {
        Ok,
        Stop,
        NoContact,
    }

    private async Task<PublishInfo> GetAsync(CancellationToken stop)
    {
        using (var response = await _http.GetAsync(Base, stop).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"The server did not hand out publish {_id} ({(int)response.StatusCode}).");
            }

            return JsonSerializer.Deserialize<PublishInfo>(await response.Content.ReadAsStringAsync().ConfigureAwait(false), AgentJson.Options)
                ?? throw new InvalidOperationException("The publish request is empty.");
        }
    }

    /// <summary>Reports during the setup, so that the server sees progress and can stop it.</summary>
    private async Task CheckpointAsync(List<CowDisk> disks, CancellationToken stop)
    {
        if (await ReportAsync(disks, stop).ConfigureAwait(false) == Answer.Stop)
        {
            throw new StopRequestedException();
        }
    }

    private async Task<Answer> ReportAsync(List<CowDisk> disks, CancellationToken stop)
    {
        List<string> lines;
        lock (_lines)
        {
            lines = new List<string>(_lines);
        }

        _status.Log = lines;
        _status.OverlayBytes = disks.Sum(d => d.OverlayBytes);
        _status.ReadBytes = disks.Sum(d => d.FetchedBytes);
        try
        {
            using (var content = new StringContent(JsonSerializer.Serialize(_status, AgentJson.Options), Encoding.UTF8, "application/json"))
            using (var response = await _http.PostAsync(Base + "/status", content, stop).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return Answer.Stop;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Answer.NoContact;
                }

                lock (_lines)
                {
                    _lines.RemoveRange(0, Math.Min(lines.Count, _lines.Count));
                }

                var answer = JsonSerializer.Deserialize<PublishStatusResponse>(await response.Content.ReadAsStringAsync().ConfigureAwait(false), AgentJson.Options);
                return answer?.Stop == true ? Answer.Stop : Answer.Ok;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException)
        {
            return Answer.NoContact;
        }
    }

    private void Write(string line)
    {
        _log.Write($"Publish {_id}: {line}");
        lock (_lines)
        {
            _lines.Add(line);
        }
    }

    private static bool Try(Action action, Action<string> log)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (!(ex is OutOfMemoryException))
        {
            log($"cleanup step failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Only empty folders, never a mount point that is still there (a delete must not reach into a volume).</summary>
    private static bool DeleteFolders(string folder, IEnumerable<string> mounts, Action<string> log)
    {
        var clean = true;
        foreach (var mount in mounts)
        {
            clean &= Try(() =>
            {
                if (Directory.Exists(mount))
                {
                    if ((File.GetAttributes(mount) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException($"{mount} is still a mount point.");
                    }

                    Directory.Delete(mount, recursive: false);
                }
            }, log);
        }

        return clean && Try(() =>
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: false);
            }
        }, log);
    }

    private static string SafeName(string name)
    {
        var chars = name.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
        return chars.Length == 0 ? "volume" : new string(chars);
    }

    private static async Task Delay(TimeSpan delay, CancellationToken stop)
    {
        try
        {
            await Task.Delay(delay, stop).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
        }
    }
}
