using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// Restores the items of one transfer: fetches them page by page, writes each file under a temporary name and renames
/// it when complete, applies times, attributes and permissions, and reports every file to the server. Nothing is written
/// outside the transfer's roots or through a link. Problems with one item are reported; the transfer goes on.
/// </summary>
public sealed class FileRestoreTransfer
{
    private const int PageSize = 500;
    private const int MaxPendingResults = 200;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly Guid _id;
    private readonly IRestoreFileSystem _fs;
    private readonly AgentLog _log;
    private readonly List<FileTransferResult> _pending = new List<FileTransferResult>();
    private readonly HashSet<string> _checkedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _failedFolders = new List<string>();
    private readonly Dictionary<int, byte[]> _security = new Dictionary<int, byte[]>();
    private FileTransferInfo _info = new FileTransferInfo();
    private DateTime _lastReport = DateTime.UtcNow;
    private long _bytes;
    private int _done;
    private bool _cancel;

    public FileRestoreTransfer(HttpClient http, Guid id, IRestoreFileSystem fs, AgentLog log)
    {
        _http = http;
        _id = id;
        _fs = fs;
        _log = log;
    }

    private string Base => AgentProtocol.TransfersPath + "/" + _id.ToString("D");

    /// <summary>"replace", "keep" or "fail"; servers before <see cref="FileTransferInfo.IfExists"/> send only Overwrite. Unknown: fail.</summary>
    private string IfExists => _info.IfExists switch
    {
        null => _info.Overwrite ? FileTransferIfExists.Replace : FileTransferIfExists.Keep,
        FileTransferIfExists.Replace or FileTransferIfExists.Keep => _info.IfExists,
        _ => FileTransferIfExists.Fail,
    };

    public async Task RunAsync(CancellationToken stop)
    {
        string? error = null;
        try
        {
            _info = await GetJsonAsync<FileTransferInfo>(Base, stop).ConfigureAwait(false);
            foreach (var root in _info.Roots)
            {
                if (!WindowsPath.IsValidAbsolute(root))
                {
                    throw new InvalidOperationException($"{root} is not a local folder.");
                }

                if (_fs.CheckRoot(root) is string reason)
                {
                    throw new InvalidOperationException($"Not restoring into {root}: {reason}.");
                }
            }

            _log.Write($"File restore {_id}: {_info.ItemCount} item(s) into {string.Join(", ", _info.Roots)}, existing files: {IfExists}.");
            var offset = 0;
            while (!_cancel && !stop.IsCancellationRequested)
            {
                var page = await GetJsonAsync<FileTransferItemsPage>($"{Base}/items?offset={offset}&limit={PageSize}", stop).ConfigureAwait(false);
                foreach (var pair in page.Security)
                {
                    _security[pair.Key] = Convert.FromBase64String(pair.Value);
                }

                foreach (var item in page.Items)
                {
                    if (_cancel || stop.IsCancellationRequested)
                    {
                        break;
                    }

                    if (await ProcessAsync(item, stop).ConfigureAwait(false) is FileTransferResult result)
                    {
                        _pending.Add(result);
                    }

                    _done = item.Index + 1;
                    await ReportAsync(final: false, null, stop).ConfigureAwait(false);
                }

                if (page.End || page.Items.Count == 0)
                {
                    break;
                }

                offset += page.Items.Count;
            }

            if (stop.IsCancellationRequested)
            {
                error = "the agent service is stopping";
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            error = "the agent service is stopping";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        _log.Write($"File restore {_id}: " + (error is null ? $"finished, {_done} item(s), {_bytes} bytes." : $"ended after {_done} item(s): {error}"));
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            if (await ReportAsync(final: true, error, CancellationToken.None).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
    }

    private async Task<FileTransferResult?> ProcessAsync(FileTransferItem item, CancellationToken stop)
    {
        var path = item.Path;
        try
        {
            if (!WindowsPath.IsValidAbsolute(path) || !IsInRoots(path))
            {
                return Failed(item, "the path is outside the restore folders");
            }

            if (_fs.CheckPath(path) is string refused)
            {
                if (item.Kind == FileTransferItemKinds.Directory)
                {
                    _failedFolders.Add(path);
                }

                return item.Kind == FileTransferItemKinds.EndDirectory ? null : Failed(item, refused);
            }

            if (item.Kind == FileTransferItemKinds.EndDirectory && IsFailedFolder(path))
            {
                // Already reported when the folder was not created.
                return null;
            }

            if (UnderFailedFolder(path) is string folder)
            {
                return item.Kind == FileTransferItemKinds.EndDirectory ? null : Failed(item, $"its folder {folder} could not be restored");
            }

            switch (item.Kind)
            {
                case FileTransferItemKinds.Directory:
                    EnsureParents(path);
                    switch (_fs.Probe(path))
                    {
                        case RestorePathKind.Missing:
                            _fs.CreateDirectory(path);
                            break;
                        case RestorePathKind.Directory:
                            break;
                        case RestorePathKind.File:
                            _failedFolders.Add(path);
                            return Failed(item, "a file with this name exists");
                        default:
                            _failedFolders.Add(path);
                            return Failed(item, "a link or junction with this name exists; nothing is written through it");
                    }

                    _checkedFolders.Add(path);
                    return null;

                case FileTransferItemKinds.EndDirectory:
                    var problem = ApplyMetadata(item, path, isDirectory: true);
                    return problem is null ? null : new FileTransferResult { Index = item.Index, Status = FileTransferStatus.Incomplete, Message = problem };

                case FileTransferItemKinds.File:
                    return await RestoreFileAsync(item, stop).ConfigureAwait(false);

                default:
                    return Failed(item, $"unknown item kind '{item.Kind}' (agent older than the server?)");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (!(ex is HttpRequestException))
        {
            if (item.Kind == FileTransferItemKinds.Directory)
            {
                _failedFolders.Add(path);
            }

            return Failed(item, Describe(ex));
        }
    }

    private async Task<FileTransferResult> RestoreFileAsync(FileTransferItem item, CancellationToken stop)
    {
        EnsureParents(item.Path);
        var target = item.Path;
        var replace = false;
        switch (_fs.Probe(target))
        {
            case RestorePathKind.Missing:
                break;
            case RestorePathKind.File when IfExists == FileTransferIfExists.Replace:
                replace = true;
                break;
            case RestorePathKind.File when IfExists == FileTransferIfExists.Keep:
                target = FreeName(item.Path) ?? throw new IOException($"no free {_info.KeepPrefix} name was found next to it");
                break;
            case RestorePathKind.File:
                return Failed(item, "a file with this name exists; it is kept");
            case RestorePathKind.Directory:
                return Failed(item, "a folder with this name exists");
            default:
                return Failed(item, "a link with this name exists; it is not replaced");
        }

        string hash;
        string temporary;
        using (var response = await _http.GetAsync($"{Base}/items/{item.Index}/content", HttpCompletionOption.ResponseHeadersRead, stop).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return Failed(item, $"the server could not read it from the backup ({(int)response.StatusCode}): {ProblemText(text)}");
            }

            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                long written = 0;
                using (var output = _fs.CreateTemporary(target, out temporary))
                {
                    try
                    {
                        var buffer = new byte[1 << 20];
                        int n;
                        while ((n = await input.ReadAsync(buffer, 0, buffer.Length, stop).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, n, stop).ConfigureAwait(false);
                            sha.AppendData(buffer, 0, n);
                            written += n;
                            _bytes += n;
                        }
                    }
                    catch
                    {
                        output.Dispose();
                        _fs.DeleteTemporary(temporary);
                        throw;
                    }
                }

                if (written != item.Size)
                {
                    _fs.DeleteTemporary(temporary);
                    return Failed(item, $"{written} bytes arrived instead of {item.Size}; the file was not changed");
                }

                hash = ToHex(sha.GetHashAndReset());
            }
        }

        try
        {
            _fs.Commit(temporary, target, replace);
        }
        catch
        {
            _fs.DeleteTemporary(temporary);
            throw;
        }

        var problem = ApplyMetadata(item, target, isDirectory: false);
        var renamed = !string.Equals(target, item.Path, StringComparison.Ordinal);
        return new FileTransferResult
        {
            Index = item.Index,
            Status = problem is not null ? FileTransferStatus.Incomplete : renamed ? FileTransferStatus.Renamed : FileTransferStatus.Ok,
            Message = problem,
            FinalPath = renamed ? target : null,
            Sha256 = hash,
        };
    }

    /// <summary>Times and attributes, then permissions (last: they may deny further changes). Returns a problem or null.</summary>
    private string? ApplyMetadata(FileTransferItem item, string path, bool isDirectory)
    {
        var problems = new List<string>();
        try
        {
            _fs.SetTimesAndAttributes(path, isDirectory, item.CreatedUtc, item.ModifiedUtc, item.AccessedUtc, (FileAttributes)item.Attributes);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is PlatformNotSupportedException)
        {
            problems.Add("times and attributes not set: " + Describe(ex));
        }

        if (_info.RestorePermissions && item.Security >= 0)
        {
            try
            {
                if (!_security.TryGetValue(item.Security, out var descriptor))
                {
                    throw new InvalidDataException($"the server sent no security descriptor {item.Security}");
                }

                _fs.SetSecurity(path, isDirectory, descriptor);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                problems.Add("permissions not restored: " + Describe(ex));
            }
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    /// <summary>Creates missing parent folders; a file or link on the way is an error (nothing is written through links).</summary>
    private void EnsureParents(string path)
    {
        var root = RootOf(path);
        var chain = new List<string>();
        for (var folder = WindowsPath.Parent(path); folder.Length >= root.TrimEnd('\\').Length && !_checkedFolders.Contains(folder); folder = WindowsPath.Parent(folder))
        {
            chain.Add(folder);
            if (folder.Length <= 3)
            {
                break;
            }
        }

        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var folder = chain[i];
            switch (_fs.Probe(folder))
            {
                case RestorePathKind.Directory:
                    break;
                case RestorePathKind.Missing when WindowsPath.IsBelow(folder, root) && folder.TrimEnd('\\').Length > 2:
                    // The restore folder itself and everything below it; never a drive root.
                    _fs.CreateDirectory(folder);
                    break;
                case RestorePathKind.Missing:
                    throw new DirectoryNotFoundException($"{folder} does not exist");
                case RestorePathKind.File:
                    throw new IOException($"{folder} is a file, not a folder");
                default:
                    throw new IOException($"{folder} is a link or junction; nothing is written through it");
            }

            _checkedFolders.Add(folder);
        }
    }

    private string? FreeName(string path)
    {
        var folder = WindowsPath.Parent(path);
        var name = _info.KeepPrefix + WindowsPath.Name(path);
        var dot = name.LastIndexOf('.');
        for (var n = 1; n < 1000; n++)
        {
            var candidate = n == 1 ? name : dot > 0 ? $"{name.Substring(0, dot)} ({n}){name.Substring(dot)}" : $"{name} ({n})";
            var full = WindowsPath.Join(folder, candidate);
            if (_fs.Probe(full) == RestorePathKind.Missing)
            {
                return full;
            }
        }

        return null;
    }

    private bool IsInRoots(string path)
    {
        foreach (var root in _info.Roots)
        {
            if (WindowsPath.IsBelow(path, root))
            {
                return true;
            }
        }

        return false;
    }

    private string RootOf(string path)
    {
        var best = string.Empty;
        foreach (var root in _info.Roots)
        {
            if (WindowsPath.IsBelow(path, root) && root.Length > best.Length)
            {
                best = root;
            }
        }

        return best;
    }

    private bool IsFailedFolder(string path)
    {
        foreach (var folder in _failedFolders)
        {
            if (string.Equals(path.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string? UnderFailedFolder(string path)
    {
        foreach (var folder in _failedFolders)
        {
            if (WindowsPath.IsBelow(path, folder) && !string.Equals(path.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return folder;
            }
        }

        return null;
    }

    /// <summary>Sends the results so far (every few seconds, and at the end). Returns false if the server was not reached.</summary>
    private async Task<bool> ReportAsync(bool final, string? error, CancellationToken stop)
    {
        if (!final && _pending.Count < MaxPendingResults && DateTime.UtcNow - _lastReport < ReportInterval)
        {
            return true;
        }

        var progress = new FileTransferProgress { Done = _done, Bytes = _bytes, Results = new List<FileTransferResult>(_pending), Finished = final, Error = error };
        try
        {
            using (var content = new StringContent(JsonSerializer.Serialize(progress, AgentJson.Options), Encoding.UTF8, "application/json"))
            using (var response = await _http.PostAsync($"{Base}/progress", content, stop).ConfigureAwait(false))
            {
                _lastReport = DateTime.UtcNow;
                if (!response.IsSuccessStatusCode)
                {
                    // 404: the server forgot the transfer (restart, or it gave up): stop.
                    _cancel = true;
                    return final;
                }

                _pending.Clear();
                var answer = JsonSerializer.Deserialize<FileTransferProgressResponse>(await response.Content.ReadAsStringAsync().ConfigureAwait(false), AgentJson.Options);
                if (answer?.Cancel == true && !_cancel)
                {
                    _cancel = true;
                    _log.Write($"File restore {_id}: stopped on the server.");
                }

                return true;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
        {
            // Kept and sent with the next report.
            _lastReport = DateTime.UtcNow;
            return false;
        }
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken stop)
        where T : class
    {
        using (var response = await _http.GetAsync(url, stop).ConfigureAwait(false))
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"{_http.BaseAddress}{url} answered {(int)response.StatusCode}: {ProblemText(text)}");
            }

            return JsonSerializer.Deserialize<T>(text, AgentJson.Options) ?? throw new InvalidDataException($"{url}: empty answer.");
        }
    }

    private static FileTransferResult Failed(FileTransferItem item, string message) =>
        new FileTransferResult { Index = item.Index, Status = FileTransferStatus.Failed, Message = message };

    /// <summary>The detail of an RFC 7807 problem, or the text itself.</summary>
    private static string ProblemText(string text)
    {
        try
        {
            using (var document = JsonDocument.Parse(text))
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                {
                    return detail.GetString() ?? text;
                }
            }
        }
        catch (JsonException)
        {
        }

        return text.Length > 300 ? text.Substring(0, 300) : text;
    }

    private static string Describe(Exception ex) => ex is UnauthorizedAccessException ? "access denied (" + ex.Message.TrimEnd('.') + ")" : ex.Message.TrimEnd('.');

    private static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
