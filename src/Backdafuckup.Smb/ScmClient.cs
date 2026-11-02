using System.Globalization;
using SMBLibrary;
using SMBLibrary.Client;
using SMBLibrary.RPC;

namespace Backdafuckup.Smb;

/// <summary>SERVICE_STATUS (MS-SCMR 2.2.47).</summary>
public sealed record ServiceStatus(uint ServiceType, uint CurrentState, uint ControlsAccepted, uint Win32ExitCode, uint ServiceSpecificExitCode, uint CheckPoint, uint WaitHint)
{
    public const uint Stopped = 1;
    public const uint StartPending = 2;
    public const uint StopPending = 3;
    public const uint Running = 4;

    public string StateName => CurrentState switch
    {
        Stopped => "stopped",
        StartPending => "starting",
        StopPending => "stopping",
        Running => "running",
        _ => $"state {CurrentState}",
    };
}

/// <summary>A failed SCM call with its Win32 error code.</summary>
public sealed class ScmException(string operation, uint error)
    : Exception($"{operation} failed: {Describe(error)}")
{
    public const uint AccessDenied = 5;
    public const uint ServiceDoesNotExist = 1060;
    public const uint ServiceAlreadyRunning = 1056;
    public const uint ServiceNotActive = 1062;
    public const uint ServiceMarkedForDelete = 1072;
    public const uint ServiceExists = 1073;

    public uint Error { get; } = error;

    public static string Describe(uint error) => error switch
    {
        AccessDenied => "access denied (5); the account needs administrator rights on the target",
        ServiceDoesNotExist => "the service does not exist (1060)",
        ServiceAlreadyRunning => "the service is already running (1056)",
        ServiceNotActive => "the service is not running (1062)",
        ServiceMarkedForDelete => "the service is marked for deletion (1072); a reboot of the guest may be needed",
        ServiceExists => "the service already exists (1073)",
        1053 => "the service did not respond to the start request in time (1053)",
        _ => $"Win32 error {error.ToString(CultureInfo.InvariantCulture)}",
    };
}

/// <summary>
/// Service Control Manager Remote Protocol (MS-SCMR) over the named pipe <c>\pipe\svcctl</c> of the IPC$ share:
/// open the SCM, create, start, query, stop and delete services. Only the calls the push installation needs.
/// </summary>
public sealed class ScmClient : IDisposable
{
    public const uint ScManagerAllAccess = 0xF003F;
    public const uint ServiceAllAccess = 0xF01FF;
    public const uint ServiceWin32OwnProcess = 0x10;
    public const uint ServiceAutoStart = 2;
    public const uint ServiceErrorNormal = 1;
    public const uint ServiceControlStop = 1;

    private const string PipeName = "svcctl";
    private static readonly Guid InterfaceId = new("367ABB81-9844-35F1-AD32-98F038001003");
    private const uint InterfaceVersion = 2;
    private const uint FsctlPipeTransceive = 0x0011C017;

    private readonly INTFileStore _ipc;
    private readonly object _pipe;
    private readonly int _maxFragment;
    private uint _callId;

    private ScmClient(INTFileStore ipc, object pipe, int maxFragment)
    {
        _ipc = ipc;
        _pipe = pipe;
        _maxFragment = maxFragment;
    }

    public static ScmClient Open(INTFileStore ipc, string target)
    {
        ArgumentNullException.ThrowIfNull(ipc);
        var status = NamedPipeHelper.BindPipe(ipc, PipeName, InterfaceId, InterfaceVersion, out var pipe, out var maxFragment);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new IOException($"The service control manager of {target} (\\pipe\\svcctl over SMB 445) is not reachable: {status}.");
        }

        return new ScmClient(ipc, pipe, maxFragment);
    }

    // ROpenSCManagerW (opnum 15)
    public ScmHandle OpenManager(uint access)
    {
        var request = new NdrWriter().UniqueString(null).UniqueString("ServicesActive").UInt32(access).ToArray();
        var response = new NdrReader(Call(15, request));
        var handle = response.Handle();
        Check("Opening the service control manager", response.UInt32());
        return handle;
    }

    // RCreateServiceW (opnum 12); account null = Local System.
    public ScmHandle Create(ScmHandle manager, string name, string displayName, string binaryPath, uint startType)
    {
        var request = new NdrWriter()
            .Handle(manager.Value)
            .String(name)
            .UniqueString(displayName)
            .UInt32(ServiceAllAccess)
            .UInt32(ServiceWin32OwnProcess)
            .UInt32(startType)
            .UInt32(ServiceErrorNormal)
            .String(binaryPath)
            .UniqueString(null) // load order group
            .Null() // tag id
            .Null() // dependencies
            .UInt32(0)
            .UniqueString(null) // service start name: LocalSystem
            .Null() // password
            .UInt32(0)
            .ToArray();
        var response = new NdrReader(Call(12, request));
        response.UniqueUInt32(); // tag id
        var handle = response.Handle();
        Check($"Creating the service {name}", response.UInt32());
        return handle;
    }

    // ROpenServiceW (opnum 16); null if the service does not exist.
    public ScmHandle? Open(ScmHandle manager, string name, uint access)
    {
        var request = new NdrWriter().Handle(manager.Value).String(name).UInt32(access).ToArray();
        var response = new NdrReader(Call(16, request));
        var handle = response.Handle();
        var error = response.UInt32();
        if (error == ScmException.ServiceDoesNotExist)
        {
            return null;
        }

        Check($"Opening the service {name}", error);
        return handle;
    }

    // RStartServiceW (opnum 19) without arguments.
    public void Start(ScmHandle service)
    {
        var request = new NdrWriter().Handle(service.Value).UInt32(0).Null().ToArray();
        var error = new NdrReader(Call(19, request)).UInt32();
        if (error != ScmException.ServiceAlreadyRunning)
        {
            Check("Starting the service", error);
        }
    }

    // RQueryServiceStatus (opnum 6)
    public ServiceStatus Query(ScmHandle service)
    {
        var response = new NdrReader(Call(6, new NdrWriter().Handle(service.Value).ToArray()));
        var status = ReadStatus(response);
        Check("Querying the service status", response.UInt32());
        return status;
    }

    // RControlService (opnum 1) with SERVICE_CONTROL_STOP; a service that is not running is fine.
    public void Stop(ScmHandle service)
    {
        var response = new NdrReader(Call(1, new NdrWriter().Handle(service.Value).UInt32(ServiceControlStop).ToArray()));
        ReadStatus(response);
        var error = response.UInt32();
        if (error != ScmException.ServiceNotActive)
        {
            Check("Stopping the service", error);
        }
    }

    // RDeleteService (opnum 2)
    public void Delete(ScmHandle service)
    {
        var error = new NdrReader(Call(2, new NdrWriter().Handle(service.Value).ToArray())).UInt32();
        if (error != ScmException.ServiceMarkedForDelete)
        {
            Check("Deleting the service", error);
        }
    }

    // RCloseServiceHandle (opnum 0)
    public void Close(ScmHandle handle)
    {
        if (!handle.IsNull)
        {
            Call(0, new NdrWriter().Handle(handle.Value).ToArray());
        }
    }

    public void Dispose() => _ipc.CloseFile(_pipe);

    private static ServiceStatus ReadStatus(NdrReader reader) =>
        new(reader.UInt32(), reader.UInt32(), reader.UInt32(), reader.UInt32(), reader.UInt32(), reader.UInt32(), reader.UInt32());

    private static void Check(string operation, uint error)
    {
        if (error != 0)
        {
            throw new ScmException(operation, error);
        }
    }

    /// <summary>One RPC request/response over the pipe (FSCTL_PIPE_TRANSCEIVE, further fragments with ReadFile).</summary>
    private byte[] Call(ushort opnum, byte[] stub)
    {
        var request = new RequestPDU
        {
            Flags = PacketFlags.FirstFragment | PacketFlags.LastFragment,
            DataRepresentation = new DataRepresentationFormat(CharacterFormat.ASCII, ByteOrder.LittleEndian, FloatingPointRepresentation.IEEE),
            CallID = ++_callId,
            OpNum = opnum,
            Data = stub,
            AllocationHint = (uint)stub.Length,
        };
        var status = _ipc.DeviceIOControl(_pipe, FsctlPipeTransceive, request.GetBytes(), out var output, _maxFragment);
        if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_BUFFER_OVERFLOW)
        {
            throw new IOException($"RPC call {opnum} to the service control manager failed: {status}.");
        }

        var data = new List<byte>();
        while (true)
        {
            var pdu = RPCPDU.GetPDU(output, 0);
            switch (pdu)
            {
                case FaultPDU fault:
                    throw new IOException($"The service control manager rejected RPC call {opnum}: fault {fault.Status}.");
                case ResponsePDU response:
                    data.AddRange(response.Data);
                    if ((response.Flags & PacketFlags.LastFragment) != 0)
                    {
                        return [.. data];
                    }

                    break;
                default:
                    throw new IOException($"Unexpected RPC answer to call {opnum}: {pdu?.GetType().Name ?? "none"}.");
            }

            status = _ipc.ReadFile(out output, _pipe, 0, _maxFragment);
            if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_BUFFER_OVERFLOW)
            {
                throw new IOException($"Reading the answer to RPC call {opnum} failed: {status}.");
            }
        }
    }
}
