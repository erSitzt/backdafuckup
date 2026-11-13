using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Tls;
using Microsoft.Win32.SafeHandles;

namespace Backdafuckup.Mover;

/// <summary>
/// Moves one volume between its pod and the backup server (<see cref="MoverStream"/>). Backup: reads the device from
/// start to end and sends its non-zero blocks. Restore: writes the regions the server sends to a new volume (zero
/// regions are left alone: a new volume reads zeros there), flushes the device and reports the result.
/// </summary>
internal static partial class MoverRunner
{
    private const int ReadBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(30);

    /// <summary>Returns the exit code: 0 done, 1 failed (reported to the server when it can be reached).</summary>
    public static async Task<int> RunAsync(MoverOptions options, HttpMessageHandler? handler, TextWriter log, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(handler ?? CreateHandler(options), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        try
        {
            switch (options.Mode)
            {
                case MoverMode.Backup:
                    await BackupAsync(http, options, log, cancellationToken).ConfigureAwait(false);
                    break;
                case MoverMode.Files:
                    await FilesAsync(http, options, log, cancellationToken).ConfigureAwait(false);
                    break;
                case MoverMode.Tree:
                    await TreeAsync(http, options, log, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await RestoreAsync(http, options, log, cancellationToken).ConfigureAwait(false);
                    break;
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.WriteLine("Stopped.");
            return 1;
        }
        catch (Exception ex)
        {
            log.WriteLine($"Failed: {ex.Message}");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await ReportAsync(http, options, new MoverResult(0, ex.Message), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception report) when (report is HttpRequestException or OperationCanceledException or IOException)
            {
                log.WriteLine($"The error could not be reported to {options.ResultUrl.Authority}: {report.Message}");
            }

            return 1;
        }
    }

    /// <summary>TLS to the server: exactly its pinned keys, or the system's CA store when there are no pins.</summary>
    internal static SocketsHttpHandler CreateHandler(MoverOptions options) => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(30),
        SslOptions = options.ServerPins.Count > 0
            ? new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = CertificatePinning.CreateValidator(TlsTrustMode.Pinned, options.ServerPins) }
            : new SslClientAuthenticationOptions(),
    };

    private static async Task BackupAsync(HttpClient http, MoverOptions options, TextWriter log, CancellationToken cancellationToken)
    {
        using var device = OpenDevice(options.Device, FileAccess.Read);
        var length = DeviceLength(device, options.Device);
        var capacity = options.Bytes ?? length;
        if (capacity > length)
        {
            throw new IOException($"The device {options.Device} has {length} bytes, fewer than the {capacity} bytes of the volume to send.");
        }

        log.WriteLine($"Backup of {options.Device} ({Size(capacity)}) to {options.DataUrl.Authority}.");
        var started = Stopwatch.GetTimestamp();
        var content = new VolumeContent(device, options.Device, capacity, options.BlockBytes, log);
        using var request = new HttpRequestMessage(HttpMethod.Put, options.DataUrl) { Content = content };
        using var response = await SendAsync(http, request, options.DataUrl, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The backup server {options.DataUrl.Authority} refused the volume: {(int)response.StatusCode} {Detail(body)}");
        }

        log.WriteLine($"Done: {Size(capacity)} read, {Size(content.DataBytes)} of data sent in {Stopwatch.GetElapsedTime(started):hh\\:mm\\:ss}.");
    }

    private static async Task RestoreAsync(HttpClient http, MoverOptions options, TextWriter log, CancellationToken cancellationToken)
    {
        var device = OpenDevice(options.Device, FileAccess.ReadWrite);
        try
        {
            var length = DeviceLength(device, options.Device);
            using var response = await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, options.DataUrl), options.DataUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException($"The backup server {options.DataUrl.Authority} refused the image: {(int)response.StatusCode} {Detail(body)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var capacity = await MoverStream.ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
            if (length < capacity)
            {
                throw new IOException($"The device {options.Device} has {length} bytes, the image {capacity}: the new volume is too small.");
            }

            log.WriteLine($"Restore of {Size(capacity)} from {options.DataUrl.Authority} to {options.Device} ({Size(length)}).");
            var started = Stopwatch.GetTimestamp();
            var progress = new Progress(log, capacity, "written");
            var total = await MoverStream.ReadRecordsAsync(
                stream,
                capacity,
                async (offset, data, ct) =>
                {
                    await RandomAccess.WriteAsync(device, data, offset, ct).ConfigureAwait(false);
                    progress.Report(offset + data.Length, data.Length);
                },
                cancellationToken).ConfigureAwait(false);

            // The data has to be on the volume before the server binds it to the workload.
            using (var flush = new FileStream(device, FileAccess.ReadWrite, bufferSize: 0))
            {
                flush.Flush(flushToDisk: true);
            }

            await ReportAsync(http, options, new MoverResult(total), cancellationToken).ConfigureAwait(false);
            log.WriteLine($"Done: {Size(total)} of data written and flushed in {Stopwatch.GetElapsedTime(started):hh\\:mm\\:ss}.");
        }
        finally
        {
            device.Dispose();
        }
    }

    private static async Task FilesAsync(HttpClient http, MoverOptions options, TextWriter log, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, options.DataUrl), options.DataUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"The backup server {options.DataUrl.Authority} refused the files: {(int)response.StatusCode} {Detail(body)}");
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The mover puts files into volumes on Linux only.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        log.WriteLine($"Restoring files from {options.DataUrl.Authority} into {options.Root}: existing files {(options.Keep ? "stay (RESTORED- copies)" : "are replaced")}, owners {(options.Owners ? "from the backup" : "of the folders they go into")}.");
        var started = Stopwatch.GetTimestamp();
        var report = await new VolumeFiles(options.Root, options.Keep, options.Owners, log).ApplyAsync(stream, cancellationToken).ConfigureAwait(false);
        await ReportAsync(http, options, new MoverResult(report.Bytes, null, report), cancellationToken).ConfigureAwait(false);
        log.WriteLine($"Done in {Stopwatch.GetElapsedTime(started):hh\\:mm\\:ss}: {report.Files} file(s), {Size(report.Bytes)}; {report.Renamed} as RESTORED- copies, {report.Failed} failed, {report.Partial} without all of owner, mode or times.");
    }

    /// <summary>
    /// Reads the mounted volume file by file (<see cref="VolumeTree"/>, on a thread of its own) and sends the tree as the
    /// request body: the walk waits while the server is behind (the pipe pauses it), a heartbeat keeps the request alive
    /// while a slow file is read.
    /// </summary>
    private static async Task TreeAsync(HttpClient http, MoverOptions options, TextWriter log, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The mover reads volumes file by file on Linux only.");
        }

        log.WriteLine($"Reading {options.Root} file by file to {options.DataUrl.Authority}{(options.PreviousUrl is null ? string.Empty : ", files unchanged since the previous run not again")}.");
        var started = Stopwatch.GetTimestamp();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // The previous run's tree streams in alongside the walk, which follows it (on its own connection).
        using var previous = options.PreviousUrl is { } previousUrl ? await PreviousAsync(http, previousUrl, cancellationToken).ConfigureAwait(false) : null;
        await using var previousStream = previous is null ? null : await previous.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var pipe = new System.IO.Pipelines.Pipe(new System.IO.Pipelines.PipeOptions(pauseWriterThreshold: 8 * 1024 * 1024, resumeWriterThreshold: 4 * 1024 * 1024));
        var walk = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    using var output = pipe.Writer.AsStream();
                    var summary = VolumeTree.Read(options.Root, output, HeartbeatInterval, stop.Token, previousStream, options.Racy);
                    pipe.Writer.Complete();
                    return summary;
                }
                catch (Exception ex)
                {
                    pipe.Writer.Complete(ex);
                    throw;
                }
            },
            stop.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        using var content = new StreamContent(pipe.Reader.AsStream());
        using var request = new HttpRequestMessage(HttpMethod.Put, options.DataUrl) { Content = content };
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(http, request, options.DataUrl, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await ThrowIfWalkFailedAsync(walk, options.Root).ConfigureAwait(false);
            throw;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await stop.CancelAsync().ConfigureAwait(false);
                await ThrowIfWalkFailedAsync(walk, options.Root).ConfigureAwait(false);
                throw new HttpRequestException($"The backup server {options.DataUrl.Authority} refused the files: {(int)response.StatusCode} {Detail(body)}");
            }
        }

        var done = await walk.ConfigureAwait(false);
        log.WriteLine($"Done in {Stopwatch.GetElapsedTime(started):hh\\:mm\\:ss}: {done.Entries} entries, {Size(done.Bytes)} of file content read, " +
                      $"{done.Unchanged} file(s) unchanged since the previous run, {done.Issues} not read" +
                      (done.AsOwner > 0 ? $", {done.AsOwner} read as their owner (root was refused: root_squash)." : "."));
    }

    /// <summary>
    /// When the walk failed (the volume cannot be read, the previous tree broke off), the upload broke because of it: its
    /// error is the one to report, not the upload's (a stream that ended early, a refused request).
    /// </summary>
    private static async Task ThrowIfWalkFailedAsync(Task walk, string root)
    {
        await Task.WhenAny(walk, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
        if (walk is { IsFaulted: true, Exception.InnerException: { } cause } && cause is not OperationCanceledException)
        {
            throw new IOException($"Reading {root} failed: {cause.Message}", cause);
        }
    }

    /// <summary>Starts fetching the previous run's tree; the walk reads it as it goes.</summary>
    private static async Task<HttpResponseMessage> PreviousAsync(HttpClient http, Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await SendAsync(http, request, url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new HttpRequestException($"The backup server {url.Authority} did not send the previous run's tree: {(int)response.StatusCode} {Detail(body)}");
        }

        return response;
    }

    private static async Task ReportAsync(HttpClient http, MoverOptions options, MoverResult result, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(result, MoverJson.Default.MoverResult);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.ResultUrl) { Content = content };
        using var response = await SendAsync(http, request, options.ResultUrl, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"The backup server {options.ResultUrl.Authority} did not take the result: {(int)response.StatusCode} {Detail(body)}");
        }
    }

    /// <summary>Sends a request; connection and TLS errors name the server.</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, Uri url, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException || ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            throw new HttpRequestException(
                $"The certificate of the backup server {url.Authority} is not trusted (pinned keys or the system's CA store; with a certificate of your own CA set Agent:ServerPins on the server): {ex.Message}",
                ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            throw new HttpRequestException($"The backup server {url.Authority} cannot be reached from the pod: {ex.Message}", ex);
        }
    }

    private static SafeFileHandle OpenDevice(string path, FileAccess access)
    {
        try
        {
            return File.OpenHandle(path, FileMode.Open, access, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"The volume's device {path} cannot be opened for {(access == FileAccess.Read ? "reading" : "writing")}: {ex.Message}", ex);
        }
    }

    /// <summary>The size of a block device (where fstat reports 0) or a file: the end found by lseek.</summary>
    private static long DeviceLength(SafeFileHandle device, string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return RandomAccess.GetLength(device);
        }

        var end = LSeek(device, 0, 2);
        return end >= 0 ? end : throw new IOException($"The size of the device {path} cannot be found (lseek: error {Marshal.GetLastPInvokeError()}).");
    }

    [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long LSeek(SafeFileHandle fd, long offset, int whence);

    private static async Task<int> ReadExactlyAsync(SafeFileHandle device, string path, Memory<byte> buffer, long offset, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await RandomAccess.ReadAsync(device, buffer[read..], offset + read, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException($"The device {path} ended at {offset + read} bytes, before its size.");
            }

            read += n;
        }

        return read;
    }

    private static string Detail(string body)
    {
        try
        {
            using var problem = JsonDocument.Parse(body);
            if (problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (JsonException)
        {
            // Not a problem document.
        }

        return body.Length > 500 ? body[..500] : body;
    }

    internal static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.00} GiB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
    };

    /// <summary>The request body of a backup: the device read in large pieces (two in turn: one read ahead) and sent block-wise without zero blocks.</summary>
    private sealed class VolumeContent(SafeFileHandle device, string path, long capacity, int blockBytes, TextWriter log) : HttpContent
    {
        public long DataBytes { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await MoverStream.WriteHeaderAsync(stream, capacity, cancellationToken).ConfigureAwait(false);
            var buffers = new[] { GC.AllocateUninitializedArray<byte>(ReadBytes), GC.AllocateUninitializedArray<byte>(ReadBytes) };
            var progress = new Progress(log, capacity, "read");
            var lastSent = Stopwatch.GetTimestamp();
            long offset = 0;
            var current = 0;
            var pending = capacity > 0 ? ReadAsync(buffers[0], 0) : null;
            while (pending is not null)
            {
                var length = await pending.ConfigureAwait(false);
                var buffer = buffers[current];
                var at = offset;
                offset += length;
                current ^= 1;
                pending = offset < capacity ? ReadAsync(buffers[current], offset) : null;

                // Runs of non-zero blocks become records; a run ends at a zero block or at the end of the piece.
                var runStart = -1;
                for (var position = 0; position < length; position += blockBytes)
                {
                    var zero = buffer.AsSpan(position, Math.Min(blockBytes, length - position)).IndexOfAnyExcept((byte)0) < 0;
                    if (!zero && runStart < 0)
                    {
                        runStart = position;
                    }
                    else if (zero && runStart >= 0)
                    {
                        await SendAsync(stream, at + runStart, buffer.AsMemory(runStart, position - runStart), cancellationToken).ConfigureAwait(false);
                        lastSent = Stopwatch.GetTimestamp();
                        runStart = -1;
                    }
                }

                if (runStart >= 0)
                {
                    await SendAsync(stream, at + runStart, buffer.AsMemory(runStart, length - runStart), cancellationToken).ConfigureAwait(false);
                    lastSent = Stopwatch.GetTimestamp();
                }

                if (Stopwatch.GetElapsedTime(lastSent) >= HeartbeatInterval)
                {
                    await MoverStream.WriteHeartbeatAsync(stream, cancellationToken).ConfigureAwait(false);
                    lastSent = Stopwatch.GetTimestamp();
                }

                progress.Report(offset, 0, DataBytes);
            }

            await MoverStream.WriteEndAsync(stream, DataBytes, cancellationToken).ConfigureAwait(false);

            Task<int> ReadAsync(byte[] into, long from) =>
                ReadExactlyAsync(device, path, into.AsMemory(0, (int)Math.Min(ReadBytes, capacity - from)), from, cancellationToken);
        }

        private async ValueTask SendAsync(Stream stream, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            await MoverStream.WriteRecordAsync(stream, offset, data, cancellationToken).ConfigureAwait(false);
            DataBytes += data.Length;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A line every <see cref="ProgressInterval"/>: position, share and data bytes.</summary>
    private sealed class Progress(TextWriter log, long capacity, string verb)
    {
        private long _last = Stopwatch.GetTimestamp();
        private long _data;

        public void Report(long position, long data, long? totalData = null)
        {
            _data = totalData ?? _data + data;
            if (Stopwatch.GetElapsedTime(_last) < ProgressInterval)
            {
                return;
            }

            _last = Stopwatch.GetTimestamp();
            var share = capacity == 0 ? 100 : position * 100.0 / capacity;
            log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Size(position)} of {Size(capacity)} {verb} ({share:0}%), {Size(_data)} of data."));
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MoverResult))]
internal sealed partial class MoverJson : JsonSerializerContext;
