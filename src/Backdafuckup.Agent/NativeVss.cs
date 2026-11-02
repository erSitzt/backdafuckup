using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent;

/// <summary>
/// VSS through <c>bdfu-vss.dll</c> (native/bdfu-vss): the requester runs here in the agent; our software provider is
/// loaded by VSS and holds the writes in CommitSnapshots until the thaw. Strings in and out are UTF-16; JSON returned by
/// the DLL is allocated with CoTaskMemAlloc.
/// </summary>
internal sealed class NativeVss : IVssBridge
{
    private const string Dll = AgentProtocol.VssDllName;

    public string ProviderState
    {
        get
        {
            try
            {
                var hr = bdfu_vss_provider_state(out var message);
                var text = Take(message);
                return hr == 0 ? "registered" : hr == 1 ? "missing" : $"error 0x{hr:X8}: {text}";
            }
            catch (DllNotFoundException)
            {
                return $"{Dll} is missing";
            }
        }
    }

    public GuestBackupInfo Prepare(bool copyOnly)
    {
        var hr = bdfu_vss_prepare(copyOnly ? 1 : 0, out var json);
        var text = Take(json);
        Check(hr, text, "Preparing the VSS backup");
        var native = JsonSerializer.Deserialize<NativePrepare>(text ?? "{}", AgentJson.Options) ?? new NativePrepare();
        return new GuestBackupInfo
        {
            Volumes = WindowsHostInfo.Volumes(),
            Writers = native.Writers.Select(w => w.ToWriter()).ToList(),
            SqlDatabases = native.Writers.Where(w => string.Equals(w.Name, "SqlServerWriter", StringComparison.OrdinalIgnoreCase))
                .SelectMany(w => w.Components)
                .Select(c => new GuestSqlDatabase { Instance = Instance(c.LogicalPath), Database = c.Name, Files = c.Files })
                .ToList(),
        };
    }

    public FreezeReport Freeze(int holdTimeoutMs)
    {
        var watch = Stopwatch.StartNew();
        var hr = bdfu_vss_freeze(holdTimeoutMs, 120_000, out var message);
        Check(hr, Take(message), "Freezing the guest");
        return new FreezeReport { FreezeMs = watch.ElapsedMilliseconds };
    }

    public ThawReport Thaw()
    {
        var hr = bdfu_vss_thaw(out var json);
        var text = Take(json);
        Check(hr, text, "Thawing the guest");
        var native = JsonSerializer.Deserialize<NativeThaw>(text ?? "{}", AgentJson.Options) ?? new NativeThaw();
        return new ThawReport
        {
            HeldMs = native.HeldMs,
            VssResult = native.VssResult,
            Consistent = native.VssResult == 0 && native.Writers.All(w => w.Failure is null),
            Writers = native.Writers.Select(w => w.ToWriter()).ToList(),
        };
    }

    public void Abort() => bdfu_vss_abort();

    /// <summary>Registers our provider with COM+ and VSS (needs administrator rights).</summary>
    public static void Register(string dllPath)
    {
        var hr = bdfu_vss_register(dllPath, out var message);
        Check(hr, Take(message), "Registering the backdafuckup VSS provider");
    }

    public static void Unregister()
    {
        var hr = bdfu_vss_unregister(out var message);
        Check(hr, Take(message), "Removing the backdafuckup VSS provider");
    }

    private static void Check(int hr, string? text, string what)
    {
        if (hr < 0)
        {
            string? detail = null;
            try
            {
                detail = JsonSerializer.Deserialize<NativeError>(text ?? "{}", AgentJson.Options)?.Error;
            }
            catch (JsonException)
            {
            }

            // The DLL names the error itself; otherwise add the name here. Then what Windows logged about it.
            var message = $"{what} failed: {detail ?? text ?? "no details"}";
            if (detail is null || detail.IndexOf("0x", StringComparison.OrdinalIgnoreCase) < 0)
            {
                message += $" ({VssErrors.Name(hr)})";
            }

            throw new VssException(message + WindowsEvents.Recent(), hr);
        }
    }

    /// <summary>"SQL01\SQLEXPRESS" → "SQLEXPRESS"; "SQL01" → "MSSQLSERVER" (default instance).</summary>
    private static string Instance(string? logicalPath)
    {
        var path = logicalPath ?? string.Empty;
        var slash = path.IndexOf('\\');
        return slash >= 0 ? path.Substring(slash + 1) : "MSSQLSERVER";
    }

    private static string? Take(IntPtr text)
    {
        if (text == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int bdfu_vss_provider_state(out IntPtr message);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int bdfu_vss_prepare(int copyOnly, out IntPtr json);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int bdfu_vss_freeze(int holdTimeoutMs, int waitTimeoutMs, out IntPtr message);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int bdfu_vss_thaw(out IntPtr json);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int bdfu_vss_abort();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int bdfu_vss_register(string dllPath, out IntPtr message);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int bdfu_vss_unregister(out IntPtr message);

    private sealed class NativePrepare
    {
        public List<NativeWriter> Writers { get; set; } = new List<NativeWriter>();
    }

    private sealed class NativeThaw
    {
        public int VssResult { get; set; }

        public long HeldMs { get; set; }

        public List<NativeWriter> Writers { get; set; } = new List<NativeWriter>();
    }

    private sealed class NativeError
    {
        public string? Error { get; set; }
    }

    private sealed class NativeWriter
    {
        public string Name { get; set; } = string.Empty;

        public string WriterId { get; set; } = string.Empty;

        public string? InstanceName { get; set; }

        public string State { get; set; } = string.Empty;

        public string? Failure { get; set; }

        public List<NativeComponent> Components { get; set; } = new List<NativeComponent>();

        public GuestWriter ToWriter() => new GuestWriter
        {
            Name = Name,
            WriterId = WriterId,
            InstanceName = InstanceName,
            State = State,
            Failure = Failure,
            Components = Components.Select(c => string.IsNullOrEmpty(c.LogicalPath) ? c.Name : c.LogicalPath + "\\" + c.Name).ToList(),
        };
    }

    private sealed class NativeComponent
    {
        public string? LogicalPath { get; set; }

        public string Name { get; set; } = string.Empty;

        public List<string> Files { get; set; } = new List<string>();
    }
}

/// <summary>
/// What Windows logged about VSS and COM+ in the last minutes (Application log): VSS reports the real cause of a
/// provider error there (e.g. which COM call failed with which HRESULT).
/// </summary>
internal static class WindowsEvents
{
    private static readonly string[] Sources = { "VSS", "COM+", "VSSVC", "SQLWRITER", "SQLVDI" };

    public static string Recent()
    {
        try
        {
            var since = DateTime.Now.AddMinutes(-3);
            using (var log = new System.Diagnostics.EventLog("Application"))
            {
                var entries = new List<string>();
                for (var i = log.Entries.Count - 1; i >= 0 && entries.Count < 4; i--)
                {
                    var entry = log.Entries[i];
                    if (entry.TimeGenerated < since)
                    {
                        break;
                    }

                    if (entry.EntryType != System.Diagnostics.EventLogEntryType.Information && Array.IndexOf(Sources, entry.Source) >= 0)
                    {
                        var text = entry.Message.Replace("\r", " ").Replace("\n", " ");
                        entries.Add($"[{entry.TimeGenerated:HH:mm:ss} {entry.Source} {entry.InstanceId & 0xFFFF}] {(text.Length > 600 ? text.Substring(0, 600) + " …" : text)}");
                    }
                }

                return entries.Count == 0 ? string.Empty : " Windows event log: " + string.Join(" | ", entries);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is System.Security.SecurityException || ex is ArgumentException)
        {
            return string.Empty;
        }
    }
}

/// <summary>Names of the VSS HRESULTs that matter for the freeze (vsserror.h).</summary>
internal static class VssErrors
{
    public static string Name(int hr)
    {
        switch (unchecked((uint)hr))
        {
            case 0x80042301: return "VSS_E_BAD_STATE";
            case 0x80042306: return "VSS_E_PROVIDER_VETO";
            case 0x8004230C: return "VSS_E_VOLUME_NOT_SUPPORTED";
            case 0x8004230F: return "VSS_E_UNEXPECTED_PROVIDER_ERROR";
            case 0x80042313: return "VSS_E_FLUSH_WRITES_TIMEOUT";
            case 0x80042314: return "VSS_E_HOLD_WRITES_TIMEOUT";
            case 0x80042315: return "VSS_E_UNEXPECTED_WRITER_ERROR";
            case 0x80042316: return "VSS_E_SNAPSHOT_SET_IN_PROGRESS";
            case 0x80042318: return "VSS_E_WRITER_INFRASTRUCTURE";
            case 0x80042319: return "VSS_E_WRITER_NOT_RESPONDING";
            case 0x8004231F: return "VSS_E_INSUFFICIENT_STORAGE";
            case 0x800423F0: return "VSS_E_WRITERERROR_INCONSISTENTSNAPSHOT";
            case 0x800423F2: return "VSS_E_WRITERERROR_TIMEOUT";
            case 0x800423F3: return "VSS_E_WRITERERROR_RETRYABLE";
            case 0x800423F4: return "VSS_E_WRITERERROR_NONRETRYABLE";
            case 0x80070005: return "E_ACCESSDENIED";
            default: return $"0x{hr:X8}";
        }
    }
}
