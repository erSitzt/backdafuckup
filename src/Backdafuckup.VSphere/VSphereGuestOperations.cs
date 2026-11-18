using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Tls;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere;

/// <summary>
/// VMware Tools guest operations (vim25 GuestOperationsManager): files are copied with InitiateFileTransferToGuest and
/// an HTTPS PUT to the VM's ESXi host, whose certificate is pinned from <c>HostSystem.config.certificate</c> as vCenter
/// reports it; programs run with StartProgramInGuest and are watched with ListProcessesInGuest.
/// </summary>
internal sealed class VSphereGuestOperations(VSphereHypervisorSession session) : IGuestOperations
{
    private readonly ConcurrentDictionary<string, VSphereVm> _vms = new(StringComparer.Ordinal);
    private (ManagedObjectReference File, ManagedObjectReference Process)? _managers;

    private VimSession Vim => session.Vim;

    public async Task<string?> CheckGuestOperationsAsync(string vmId, CancellationToken cancellationToken)
    {
        var vm = await VmAsync(vmId, cancellationToken).ConfigureAwait(false);
        if (Vim.Content.GuestOperationsManager is null)
        {
            return $"vCenter {Vim.Endpoint} offers no guest operations.";
        }

        var row = (await Vim.RetrievePropertiesAsync([vm.Ref], ["runtime.powerState", "guest.toolsRunningStatus", "guest.guestOperationsReady"], cancellationToken).ConfigureAwait(false))
            .FirstOrDefault();
        if (row?.Str("runtime.powerState") != "poweredOn")
        {
            return $"VM {vm.Name} is not running ({row?.Str("runtime.powerState") ?? "power state unknown"}).";
        }

        if (row.Str("guest.toolsRunningStatus") != "guestToolsRunning")
        {
            return $"VMware Tools are not running in VM {vm.Name} ({row.Str("guest.toolsRunningStatus") ?? "status unknown"}).";
        }

        return row.Str("guest.guestOperationsReady") == "true"
            ? null
            : $"VMware Tools in VM {vm.Name} do not accept guest operations yet (guestOperationsReady is false: the guest is still starting, or the Tools are too old).";
    }

    public async Task CreateDirectoryAsync(string vmId, GuestLogin login, string path, CancellationToken cancellationToken)
    {
        var vm = await VmAsync(vmId, cancellationToken).ConfigureAwait(false);
        var (file, _) = await ManagersAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Vim.InvokeAsync("MakeDirectoryInGuest", file, cancellationToken,
                VimXml.Ref("vm", vm.Ref), Auth(login), VimXml.E("directoryPath", path), VimXml.E("createParentDirectories", "true")).ConfigureAwait(false);
        }
        catch (VimFaultException ex) when (ex.FaultType == "FileAlreadyExists")
        {
        }
        catch (VimFaultException ex)
        {
            throw Explain(ex, vm, login, $"create directory {path}");
        }
    }

    public async Task UploadFileAsync(string vmId, GuestLogin login, string path, Stream content, long length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var vm = await VmAsync(vmId, cancellationToken).ConfigureAwait(false);
        var (file, _) = await ManagersAsync(cancellationToken).ConfigureAwait(false);
        VimObject? url;
        try
        {
            url = await Vim.InvokeAsync("InitiateFileTransferToGuest", file, cancellationToken,
                VimXml.Ref("vm", vm.Ref), Auth(login), VimXml.E("guestFilePath", path), VimXml.Typed("fileAttributes", "GuestWindowsFileAttributes"),
                VimXml.E("fileSize", length.ToString(CultureInfo.InvariantCulture)), VimXml.E("overwrite", "true")).ConfigureAwait(false);
        }
        catch (VimFaultException ex)
        {
            throw Explain(ex, vm, login, $"write {path}");
        }

        var (hostName, pin) = await HostAsync(vm, cancellationToken).ConfigureAwait(false);
        var target = new Uri((url?.Value ?? throw new InvalidDataException($"vCenter {Vim.Endpoint}: InitiateFileTransferToGuest for {path} in VM {vm.Name} returned no URL."))
            .Replace("://*", "://" + hostName, StringComparison.Ordinal));
        using var http = new HttpClient(session.Provider.CreateHostHandler(session.Settings, pin), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        using var body = new StreamContent(content);
        body.Headers.ContentLength = length;
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        HttpResponseMessage response;
        try
        {
            response = await http.PutAsync(target, body, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"Copying {path} into VM {vm.Name} through ESXi host {target.Host}:{target.Port} failed: {ex.Message} " +
                "(the backup server needs HTTPS access to the VM's ESXi host for guest file transfers).", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new IOException($"ESXi host {target.Host}:{target.Port} refused the copy of {path} into VM {vm.Name}: HTTP {(int)response.StatusCode} {text.Trim()}");
            }
        }
    }

    public async Task<long> StartProgramAsync(string vmId, GuestLogin login, string programPath, string arguments, CancellationToken cancellationToken)
    {
        var vm = await VmAsync(vmId, cancellationToken).ConfigureAwait(false);
        var (_, process) = await ManagersAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pid = await Vim.InvokeAsync("StartProgramInGuest", process, cancellationToken,
                VimXml.Ref("vm", vm.Ref), Auth(login), VimXml.Typed("spec", "GuestProgramSpec", VimXml.E("programPath", programPath), VimXml.E("arguments", arguments))).ConfigureAwait(false);
            return long.Parse(pid?.Value ?? throw new InvalidDataException($"vCenter {Vim.Endpoint}: StartProgramInGuest returned no process id."), CultureInfo.InvariantCulture);
        }
        catch (VimFaultException ex)
        {
            throw Explain(ex, vm, login, $"start {programPath}");
        }
    }

    public async Task<int?> GetExitCodeAsync(string vmId, GuestLogin login, long processId, CancellationToken cancellationToken)
    {
        var vm = await VmAsync(vmId, cancellationToken).ConfigureAwait(false);
        var (_, process) = await ManagersAsync(cancellationToken).ConfigureAwait(false);
        VimObject? result;
        try
        {
            result = await Vim.InvokeAsync("ListProcessesInGuest", process, cancellationToken,
                VimXml.Ref("vm", vm.Ref), Auth(login), VimXml.E("pids", processId.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
        }
        catch (VimFaultException ex)
        {
            throw Explain(ex, vm, login, $"read the state of process {processId}");
        }

        // One pid asked for: the first (only) returnval is its GuestProcessInfo.
        var info = result is not null && result.Long("pid") == processId
            ? result
            : throw new InvalidOperationException($"Process {processId} in VM {vm.Name} is unknown to VMware Tools.");
        return info.Str("endTime") is null ? null : info.Int("exitCode") ?? 0;
    }

    /// <summary><c>.\user</c> means a local account; VMware Tools expect the bare name for that.</summary>
    internal static XElement Auth(GuestLogin login) => VimXml.Typed("auth", "NamePasswordAuthentication",
        VimXml.E("interactiveSession", "false"),
        VimXml.E("username", login.UserName.StartsWith(@".\", StringComparison.Ordinal) ? login.UserName[2..] : login.UserName),
        VimXml.E("password", login.Password));

    private Exception Explain(VimFaultException ex, VSphereVm vm, GuestLogin login, string what) => ex.FaultType switch
    {
        "InvalidGuestLogin" => new UnauthorizedAccessException($"Logon as {login.UserName} in VM {vm.Name} through VMware Tools failed: wrong user name or password.", ex),
        "GuestPermissionDenied" or "NoPermission" => new UnauthorizedAccessException(
            $"Guest operations in VM {vm.Name}: {what} was refused for {login.UserName} ({ex.FaultMessage}). The vCenter account needs the privileges " +
            "Virtual machine > Guest operations, and the guest account write access there.", ex),
        "GuestOperationsUnavailable" or "GuestComponentsOutOfDate" or "OperationNotSupportedByGuest" => new InvalidOperationException(
            $"Guest operations in VM {vm.Name}: {what} is not possible: {ex.FaultMessage} ({ex.FaultType}).", ex),
        _ => new InvalidOperationException($"Guest operations in VM {vm.Name}: {what} failed: {ex.FaultMessage} ({ex.FaultType}).", ex),
    };

    private async Task<VSphereVm> VmAsync(string vmId, CancellationToken cancellationToken)
    {
        if (_vms.TryGetValue(vmId, out var cached))
        {
            return cached;
        }

        var vm = await VSphereInventory.FindVmAsync(Vim, vmId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"vCenter {Vim.Endpoint} has no VM with instance UUID {vmId}.");
        return _vms[vmId] = vm;
    }

    private async Task<(ManagedObjectReference File, ManagedObjectReference Process)> ManagersAsync(CancellationToken cancellationToken)
    {
        if (_managers is { } known)
        {
            return known;
        }

        var manager = Vim.Content.GuestOperationsManager ?? throw new InvalidOperationException($"vCenter {Vim.Endpoint} offers no guest operations.");
        var row = (await Vim.RetrievePropertiesAsync([manager], ["fileManager", "processManager"], cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        var file = row?["fileManager"]?.AsRef() ?? throw new InvalidDataException($"vCenter {Vim.Endpoint}: GuestOperationsManager without fileManager.");
        var process = row["processManager"]?.AsRef() ?? throw new InvalidDataException($"vCenter {Vim.Endpoint}: GuestOperationsManager without processManager.");
        _managers = (file, process);
        return (file, process);
    }

    /// <summary>The VM's ESXi host: its name in vCenter and the SPKI pin of the certificate vCenter reports for it.</summary>
    private async Task<(string Name, string Pin)> HostAsync(VSphereVm vm, CancellationToken cancellationToken)
    {
        var host = (await Vim.RetrievePropertiesAsync([vm.Ref], ["runtime.host"], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?["runtime.host"]?.AsRef()
            ?? throw new InvalidOperationException($"VM {vm.Name} runs on no ESXi host.");
        var row = (await Vim.RetrievePropertiesAsync([host], ["name", "config.certificate"], cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        var name = row?.Str("name") ?? throw new InvalidDataException($"vCenter {Vim.Endpoint}: host {host.Value} has no name.");
        var pem = row["config.certificate"]?.Bytes() ?? throw new InvalidOperationException($"vCenter {Vim.Endpoint} reports no certificate for ESXi host {name}, so a guest file transfer cannot be checked.");
        using var certificate = X509Certificate2.CreateFromPem(System.Text.Encoding.ASCII.GetString(pem));
        return (name, CertificatePinning.SpkiSha256(certificate));
    }
}
