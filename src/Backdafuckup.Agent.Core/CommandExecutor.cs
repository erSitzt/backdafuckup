using System;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>VSS in the guest: requester plus our own provider in <c>bdfu-vss.dll</c> (Windows) or a fake (tests).</summary>
public interface IVssBridge
{
    /// <summary>"registered", "missing" or an error text.</summary>
    string ProviderState { get; }

    /// <summary>Starts a backup: writer metadata (with SQL databases from the SQL writer's components).</summary>
    GuestBackupInfo Prepare(bool copyOnly);

    /// <summary>Returns once our provider holds the writes; the provider releases them by itself after <paramref name="holdTimeoutMs"/>.</summary>
    FreezeReport Freeze(int holdTimeoutMs);

    /// <summary>Releases the writes, completes the snapshot set and reports the writer states.</summary>
    ThawReport Thaw();

    /// <summary>Abandons the backup (also while frozen).</summary>
    void Abort();
}

/// <summary>Facts about the machine for the server: names, BIOS serial (= VM UUID on XCP-ng), OS, addresses.</summary>
public interface IHostInfoSource
{
    AgentHostInfo Collect();
}

/// <summary>A failed VSS call with its HRESULT.</summary>
public sealed class VssException : Exception
{
    public VssException(string message, int hresult)
        : base(message)
    {
        HResult = hresult;
    }
}

/// <summary>
/// Executes the commands of the server. Prepare, freeze and thaw belong to one operation and must come in this order;
/// abort is accepted at any time (the server sends it when it gives up waiting).
/// </summary>
public sealed class CommandExecutor
{
    private readonly IVssBridge _vss;
    private readonly IHostInfoSource _host;
    private readonly AgentLog _log;
    private readonly object _lock = new object();
    private Guid? _operation;
    private string _stage = "idle";

    public CommandExecutor(IVssBridge vss, IHostInfoSource host, AgentLog log)
    {
        _vss = vss;
        _host = host;
        _log = log;
    }

    public AgentCommandResult Execute(AgentCommand command)
    {
        var result = new AgentCommandResult { CommandId = command.Id };
        try
        {
            switch (command.Kind)
            {
                case AgentCommandKinds.GetInfo:
                    result.Info = _host.Collect();
                    break;
                case AgentCommandKinds.Abort:
                    Abort("server request");
                    break;
                case AgentCommandKinds.PrepareBackup:
                    lock (_lock)
                    {
                        if (_operation is not null)
                        {
                            // A previous backup the server gave up on: start clean.
                            Abort("a new backup starts");
                        }

                        _operation = command.OperationId;
                        _stage = "prepared";
                        result.Prepare = _vss.Prepare(command.CopyOnly);
                        _log.Write($"Backup {command.OperationId}: VSS prepared ({result.Prepare.Writers.Count} writer(s), {result.Prepare.SqlDatabases.Count} SQL database(s)).");
                    }

                    break;
                case AgentCommandKinds.Freeze:
                    Expect(command, "prepared");

                    // No disk writes until the thaw: they would wait for the frozen volume (see AgentLog).
                    _log.Pause();
                    result.Freeze = _vss.Freeze(command.TimeoutMs);
                    _stage = "frozen";
                    _log.Write($"Backup {command.OperationId}: writes held after {result.Freeze.FreezeMs} ms.");
                    break;
                case AgentCommandKinds.Thaw:
                    Expect(command, "frozen");
                    try
                    {
                        result.Thaw = _vss.Thaw();
                    }
                    finally
                    {
                        _log.Resume();
                    }

                    Reset();
                    _log.Write($"Backup {command.OperationId}: thawed after {result.Thaw.HeldMs} ms, " +
                        (result.Thaw.Consistent ? "application-consistent." : $"not consistent (0x{result.Thaw.VssResult:X8})."));
                    break;
                default:
                    // Not Fail(): an unknown command must not abort a backup that is prepared or frozen right now.
                    result.Error = "Unknown command " + command.Kind + " (agent older than the server?).";
                    _log.Write(result.Error);
                    return result;
            }

            result.Success = true;
        }
        catch (VssException ex)
        {
            Fail(result, command, ex.Message, ex.HResult);
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is TimeoutException)
        {
            Fail(result, command, ex.Message, 0);
        }

        return result;
    }

    private void Expect(AgentCommand command, string stage)
    {
        lock (_lock)
        {
            if (_operation != command.OperationId || _stage != stage)
            {
                throw new InvalidOperationException($"{command.Kind} does not fit the state of the agent ({_stage}, backup {_operation?.ToString() ?? "none"}).");
            }
        }
    }

    private void Fail(AgentCommandResult result, AgentCommand command, string message, int code)
    {
        if (command.Kind == AgentCommandKinds.Freeze)
        {
            // The freeze did not happen (or VSS gave up): release the abort first, then write again.
            Abort("the command failed");
            _log.Resume();
        }

        result.Success = false;
        result.Error = message;
        result.ErrorCode = code;
        _log.Write($"{command.Kind} failed: {message}" + (code != 0 ? $" (0x{code:X8})" : string.Empty));
        if (command.Kind != AgentCommandKinds.GetInfo && command.Kind != AgentCommandKinds.Abort)
        {
            Abort("the command failed");
        }
    }

    private void Abort(string reason)
    {
        lock (_lock)
        {
            if (_operation is null)
            {
                return;
            }

            _log.Write($"Backup {_operation}: aborted ({reason}).");
            Reset();
        }

        try
        {
            _vss.Abort();
        }
        catch (VssException ex)
        {
            _log.Write("Abort: " + ex.Message);
        }
        finally
        {
            _log.Resume();
        }
    }

    private void Reset()
    {
        lock (_lock)
        {
            _operation = null;
            _stage = "idle";
        }
    }
}
