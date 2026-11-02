// bdfu-vss.dll – VSS for the backdafuckup agent (AGPL-3.0, part of backdafuckup).
//
// Two roles in one DLL:
//  1. A VSS *requester* used by the agent through C exports (bdfu_vss_*): prepares a copy-only backup with the writers,
//     starts DoSnapshotSet and reports "frozen" as soon as our provider holds the writes, then thaws and reports the
//     writer states.
//  2. A VSS *software provider* (COM class, loaded by VSS in a COM+ application): in CommitSnapshots – inside the VSS
//     flush-and-hold window – it signals "frozen" and blocks until the agent signals "thaw" (after the hypervisor took the
//     VM snapshot). It creates no shadow copy of its own; the VM snapshot is the snapshot.
//
// The freeze pattern is the one of the QEMU guest agent and the old XenServer VSS provider, implemented from the public
// Microsoft documentation (no code taken). Built with mingw-w64 >= 10 (headers vss.h, vsprov.h, vsadmin.h, comadmin.h).
//
// Coexistence with other backup products: IsVolumeSupported says yes only while the named event "frozen" exists, i.e.
// only during our own AddToSnapshotSet calls and our freeze. Otherwise VSS keeps using the system provider for others.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>
#include <oleauto.h>
#include <sddl.h>
#include <vss.h>
#include <vswriter.h>
#include <vsbackup.h>
#include <vsprov.h>
#include <vsadmin.h>
#include <comadmin.h>

#include <cstdio>
#include <new>
#include <string>
#include <vector>

namespace {

// {cabef6cc-dbb3-4c01-9332-81a3b04fe0bb}
const GUID kProviderId = {0xcabef6cc, 0xdbb3, 0x4c01, {0x93, 0x32, 0x81, 0xa3, 0xb0, 0x4f, 0xe0, 0xbb}};
// {07710e11-10ab-43b1-b568-cf05c719eb7b}
const CLSID kProviderClsid = {0x07710e11, 0x10ab, 0x43b1, {0xb5, 0x68, 0xcf, 0x05, 0xc7, 0x19, 0xeb, 0x7b}};
// {dd48c4f9-57fd-4cfe-a6d2-43cddf026f59}
const GUID kProviderVersionId = {0xdd48c4f9, 0x57fd, 0x4cfe, {0xa6, 0xd2, 0x43, 0xcd, 0xdf, 0x02, 0x6f, 0x59}};
// VSS coordinator (IVssAdmin) and the COM+ catalog.
const CLSID kVssCoordinator = {0xe579ab5f, 0x1cc4, 0x44b4, {0xbe, 0xd9, 0xde, 0x09, 0x91, 0xff, 0x06, 0x23}};
const CLSID kComAdminCatalog = {0xf618c514, 0xdfb8, 0x11d1, {0xa2, 0xcf, 0x00, 0x80, 0x5f, 0xc7, 0x92, 0x35}};

const wchar_t kProviderName[] = L"backdafuckup VSS provider";
const wchar_t kProviderVersion[] = L"1.0.0";
const wchar_t kAppName[] = L"backdafuckup VSS provider";
const wchar_t kEventFrozen[] = L"Global\\backdafuckup-vss-frozen";
const wchar_t kEventThaw[] = L"Global\\backdafuckup-vss-thaw";
const wchar_t kEventTimeout[] = L"Global\\backdafuckup-vss-timeout";
// SYSTEM (agent and provider service) and administrators.
const wchar_t kEventSddl[] = L"D:P(A;;GA;;;SY)(A;;GA;;;BA)";
// The COM+ application of the provider runs as this Windows service under Local System (a COM+ "Identity" of
// LocalSystem with an empty password is rejected at activation: CO_E_RUNAS_LOGON_FAILURE).
const wchar_t kServiceName[] = L"BackdafuckupVssProvider";

// The provider never holds the writes longer than this, even if no thaw arrives (VSS gives up after 10 s anyway).
const DWORD kProviderHoldLimitMs = 60000;
const DWORD kAsyncTimeoutMs = 10 * 60 * 1000;

HMODULE g_module = nullptr;
LONG g_objects = 0;
LONG g_locks = 0;

// ---------------------------------------------------------------------------------------------------------------- util

std::wstring Hex(HRESULT hr)
{
    wchar_t buffer[16];
    swprintf(buffer, 16, L"0x%08X", static_cast<unsigned>(hr));
    return buffer;
}

std::wstring GuidText(const GUID& guid)
{
    wchar_t buffer[64];
    StringFromGUID2(guid, buffer, 64);
    return buffer;
}

std::wstring JsonString(const std::wstring& text)
{
    std::wstring out = L"\"";
    for (wchar_t c : text)
    {
        switch (c)
        {
        case L'"': out += L"\\\""; break;
        case L'\\': out += L"\\\\"; break;
        case L'\n': out += L"\\n"; break;
        case L'\r': out += L"\\r"; break;
        case L'\t': out += L"\\t"; break;
        default:
            if (c < 0x20)
            {
                wchar_t buffer[8];
                swprintf(buffer, 8, L"\\u%04x", static_cast<unsigned>(c));
                out += buffer;
            }
            else
            {
                out += c;
            }
        }
    }

    return out + L"\"";
}

HRESULT Out(wchar_t** target, const std::wstring& text)
{
    if (target == nullptr)
    {
        return S_OK;
    }

    *target = static_cast<wchar_t*>(CoTaskMemAlloc((text.size() + 1) * sizeof(wchar_t)));
    if (*target == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    memcpy(*target, text.c_str(), (text.size() + 1) * sizeof(wchar_t));
    return S_OK;
}

HRESULT Fail(wchar_t** target, HRESULT hr, const std::wstring& message)
{
    Out(target, L"{\"error\":" + JsonString(message + L" (" + Hex(hr) + L")") + L"}");
    return FAILED(hr) ? hr : E_FAIL;
}

std::wstring Bstr(BSTR value)
{
    return value != nullptr ? std::wstring(value, SysStringLen(value)) : std::wstring();
}

const wchar_t* WriterState(VSS_WRITER_STATE state)
{
    switch (state)
    {
    case VSS_WS_STABLE: return L"Stable";
    case VSS_WS_WAITING_FOR_FREEZE: return L"WaitingForFreeze";
    case VSS_WS_WAITING_FOR_THAW: return L"WaitingForThaw";
    case VSS_WS_WAITING_FOR_POST_SNAPSHOT: return L"WaitingForPostSnapshot";
    case VSS_WS_WAITING_FOR_BACKUP_COMPLETE: return L"WaitingForBackupComplete";
    case VSS_WS_FAILED_AT_IDENTIFY: return L"FailedAtIdentify";
    case VSS_WS_FAILED_AT_PREPARE_BACKUP: return L"FailedAtPrepareBackup";
    case VSS_WS_FAILED_AT_PREPARE_SNAPSHOT: return L"FailedAtPrepareSnapshot";
    case VSS_WS_FAILED_AT_FREEZE: return L"FailedAtFreeze";
    case VSS_WS_FAILED_AT_THAW: return L"FailedAtThaw";
    case VSS_WS_FAILED_AT_POST_SNAPSHOT: return L"FailedAtPostSnapshot";
    case VSS_WS_FAILED_AT_BACKUP_COMPLETE: return L"FailedAtBackupComplete";
    case VSS_WS_FAILED_AT_PRE_RESTORE: return L"FailedAtPreRestore";
    case VSS_WS_FAILED_AT_POST_RESTORE: return L"FailedAtPostRestore";
    default: return L"Unknown";
    }
}

std::wstring VssErrorName(HRESULT hr)
{
    switch (hr)
    {
    case VSS_E_BAD_STATE: return L"VSS_E_BAD_STATE";
    case VSS_E_PROVIDER_VETO: return L"VSS_E_PROVIDER_VETO";
    case VSS_E_UNEXPECTED_PROVIDER_ERROR: return L"VSS_E_UNEXPECTED_PROVIDER_ERROR";
    case VSS_E_FLUSH_WRITES_TIMEOUT: return L"VSS_E_FLUSH_WRITES_TIMEOUT";
    case VSS_E_HOLD_WRITES_TIMEOUT: return L"VSS_E_HOLD_WRITES_TIMEOUT";
    case VSS_E_UNEXPECTED_WRITER_ERROR: return L"VSS_E_UNEXPECTED_WRITER_ERROR";
    case VSS_E_SNAPSHOT_SET_IN_PROGRESS: return L"VSS_E_SNAPSHOT_SET_IN_PROGRESS";
    case VSS_E_WRITER_INFRASTRUCTURE: return L"VSS_E_WRITER_INFRASTRUCTURE";
    case VSS_E_WRITER_NOT_RESPONDING: return L"VSS_E_WRITER_NOT_RESPONDING";
    case VSS_E_VOLUME_NOT_SUPPORTED: return L"VSS_E_VOLUME_NOT_SUPPORTED";
    case VSS_E_VOLUME_NOT_SUPPORTED_BY_PROVIDER: return L"VSS_E_VOLUME_NOT_SUPPORTED_BY_PROVIDER";
    case VSS_E_WRITERERROR_INCONSISTENTSNAPSHOT: return L"VSS_E_WRITERERROR_INCONSISTENTSNAPSHOT";
    case VSS_E_WRITERERROR_OUTOFRESOURCES: return L"VSS_E_WRITERERROR_OUTOFRESOURCES";
    case VSS_E_WRITERERROR_TIMEOUT: return L"VSS_E_WRITERERROR_TIMEOUT";
    case VSS_E_WRITERERROR_RETRYABLE: return L"VSS_E_WRITERERROR_RETRYABLE";
    case VSS_E_WRITERERROR_NONRETRYABLE: return L"VSS_E_WRITERERROR_NONRETRYABLE";
    case E_ACCESSDENIED: return L"E_ACCESSDENIED";
    default: return Hex(hr);
    }
}

bool EventExists(const wchar_t* name)
{
    HANDLE handle = OpenEventW(SYNCHRONIZE, FALSE, name);
    if (handle == nullptr)
    {
        return false;
    }

    CloseHandle(handle);
    return true;
}

HANDLE CreateGuardedEvent(const wchar_t* name)
{
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(kEventSddl, SDDL_REVISION_1, &descriptor, nullptr))
    {
        return nullptr;
    }

    SECURITY_ATTRIBUTES attributes = {sizeof(attributes), descriptor, FALSE};
    HANDLE handle = CreateEventW(&attributes, TRUE, FALSE, name);
    LocalFree(descriptor);
    if (handle != nullptr)
    {
        ResetEvent(handle);
    }

    return handle;
}

// Initializes COM (MTA) for the calling thread; the agent calls from thread-pool threads.
class ComScope
{
public:
    ComScope() : _uninit(SUCCEEDED(CoInitializeEx(nullptr, COINIT_MULTITHREADED))) {}
    ~ComScope()
    {
        if (_uninit)
        {
            CoUninitialize();
        }
    }

private:
    bool _uninit;
};

void InitializeComSecurityOnce()
{
    static LONG done = 0;
    if (InterlockedCompareExchange(&done, 1, 0) == 0)
    {
        // VSS needs at least identify-level impersonation; RPC_E_TOO_LATE means the process already chose a setting.
        CoInitializeSecurity(nullptr, -1, nullptr, nullptr, RPC_C_AUTHN_LEVEL_PKT_PRIVACY, RPC_C_IMP_LEVEL_IDENTIFY, nullptr, EOAC_DYNAMIC_CLOAKING, nullptr);
    }
}

HRESULT WaitAsync(IVssAsync* async, DWORD timeoutMs, HRESULT* status)
{
    HRESULT hr = async->Wait(timeoutMs);
    if (FAILED(hr))
    {
        async->Release();
        return hr;
    }

    HRESULT result = S_OK;
    hr = async->QueryStatus(&result, nullptr);
    if (SUCCEEDED(hr) && result == VSS_S_ASYNC_PENDING)
    {
        async->Cancel();
        result = HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    }

    async->Release();
    if (status != nullptr)
    {
        *status = result;
    }

    return FAILED(hr) ? hr : (FAILED(result) ? result : S_OK);
}

// ----------------------------------------------------------------------------------------------------------- requester

struct Component
{
    VSS_ID instance;
    VSS_ID writer;
    VSS_COMPONENT_TYPE type;
    std::wstring logicalPath;
    std::wstring name;
};

struct RequesterState
{
    IVssBackupComponents* backup = nullptr;
    IVssAsync* snapshot = nullptr;
    HANDLE frozen = nullptr;
    HANDLE timeout = nullptr;
    VSS_ID setId = GUID_NULL;
    ULONGLONG frozenAt = 0;
    bool isFrozen = false;
    std::vector<Component> components;
};

SRWLOCK g_lock = SRWLOCK_INIT;
RequesterState g_state;
// The thaw event stays open for the life of the process, so that an abort can signal it without the lock.
HANDLE g_thaw = nullptr;
volatile LONG g_abortRequested = 0;

void CloseFrozenEvents()
{
    if (g_state.frozen != nullptr)
    {
        CloseHandle(g_state.frozen);
        g_state.frozen = nullptr;
    }

    if (g_state.timeout != nullptr)
    {
        CloseHandle(g_state.timeout);
        g_state.timeout = nullptr;
    }
}

// Ends the backup (lock held): releases the provider, aborts VSS, drops all state.
void Cleanup(bool abort)
{
    if (g_thaw != nullptr)
    {
        SetEvent(g_thaw);
    }

    if (g_state.snapshot != nullptr)
    {
        g_state.snapshot->Wait(30000);
        g_state.snapshot->Release();
        g_state.snapshot = nullptr;
    }

    if (g_state.backup != nullptr)
    {
        if (abort)
        {
            g_state.backup->AbortBackup();
        }

        g_state.backup->Release();
        g_state.backup = nullptr;
    }

    CloseFrozenEvents();
    g_state.components.clear();
    g_state.setId = GUID_NULL;
    g_state.isFrozen = false;
    g_state.frozenAt = 0;
}

std::wstring FileList(IVssWMComponent* component, UINT count, HRESULT (STDMETHODCALLTYPE IVssWMComponent::*get)(UINT, IVssWMFiledesc**))
{
    std::wstring out;
    for (UINT i = 0; i < count; i++)
    {
        IVssWMFiledesc* file = nullptr;
        if (FAILED((component->*get)(i, &file)) || file == nullptr)
        {
            continue;
        }

        BSTR path = nullptr;
        BSTR spec = nullptr;
        file->GetPath(&path);
        file->GetFilespec(&spec);
        std::wstring full = Bstr(path);
        if (!full.empty() && full.back() != L'\\')
        {
            full += L'\\';
        }

        full += Bstr(spec);
        SysFreeString(path);
        SysFreeString(spec);
        file->Release();
        if (!out.empty())
        {
            out += L',';
        }

        out += JsonString(full);
    }

    return out;
}

// Writer metadata as JSON; adds the selectable components to the backup.
HRESULT DescribeWriters(std::wstring& json)
{
    UINT writers = 0;
    HRESULT hr = g_state.backup->GetWriterMetadataCount(&writers);
    if (FAILED(hr))
    {
        return hr;
    }

    json = L"{\"writers\":[";
    for (UINT w = 0; w < writers; w++)
    {
        VSS_ID instance = GUID_NULL;
        IVssExamineWriterMetadata* metadata = nullptr;
        if (FAILED(g_state.backup->GetWriterMetadata(w, &instance, &metadata)) || metadata == nullptr)
        {
            continue;
        }

        VSS_ID instanceId = GUID_NULL;
        VSS_ID writerId = GUID_NULL;
        BSTR name = nullptr;
        VSS_USAGE_TYPE usage;
        VSS_SOURCE_TYPE source;
        UINT includes = 0;
        UINT excludes = 0;
        UINT components = 0;
        metadata->GetIdentity(&instanceId, &writerId, &name, &usage, &source);
        metadata->GetFileCounts(&includes, &excludes, &components);
        if (w > 0)
        {
            json += L',';
        }

        json += L"{\"name\":" + JsonString(Bstr(name)) + L",\"writerId\":" + JsonString(GuidText(writerId)) +
                L",\"instanceName\":" + JsonString(GuidText(instanceId)) + L",\"state\":\"Stable\",\"components\":[";
        SysFreeString(name);
        bool first = true;
        for (UINT c = 0; c < components; c++)
        {
            IVssWMComponent* component = nullptr;
            if (FAILED(metadata->GetComponent(c, &component)) || component == nullptr)
            {
                continue;
            }

            PVSSCOMPONENTINFO info = nullptr;
            if (SUCCEEDED(component->GetComponentInfo(&info)) && info != nullptr)
            {
                std::wstring logical = info->bstrLogicalPath != nullptr ? Bstr(info->bstrLogicalPath) : std::wstring();
                std::wstring componentName = Bstr(info->bstrComponentName);
                std::wstring files = FileList(component, info->cFileCount, &IVssWMComponent::GetFile);
                std::wstring databases = FileList(component, info->cDatabases, &IVssWMComponent::GetDatabaseFile);
                std::wstring logs = FileList(component, info->cLogFiles, &IVssWMComponent::GetDatabaseLogFile);
                std::wstring all = files;
                for (const std::wstring* part : {&databases, &logs})
                {
                    if (!part->empty())
                    {
                        all += (all.empty() ? L"" : L",") + *part;
                    }
                }

                json += std::wstring(first ? L"" : L",") + L"{\"logicalPath\":" + JsonString(logical) + L",\"name\":" + JsonString(componentName) +
                        L",\"files\":[" + all + L"]}";
                first = false;
                if (info->bSelectable)
                {
                    HRESULT added = g_state.backup->AddComponent(instanceId, writerId, info->type, info->bstrLogicalPath, info->bstrComponentName);
                    if (SUCCEEDED(added))
                    {
                        g_state.components.push_back({instanceId, writerId, info->type, logical, componentName});
                    }
                }

                component->FreeComponentInfo(info);
            }

            component->Release();
        }

        json += L"]}";
        metadata->Release();
    }

    json += L"]}";
    return S_OK;
}

// Adds every fixed volume with a file system to the snapshot set, with our provider.
HRESULT AddVolumes(std::wstring& error)
{
    wchar_t volume[MAX_PATH + 1];
    HANDLE find = FindFirstVolumeW(volume, MAX_PATH);
    if (find == INVALID_HANDLE_VALUE)
    {
        error = L"No volumes found";
        return HRESULT_FROM_WIN32(GetLastError());
    }

    int added = 0;
    do
    {
        wchar_t fileSystem[MAX_PATH + 1] = L"";
        if (GetDriveTypeW(volume) != DRIVE_FIXED ||
            !GetVolumeInformationW(volume, nullptr, 0, nullptr, nullptr, nullptr, fileSystem, MAX_PATH))
        {
            continue;
        }

        VSS_ID snapshot = GUID_NULL;
        HRESULT hr = g_state.backup->AddToSnapshotSet(volume, kProviderId, &snapshot);
        if (SUCCEEDED(hr))
        {
            added++;
        }
        else if (hr != VSS_E_VOLUME_NOT_SUPPORTED && hr != VSS_E_VOLUME_NOT_SUPPORTED_BY_PROVIDER)
        {
            FindVolumeClose(find);
            error = std::wstring(L"Adding volume ") + volume + L" to the snapshot set failed: " + VssErrorName(hr);
            return hr;
        }
    } while (FindNextVolumeW(find, volume, MAX_PATH));

    FindVolumeClose(find);
    if (added == 0)
    {
        error = L"No volume can be frozen (no fixed volume with a file system)";
        return VSS_E_VOLUME_NOT_SUPPORTED;
    }

    return S_OK;
}

}  // namespace

// ---------------------------------------------------------------------------------------------------- requester exports

extern "C" __declspec(dllexport) int bdfu_vss_prepare(int copyOnly, wchar_t** json)
{
    ComScope com;
    InitializeComSecurityOnce();
    AcquireSRWLockExclusive(&g_lock);
    Cleanup(true);
    InterlockedExchange(&g_abortRequested, 0);
    HRESULT hr = S_OK;
    std::wstring error;
    std::wstring writers;
    do
    {
        if (g_thaw == nullptr && (g_thaw = CreateGuardedEvent(kEventThaw)) == nullptr)
        {
            hr = HRESULT_FROM_WIN32(GetLastError());
            error = L"Creating the thaw event failed";
            break;
        }

        if (FAILED(hr = CreateVssBackupComponents(&g_state.backup)))
        {
            error = L"CreateVssBackupComponents failed (needs administrator rights)";
            break;
        }

        if (FAILED(hr = g_state.backup->InitializeForBackup(nullptr)))
        {
            error = L"InitializeForBackup failed";
            break;
        }

        if (FAILED(hr = g_state.backup->SetBackupState(true, false, copyOnly ? VSS_BT_COPY : VSS_BT_FULL, false)))
        {
            error = L"SetBackupState failed";
            break;
        }

        // Transportable and without auto-recovery: VSS does not try to surface the (non-existing) shadow copies locally.
        if (FAILED(hr = g_state.backup->SetContext(VSS_CTX_APP_ROLLBACK | VSS_VOLSNAP_ATTR_TRANSPORTABLE | VSS_VOLSNAP_ATTR_NO_AUTORECOVERY | VSS_VOLSNAP_ATTR_TXF_RECOVERY)))
        {
            error = L"SetContext failed";
            break;
        }

        IVssAsync* async = nullptr;
        if (FAILED(hr = g_state.backup->GatherWriterMetadata(&async)) || FAILED(hr = WaitAsync(async, kAsyncTimeoutMs, nullptr)))
        {
            error = L"Gathering the VSS writer metadata failed";
            break;
        }

        if (FAILED(hr = DescribeWriters(writers)))
        {
            error = L"Reading the VSS writer metadata failed";
            break;
        }

        if (FAILED(hr = g_state.backup->StartSnapshotSet(&g_state.setId)))
        {
            error = L"StartSnapshotSet failed";
            break;
        }

        // Our provider claims volumes only while the "frozen" event exists: just for these calls.
        g_state.frozen = CreateGuardedEvent(kEventFrozen);
        hr = AddVolumes(error);
        CloseFrozenEvents();
        if (FAILED(hr))
        {
            break;
        }

        if (FAILED(hr = g_state.backup->PrepareForBackup(&async)) || FAILED(hr = WaitAsync(async, kAsyncTimeoutMs, nullptr)))
        {
            error = L"PrepareForBackup failed (" + VssErrorName(hr) + L")";
            break;
        }
    } while (false);

    if (FAILED(hr))
    {
        Cleanup(true);
        ReleaseSRWLockExclusive(&g_lock);
        return Fail(json, hr, error);
    }

    ReleaseSRWLockExclusive(&g_lock);
    return Out(json, writers);
}

extern "C" __declspec(dllexport) int bdfu_vss_freeze(int holdTimeoutMs, int waitTimeoutMs, wchar_t** message)
{
    (void)holdTimeoutMs;  // The provider holds at most kProviderHoldLimitMs; the server always sends thaw or abort.
    ComScope com;
    AcquireSRWLockExclusive(&g_lock);
    if (g_state.backup == nullptr || g_state.snapshot != nullptr)
    {
        ReleaseSRWLockExclusive(&g_lock);
        return Fail(message, VSS_E_BAD_STATE, L"No prepared backup to freeze");
    }

    ResetEvent(g_thaw);
    g_state.frozen = CreateGuardedEvent(kEventFrozen);
    g_state.timeout = CreateGuardedEvent(kEventTimeout);
    HRESULT hr = g_state.backup->DoSnapshotSet(&g_state.snapshot);
    if (FAILED(hr))
    {
        Cleanup(true);
        ReleaseSRWLockExclusive(&g_lock);
        return Fail(message, hr, L"DoSnapshotSet failed: " + VssErrorName(hr));
    }

    HANDLE frozen = g_state.frozen;
    IVssAsync* snapshot = g_state.snapshot;
    ReleaseSRWLockExclusive(&g_lock);

    // Writers flush and freeze, then VSS calls our provider's CommitSnapshots, which signals "frozen".
    ULONGLONG start = GetTickCount64();
    HRESULT failure = S_OK;
    std::wstring error;
    while (true)
    {
        if (WaitForSingleObject(frozen, 100) == WAIT_OBJECT_0)
        {
            break;
        }

        HRESULT status = S_OK;
        if (SUCCEEDED(snapshot->QueryStatus(&status, nullptr)) && status != VSS_S_ASYNC_PENDING)
        {
            failure = FAILED(status) ? status : VSS_E_UNEXPECTED_PROVIDER_ERROR;
            error = FAILED(status) ? L"The snapshot set failed before the freeze: " + VssErrorName(status)
                                   : L"VSS completed the snapshot set without calling the backdafuckup provider";
            break;
        }

        if (InterlockedCompareExchange(&g_abortRequested, 0, 0) != 0)
        {
            failure = E_ABORT;
            error = L"Aborted while freezing";
            break;
        }

        if (GetTickCount64() - start > static_cast<ULONGLONG>(waitTimeoutMs))
        {
            failure = VSS_E_FLUSH_WRITES_TIMEOUT;
            error = L"The writers did not freeze in time";
            break;
        }
    }

    AcquireSRWLockExclusive(&g_lock);
    if (FAILED(failure))
    {
        Cleanup(true);
        ReleaseSRWLockExclusive(&g_lock);
        return Fail(message, failure, error);
    }

    g_state.isFrozen = true;
    g_state.frozenAt = GetTickCount64();
    ReleaseSRWLockExclusive(&g_lock);
    return S_OK;
}

extern "C" __declspec(dllexport) int bdfu_vss_thaw(wchar_t** json)
{
    ComScope com;
    AcquireSRWLockExclusive(&g_lock);
    if (g_state.snapshot == nullptr || !g_state.isFrozen)
    {
        ReleaseSRWLockExclusive(&g_lock);
        return Fail(json, VSS_E_BAD_STATE, L"Nothing is frozen");
    }

    SetEvent(g_thaw);
    ULONGLONG held = GetTickCount64() - g_state.frozenAt;
    bool providerGaveUp = g_state.timeout != nullptr && WaitForSingleObject(g_state.timeout, 0) == WAIT_OBJECT_0;
    HRESULT status = S_OK;
    WaitAsync(g_state.snapshot, 120000, &status);
    g_state.snapshot = nullptr;
    if (SUCCEEDED(status) && providerGaveUp)
    {
        status = VSS_E_HOLD_WRITES_TIMEOUT;
    }

    if (SUCCEEDED(status))
    {
        for (const Component& component : g_state.components)
        {
            g_state.backup->SetBackupSucceeded(component.instance, component.writer, component.type,
                                                component.logicalPath.empty() ? nullptr : component.logicalPath.c_str(), component.name.c_str(), true);
        }

        IVssAsync* async = nullptr;
        if (SUCCEEDED(g_state.backup->BackupComplete(&async)))
        {
            WaitAsync(async, kAsyncTimeoutMs, nullptr);
        }
    }
    else
    {
        g_state.backup->AbortBackup();
    }

    std::wstring writers;
    IVssAsync* async = nullptr;
    if (SUCCEEDED(g_state.backup->GatherWriterStatus(&async)) && SUCCEEDED(WaitAsync(async, kAsyncTimeoutMs, nullptr)))
    {
        UINT count = 0;
        g_state.backup->GetWriterStatusCount(&count);
        for (UINT i = 0; i < count; i++)
        {
            VSS_ID instance = GUID_NULL;
            VSS_ID writer = GUID_NULL;
            BSTR name = nullptr;
            VSS_WRITER_STATE state = VSS_WS_UNKNOWN;
            HRESULT failure = S_OK;
            if (FAILED(g_state.backup->GetWriterStatus(i, &instance, &writer, &name, &state, &failure)))
            {
                continue;
            }

            if (!writers.empty())
            {
                writers += L',';
            }

            writers += L"{\"name\":" + JsonString(Bstr(name)) + L",\"writerId\":" + JsonString(GuidText(writer)) + L",\"state\":" + JsonString(WriterState(state)) +
                       L",\"failure\":" + (failure == S_OK ? std::wstring(L"null") : JsonString(VssErrorName(failure))) + L"}";
            SysFreeString(name);
        }

        g_state.backup->FreeWriterStatus();
    }

    if (SUCCEEDED(status))
    {
        // The persistent context keeps a record of the (empty) snapshot set: remove it.
        LONG deleted = 0;
        VSS_ID nondeleted = GUID_NULL;
        g_state.backup->DeleteSnapshots(g_state.setId, VSS_OBJECT_SNAPSHOT_SET, TRUE, &deleted, &nondeleted);
    }

    wchar_t number[32];
    swprintf(number, 32, L"%d", static_cast<int>(SUCCEEDED(status) ? 0 : status));
    std::wstring result = std::wstring(L"{\"vssResult\":") + number;
    swprintf(number, 32, L"%llu", static_cast<unsigned long long>(held));
    result += std::wstring(L",\"heldMs\":") + number + L",\"writers\":[" + writers + L"]}";
    Cleanup(false);
    ReleaseSRWLockExclusive(&g_lock);
    return Out(json, result);
}

extern "C" __declspec(dllexport) int bdfu_vss_abort()
{
    // Never blocks behind a freeze in progress: signal the provider and let the freeze clean up.
    InterlockedExchange(&g_abortRequested, 1);
    if (g_thaw != nullptr)
    {
        SetEvent(g_thaw);
    }

    if (TryAcquireSRWLockExclusive(&g_lock))
    {
        ComScope com;
        Cleanup(true);
        ReleaseSRWLockExclusive(&g_lock);
    }

    return S_OK;
}

// ---------------------------------------------------------------------------------------------------------- provider

namespace {

class EmptyEnum : public IVssEnumObject
{
public:
    virtual ~EmptyEnum() = default;

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (ppv == nullptr)
        {
            return E_POINTER;
        }

        if (riid == __uuidof(IUnknown) || riid == __uuidof(IVssEnumObject))
        {
            *ppv = static_cast<IVssEnumObject*>(this);
            AddRef();
            return S_OK;
        }

        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_refs); }

    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG refs = InterlockedDecrement(&_refs);
        if (refs == 0)
        {
            delete this;
        }

        return refs;
    }

    STDMETHODIMP Next(ULONG celt, VSS_OBJECT_PROP*, ULONG* fetched) override
    {
        if (fetched != nullptr)
        {
            *fetched = 0;
        }

        return celt == 0 ? S_OK : S_FALSE;
    }

    STDMETHODIMP Skip(ULONG celt) override { return celt == 0 ? S_OK : S_FALSE; }

    STDMETHODIMP Reset() override { return S_OK; }

    STDMETHODIMP Clone(IVssEnumObject** clone) override
    {
        if (clone == nullptr)
        {
            return E_POINTER;
        }

        *clone = new (std::nothrow) EmptyEnum();
        return *clone != nullptr ? S_OK : E_OUTOFMEMORY;
    }

private:
    LONG _refs = 1;
};

class Provider : public IVssSoftwareSnapshotProvider, public IVssProviderCreateSnapshotSet, public IVssProviderNotifications
{
public:
    Provider() { InterlockedIncrement(&g_objects); }
    virtual ~Provider() { InterlockedDecrement(&g_objects); }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (ppv == nullptr)
        {
            return E_POINTER;
        }

        if (riid == __uuidof(IUnknown) || riid == __uuidof(IVssSoftwareSnapshotProvider))
        {
            *ppv = static_cast<IVssSoftwareSnapshotProvider*>(this);
        }
        else if (riid == __uuidof(IVssProviderCreateSnapshotSet))
        {
            *ppv = static_cast<IVssProviderCreateSnapshotSet*>(this);
        }
        else if (riid == __uuidof(IVssProviderNotifications))
        {
            *ppv = static_cast<IVssProviderNotifications*>(this);
        }
        else
        {
            *ppv = nullptr;
            return E_NOINTERFACE;
        }

        AddRef();
        return S_OK;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_refs); }

    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG refs = InterlockedDecrement(&_refs);
        if (refs == 0)
        {
            delete this;
        }

        return refs;
    }

    // IVssSoftwareSnapshotProvider
    STDMETHODIMP SetContext(LONG) override { return S_OK; }

    STDMETHODIMP GetSnapshotProperties(VSS_ID, VSS_SNAPSHOT_PROP*) override { return VSS_E_OBJECT_NOT_FOUND; }

    STDMETHODIMP Query(VSS_ID, VSS_OBJECT_TYPE, VSS_OBJECT_TYPE, IVssEnumObject** ppEnum) override
    {
        if (ppEnum == nullptr)
        {
            return E_POINTER;
        }

        *ppEnum = new (std::nothrow) EmptyEnum();
        return *ppEnum != nullptr ? S_OK : E_OUTOFMEMORY;
    }

    STDMETHODIMP DeleteSnapshots(VSS_ID, VSS_OBJECT_TYPE, WINBOOL, LONG* deleted, VSS_ID* nondeleted) override
    {
        if (deleted != nullptr)
        {
            *deleted = 0;
        }

        if (nondeleted != nullptr)
        {
            *nondeleted = GUID_NULL;
        }

        return S_OK;
    }

    STDMETHODIMP BeginPrepareSnapshot(VSS_ID, VSS_ID, VSS_PWSZ, LONG) override { return S_OK; }

    // Only during backdafuckup's own backup; otherwise VSS uses the system provider for other requesters.
    STDMETHODIMP IsVolumeSupported(VSS_PWSZ, WINBOOL* supported) override
    {
        if (supported == nullptr)
        {
            return E_POINTER;
        }

        *supported = EventExists(kEventFrozen) ? TRUE : FALSE;
        return S_OK;
    }

    STDMETHODIMP IsVolumeSnapshotted(VSS_PWSZ, WINBOOL* present, LONG* compatibility) override
    {
        if (present != nullptr)
        {
            *present = FALSE;
        }

        if (compatibility != nullptr)
        {
            *compatibility = 0;
        }

        return S_OK;
    }

    STDMETHODIMP SetSnapshotProperty(VSS_ID, VSS_SNAPSHOT_PROPERTY_ID, VARIANT) override { return S_OK; }

    STDMETHODIMP RevertToSnapshot(VSS_ID) override { return E_NOTIMPL; }

    STDMETHODIMP QueryRevertStatus(VSS_PWSZ, IVssAsync**) override { return E_NOTIMPL; }

    // IVssProviderCreateSnapshotSet
    STDMETHODIMP EndPrepareSnapshots(VSS_ID) override { return S_OK; }

    STDMETHODIMP PreCommitSnapshots(VSS_ID) override { return S_OK; }

    // Inside the flush-and-hold window: the writes are held while this method runs.
    STDMETHODIMP CommitSnapshots(VSS_ID) override
    {
        HANDLE frozen = OpenEventW(EVENT_MODIFY_STATE, FALSE, kEventFrozen);
        HANDLE thaw = OpenEventW(SYNCHRONIZE, FALSE, kEventThaw);
        HANDLE timeout = OpenEventW(EVENT_MODIFY_STATE, FALSE, kEventTimeout);
        if (frozen != nullptr && thaw != nullptr)
        {
            SetEvent(frozen);
            if (WaitForSingleObject(thaw, kProviderHoldLimitMs) != WAIT_OBJECT_0 && timeout != nullptr)
            {
                SetEvent(timeout);
            }
        }

        for (HANDLE handle : {frozen, thaw, timeout})
        {
            if (handle != nullptr)
            {
                CloseHandle(handle);
            }
        }

        return S_OK;
    }

    STDMETHODIMP PostCommitSnapshots(VSS_ID, LONG) override { return S_OK; }

    STDMETHODIMP PreFinalCommitSnapshots(VSS_ID) override { return S_OK; }

    STDMETHODIMP PostFinalCommitSnapshots(VSS_ID) override { return S_OK; }

    STDMETHODIMP AbortSnapshots(VSS_ID) override { return S_OK; }

    // IVssProviderNotifications
    STDMETHODIMP OnLoad(IUnknown*) override { return S_OK; }

    STDMETHODIMP OnUnload(WINBOOL) override { return S_OK; }

private:
    LONG _refs = 1;
};

class Factory : public IClassFactory
{
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (ppv == nullptr)
        {
            return E_POINTER;
        }

        if (riid == __uuidof(IUnknown) || riid == __uuidof(IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }

        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return 2; }

    STDMETHODIMP_(ULONG) Release() override { return 1; }

    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer != nullptr)
        {
            return CLASS_E_NOAGGREGATION;
        }

        Provider* provider = new (std::nothrow) Provider();
        if (provider == nullptr)
        {
            return E_OUTOFMEMORY;
        }

        HRESULT hr = provider->QueryInterface(riid, ppv);
        provider->Release();
        return hr;
    }

    STDMETHODIMP LockServer(WINBOOL lock) override
    {
        if (lock)
        {
            InterlockedIncrement(&g_locks);
        }
        else
        {
            InterlockedDecrement(&g_locks);
        }

        return S_OK;
    }
};

Factory g_factory;

std::wstring ModulePath()
{
    wchar_t path[MAX_PATH];
    DWORD length = GetModuleFileNameW(g_module, path, MAX_PATH);
    return std::wstring(path, length);
}

std::wstring ClsidKey() { return L"CLSID\\" + GuidText(kProviderClsid); }

LSTATUS SetString(HKEY root, const std::wstring& key, const wchar_t* name, const std::wstring& value)
{
    HKEY handle = nullptr;
    LSTATUS status = RegCreateKeyExW(root, key.c_str(), 0, nullptr, 0, KEY_SET_VALUE, nullptr, &handle, nullptr);
    if (status != ERROR_SUCCESS)
    {
        return status;
    }

    status = RegSetValueExW(handle, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()), static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(handle);
    return status;
}

// ------------------------------------------------------------------------------------------------------ registration

VARIANT Text(const wchar_t* value)
{
    VARIANT variant;
    VariantInit(&variant);
    variant.vt = VT_BSTR;
    variant.bstrVal = SysAllocString(value);
    return variant;
}

VARIANT Number(LONG value)
{
    VARIANT variant;
    VariantInit(&variant);
    variant.vt = VT_I4;
    variant.lVal = value;
    return variant;
}

VARIANT Flag(bool value)
{
    VARIANT variant;
    VariantInit(&variant);
    variant.vt = VT_BOOL;
    variant.boolVal = value ? VARIANT_TRUE : VARIANT_FALSE;
    return variant;
}

HRESULT Put(ICatalogObject* object, const wchar_t* property, VARIANT value)
{
    BSTR name = SysAllocString(property);
    HRESULT hr = object->put_Value(name, value);
    SysFreeString(name);
    VariantClear(&value);
    return hr;
}

HRESULT OpenApplications(ICOMAdminCatalog2** catalog, ICatalogCollection** applications)
{
    HRESULT hr = CoCreateInstance(kComAdminCatalog, nullptr, CLSCTX_INPROC_SERVER, __uuidof(ICOMAdminCatalog2), reinterpret_cast<void**>(catalog));
    if (FAILED(hr))
    {
        return hr;
    }

    IDispatch* dispatch = nullptr;
    BSTR name = SysAllocString(L"Applications");
    hr = (*catalog)->GetCollection(name, &dispatch);
    SysFreeString(name);
    if (SUCCEEDED(hr))
    {
        hr = dispatch->QueryInterface(__uuidof(ICatalogCollection), reinterpret_cast<void**>(applications));
        dispatch->Release();
    }

    if (SUCCEEDED(hr))
    {
        hr = (*applications)->Populate();
    }

    if (FAILED(hr))
    {
        (*catalog)->Release();
        *catalog = nullptr;
    }

    return hr;
}

// Removes our COM+ application (if any), and the Windows service that hosts it.
HRESULT RemoveApplication()
{
    ICOMAdminCatalog2* catalog = nullptr;
    ICatalogCollection* applications = nullptr;
    HRESULT hr = OpenApplications(&catalog, &applications);
    if (FAILED(hr))
    {
        return hr;
    }

    BSTR app = SysAllocString(kAppName);
    catalog->DeleteServiceForApplication(app);  // fails harmlessly if there is no such application or service
    SysFreeString(app);

    LONG count = 0;
    applications->get_Count(&count);
    for (LONG i = count - 1; i >= 0; i--)
    {
        IDispatch* dispatch = nullptr;
        if (FAILED(applications->get_Item(i, &dispatch)))
        {
            continue;
        }

        ICatalogObject* application = nullptr;
        if (SUCCEEDED(dispatch->QueryInterface(__uuidof(ICatalogObject), reinterpret_cast<void**>(&application))))
        {
            VARIANT name;
            VariantInit(&name);
            if (SUCCEEDED(application->get_Name(&name)) && name.vt == VT_BSTR && name.bstrVal != nullptr && wcscmp(name.bstrVal, kAppName) == 0)
            {
                applications->Remove(i);
            }

            VariantClear(&name);
            application->Release();
        }

        dispatch->Release();
    }

    LONG changes = 0;
    hr = applications->SaveChanges(&changes);
    applications->Release();
    catalog->Release();
    return hr;
}

}  // namespace

// ------------------------------------------------------------------------------------------------------- COM exports

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (clsid != kProviderClsid)
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    return (g_objects == 0 && g_locks == 0) ? S_OK : S_FALSE;
}

STDAPI DllRegisterServer()
{
    std::wstring key = ClsidKey();
    LSTATUS status = SetString(HKEY_CLASSES_ROOT, key, nullptr, kProviderName);
    if (status == ERROR_SUCCESS)
    {
        status = SetString(HKEY_CLASSES_ROOT, key + L"\\InprocServer32", nullptr, ModulePath());
    }

    if (status == ERROR_SUCCESS)
    {
        status = SetString(HKEY_CLASSES_ROOT, key + L"\\InprocServer32", L"ThreadingModel", L"Apartment");
    }

    return HRESULT_FROM_WIN32(status);
}

STDAPI DllUnregisterServer()
{
    LSTATUS status = RegDeleteTreeW(HKEY_CLASSES_ROOT, ClsidKey().c_str());
    return status == ERROR_SUCCESS || status == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(status);
}

// ------------------------------------------------------------------------------------------------- management exports

// 0: registered with VSS, 1: not registered.
extern "C" __declspec(dllexport) int bdfu_vss_provider_state(wchar_t** message)
{
    std::wstring key = L"SYSTEM\\CurrentControlSet\\Services\\VSS\\Providers\\" + GuidText(kProviderId);
    HKEY handle = nullptr;
    LSTATUS status = RegOpenKeyExW(HKEY_LOCAL_MACHINE, key.c_str(), 0, KEY_READ, &handle);
    if (status == ERROR_SUCCESS)
    {
        RegCloseKey(handle);
        Out(message, L"registered");
        return 0;
    }

    Out(message, L"missing");
    return 1;
}

// Installs the provider as COM+ application (Local System) and registers it with VSS: the class is registered as
// in-process server of this DLL (DllRegisterServer), imported into the COM+ application, then announced to VSS.
extern "C" __declspec(dllexport) int bdfu_vss_register(const wchar_t* dllPath, wchar_t** message)
{
    (void)dllPath;  // Always this DLL: the agent calls it from its own folder.
    ComScope com;
    InitializeComSecurityOnce();

    // Start clean: an older registration of the provider or the application.
    IVssAdmin* admin = nullptr;
    HRESULT hr = CoCreateInstance(kVssCoordinator, nullptr, CLSCTX_ALL, __uuidof(IVssAdmin), reinterpret_cast<void**>(&admin));
    if (FAILED(hr))
    {
        return Fail(message, hr, L"The VSS coordinator is not available");
    }

    admin->UnregisterProvider(kProviderId);
    RemoveApplication();

    ICOMAdminCatalog2* catalog = nullptr;
    ICatalogCollection* applications = nullptr;
    hr = OpenApplications(&catalog, &applications);
    if (FAILED(hr))
    {
        admin->Release();
        return Fail(message, hr, L"The COM+ catalog is not available");
    }

    IDispatch* dispatch = nullptr;
    ICatalogObject* application = nullptr;
    hr = applications->Add(&dispatch);
    if (SUCCEEDED(hr))
    {
        hr = dispatch->QueryInterface(__uuidof(ICatalogObject), reinterpret_cast<void**>(&application));
        dispatch->Release();
    }

    std::wstring step = L"Creating the COM+ application";
    if (SUCCEEDED(hr))
    {
        Put(application, L"Name", Text(kAppName));
        Put(application, L"Description", Text(L"VSS provider of the backdafuckup agent: holds the writes while the hypervisor takes the VM snapshot"));
        Put(application, L"ApplicationAccessChecksEnabled", Flag(false));
        Put(application, L"Authentication", Number(6));      // packet privacy
        Put(application, L"AuthenticationCapability", Number(2));
        Put(application, L"ImpersonationLevel", Number(2));  // identify
        LONG changes = 0;
        hr = applications->SaveChanges(&changes);
    }

    if (application != nullptr)
    {
        application->Release();
    }

    applications->Release();
    if (SUCCEEDED(hr))
    {
        step = L"Registering the provider class";
        hr = DllRegisterServer();
    }

    if (SUCCEEDED(hr))
    {
        step = L"Importing the provider class into the COM+ application";
        BSTR app = SysAllocString(kAppName);
        BSTR clsid = SysAllocString(GuidText(kProviderClsid).c_str());
        hr = catalog->ImportComponent(app, clsid);
        SysFreeString(app);
        SysFreeString(clsid);
    }

    if (SUCCEEDED(hr))
    {
        // VSS loads the provider in this server application; it runs as a Windows service under Local System.
        step = L"Creating the Windows service for the COM+ application";
        BSTR app = SysAllocString(kAppName);
        BSTR service = SysAllocString(kServiceName);
        BSTR startType = SysAllocString(L"SERVICE_DEMAND_START");
        BSTR errorControl = SysAllocString(L"SERVICE_ERROR_NORMAL");
        BSTR dependencies = SysAllocString(L"");
        BSTR runAs = SysAllocString(L".\\localsystem");
        BSTR password = SysAllocString(L"");
        hr = catalog->CreateServiceForApplication(app, service, startType, errorControl, dependencies, runAs, password, VARIANT_FALSE);
        for (BSTR value : {app, service, startType, errorControl, dependencies, runAs, password})
        {
            SysFreeString(value);
        }
    }

    catalog->Release();
    if (SUCCEEDED(hr))
    {
        step = L"Registering the provider with VSS";
        BSTR name = SysAllocString(kProviderName);
        BSTR version = SysAllocString(kProviderVersion);
        hr = admin->RegisterProvider(kProviderId, kProviderClsid, name, VSS_PROV_SOFTWARE, version, kProviderVersionId);
        SysFreeString(name);
        SysFreeString(version);
        if (hr == VSS_E_PROVIDER_ALREADY_REGISTERED)
        {
            hr = S_OK;
        }
    }

    admin->Release();
    if (FAILED(hr))
    {
        RemoveApplication();
        return Fail(message, hr, step + L" failed");
    }

    Out(message, L"registered");
    return S_OK;
}

extern "C" __declspec(dllexport) int bdfu_vss_unregister(wchar_t** message)
{
    ComScope com;
    IVssAdmin* admin = nullptr;
    HRESULT hr = CoCreateInstance(kVssCoordinator, nullptr, CLSCTX_ALL, __uuidof(IVssAdmin), reinterpret_cast<void**>(&admin));
    if (SUCCEEDED(hr))
    {
        hr = admin->UnregisterProvider(kProviderId);
        admin->Release();
        if (hr == VSS_E_PROVIDER_NOT_REGISTERED)
        {
            hr = S_OK;
        }
    }

    HRESULT removed = RemoveApplication();
    DllUnregisterServer();
    if (FAILED(hr))
    {
        return Fail(message, hr, L"Unregistering the provider from VSS failed");
    }

    if (FAILED(removed))
    {
        return Fail(message, removed, L"Removing the COM+ application failed");
    }

    Out(message, L"removed");
    return S_OK;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }

    return TRUE;
}
