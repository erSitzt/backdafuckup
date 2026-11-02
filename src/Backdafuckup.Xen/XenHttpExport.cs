using System.Buffers;
using System.Diagnostics;
using System.Net;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Xen.Xapi;

namespace Backdafuckup.Xen;

/// <summary>
/// Fallback transport: GET /export_raw_vdi streams a snapshot disk over the XAPI HTTPS connection (no NBD network
/// needed). One stream per disk; every backup transfers the whole allocated disk (VHD) or the whole disk (raw).
/// </summary>
internal static class XenHttpExport
{
    public static async Task<ISequentialDiskReader> OpenAsync(
        XenHypervisorSession owner,
        XapiSession xapi,
        IOperationLog log,
        SnapshotDisk disk,
        string snapshotVdiRef,
        CancellationToken cancellationToken)
    {
        var format = owner.Provider.Options.HttpExportFormat;
        var formatName = format == XenHttpExportFormat.Vhd ? "vhd" : "raw";
        var vdi = await xapi.GetRecordAsync("VDI", snapshotVdiRef, cancellationToken).ConfigureAwait(false);
        var exportHost = await XenSrHosts.ResolveAsync(xapi, vdi.Str("SR"), cancellationToken).ConfigureAwait(false);
        log.Info($"Disk {disk.Name}: HTTP export: {exportHost.Reason}.");

        var taskRef = await xapi.CreateTaskAsync($"backdafuckup backup: export {disk.Name}", cancellationToken).ConfigureAwait(false);
        log.Info($"Disk {disk.Name}: HTTP export: XAPI task.create → export task created ({format} format).");
        var host = exportHost.Address;
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                // A dedicated connection, closed after the export, so XAPI can detach the disk right away.
                var transport = owner.CreateTransport(host);
                var shown = $"{transport.BaseAddress.GetLeftPart(UriPartial.Authority)}/export_raw_vdi?vdi={vdi.Str("uuid")}&format={formatName}";
                var query = $"export_raw_vdi?session_id={Uri.EscapeDataString(xapi.SessionRef)}&task_id={Uri.EscapeDataString(taskRef)}&vdi={Uri.EscapeDataString(snapshotVdiRef)}&format={formatName}";
                log.Info($"Disk {disk.Name}: HTTP export: GET {shown} …");
                var watch = Stopwatch.StartNew();
                HttpResponseMessage response;
                try
                {
                    response = await transport.SendHandlerAsync(HttpMethod.Get, query, null, cancellationToken, closeConnection: true).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
                {
                    transport.Dispose();
                    throw new XenHttpExportException($"Disk {disk.Name}: HTTP export from {transport.BaseAddress.Authority} failed after {watch.ElapsedMilliseconds} ms: {Reason(ex)}.", ex);
                }

                if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect && response.Headers.Location is { } location && redirects < 3)
                {
                    log.Info($"Disk {disk.Name}: HTTP export: HTTP {(int)response.StatusCode} after {watch.ElapsedMilliseconds} ms, redirected to {location.Host}.");
                    response.Dispose();
                    transport.Dispose();
                    host = location.Host;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var status = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    response.Dispose();
                    transport.Dispose();
                    throw new XenHttpExportException($"Disk {disk.Name}: export_raw_vdi on {transport.BaseAddress.Authority} answered {status}.");
                }

                log.Info($"Disk {disk.Name}: HTTP export: HTTP {(int)response.StatusCode} from {transport.BaseAddress.Authority} after {watch.ElapsedMilliseconds} ms, streaming the disk ({formatName}).");
                return new XenHttpExportReader(owner, xapi, log, disk, response, transport, taskRef, format, transport.BaseAddress.Authority);
            }
        }
        catch
        {
            await xapi.TryDestroyTaskAsync(taskRef).ConfigureAwait(false);
            throw;
        }
    }

    internal static string Reason(Exception ex) => ex.InnerException is { } inner && inner.Message != ex.Message ? $"{ex.Message} ({inner.Message})" : ex.Message;
}

/// <summary>Reads the body of an export_raw_vdi response (raw or VHD) and hands the data to the backup pipeline.</summary>
internal sealed class XenHttpExportReader(
    XenHypervisorSession owner,
    XapiSession xapi,
    IOperationLog log,
    SnapshotDisk disk,
    HttpResponseMessage response,
    XapiTransport? ownedTransport,
    string taskRef,
    XenHttpExportFormat format,
    string endpoint) : ISequentialDiskReader
{
    private const int RawReadSize = 4 * 1024 * 1024;
    private int _streamed;
    private int _disposed;

    public long Capacity => disk.CapacityBytes;

    public string TransportName => format == XenHttpExportFormat.Vhd ? "http-export-vhd" : "http-export-raw";

    public async Task StreamAsync(DiskDataSink sink, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _streamed, 1) != 0)
        {
            throw new InvalidOperationException("The export can only be streamed once.");
        }

        var watch = Stopwatch.StartNew();
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            long transferred;
            if (format == XenHttpExportFormat.Vhd)
            {
                var vhd = await VhdStreamReader.OpenAsync(body, cancellationToken).ConfigureAwait(false);
                log.Info(
                    $"Disk {disk.Name}: HTTP export: dynamic VHD, virtual size {Bytes(vhd.CurrentSize)}, block size {Bytes(vhd.BlockSize)}, " +
                    $"{vhd.AllocatedBlocks} of {vhd.MaxTableEntries} blocks allocated ({Bytes(vhd.AllocatedBytes)} to transfer).");
                if (vhd.CurrentSize != disk.CapacityBytes)
                {
                    log.Warning($"Disk {disk.Name}: HTTP export: the VHD size {vhd.CurrentSize} differs from the disk size {disk.CapacityBytes}; the difference is treated as empty.");
                }

                await vhd.StreamAsync(disk.CapacityBytes, sink, cancellationToken).ConfigureAwait(false);
                transferred = vhd.AllocatedBytes;
                if (!vhd.EndedWithFooter)
                {
                    log.Warning($"Disk {disk.Name}: HTTP export: the VHD stream did not end with a footer; all {vhd.AllocatedBlocks} blocks were read completely.");
                }
            }
            else
            {
                transferred = await StreamRawAsync(body, sink, cancellationToken).ConfigureAwait(false);
            }

            await xapi.WaitForTaskAsync(taskRef, owner.Provider.Options.TaskTimeout, null, cancellationToken).ConfigureAwait(false);
            var seconds = Math.Max(0.001, watch.Elapsed.TotalSeconds);
            log.Info($"Disk {disk.Name}: HTTP export from {endpoint} finished: {Bytes(transferred)} in {watch.Elapsed:hh\\:mm\\:ss} ({Bytes((long)(transferred / seconds))}/s), XAPI task completed.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException && !cancellationToken.IsCancellationRequested)
        {
            var task = await TryDescribeTaskAsync().ConfigureAwait(false);
            throw new XenHttpExportException($"Disk {disk.Name}: HTTP export from {endpoint} failed after {watch.Elapsed:hh\\:mm\\:ss}: {XenHttpExport.Reason(ex)}{task}", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        response.Dispose();
        ownedTransport?.Dispose();
        await xapi.TryDestroyTaskAsync(taskRef).ConfigureAwait(false);
    }

    private async Task<long> StreamRawAsync(Stream body, DiskDataSink sink, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(RawReadSize);
        try
        {
            long offset = 0;
            while (offset < disk.CapacityBytes)
            {
                var want = (int)Math.Min(RawReadSize, disk.CapacityBytes - offset);
                var read = await body.ReadAtLeastAsync(buffer.AsMemory(0, want), want, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new InvalidDataException($"the raw export ended after {offset} of {disk.CapacityBytes} bytes (truncated export?)");
                }

                await sink(offset, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                offset += read;
            }

            if (await body.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new InvalidDataException($"the raw export is larger than the disk ({disk.CapacityBytes} bytes)");
            }

            return offset;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<string> TryDescribeTaskAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var task = await xapi.GetRecordAsync("task", taskRef, timeout.Token).ConfigureAwait(false);
            var errors = task.Set("error_info");
            return $". XAPI task status: {task.Str("status")}{(errors.Count > 0 ? $" ({string.Join(", ", errors)})" : string.Empty)}.";
        }
        catch (Exception ex) when (ex is XapiException or HttpRequestException or IOException or OperationCanceledException)
        {
            return ".";
        }
    }

    private static string Bytes(long bytes) => XenFormat.Bytes(bytes);
}

/// <summary>Which host serves an SR (for HTTP handlers that must run where the storage is attached).</summary>
internal static class XenSrHosts
{
    public sealed record SrHost(string Address, string Reason);

    /// <summary>Shared SRs are served by the pool master; local SRs by the host that has them attached.</summary>
    public static async Task<SrHost> ResolveAsync(XapiSession xapi, string srRef, CancellationToken cancellationToken)
    {
        var sr = await xapi.GetRecordAsync("SR", srRef, cancellationToken).ConfigureAwait(false);
        if (sr.Bool("shared"))
        {
            return new SrHost(xapi.MasterAddress, $"storage '{sr.Str("name_label")}' is shared, using the pool master {xapi.MasterAddress}");
        }

        foreach (var pbdRef in sr.Set("PBDs"))
        {
            var pbd = await xapi.GetRecordAsync("PBD", pbdRef, cancellationToken).ConfigureAwait(false);
            if (pbd.Bool("currently_attached"))
            {
                var host = await xapi.GetRecordAsync("host", pbd.Str("host"), cancellationToken).ConfigureAwait(false);
                return new SrHost(host.Str("address"), $"storage '{sr.Str("name_label")}' is local to host {host.Str("name_label")} ({host.Str("address")})");
            }
        }

        return new SrHost(xapi.MasterAddress, $"storage '{sr.Str("name_label")}' is not attached to any host, trying the pool master {xapi.MasterAddress}");
    }
}

internal static class XenFormat
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
