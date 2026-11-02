using System.Globalization;
using Backdafuckup.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace Backdafuckup.Web.Services;

/// <summary>Login state of the console. The layout shows setup or login until a user is signed in.</summary>
public sealed class AuthState
{
    private readonly ApiClient _api;

    public AuthState(ApiClient api)
    {
        _api = api;
        _api.Unauthorized += () =>
        {
            if (User is not null)
            {
                User = null;
                Changed?.Invoke();
            }
        };
    }

    public event Action? Changed;

    public bool Initialized { get; private set; }

    public bool SetupRequired { get; private set; }

    public CurrentUserDto? User { get; private set; }

    public async Task InitializeAsync()
    {
        SetupRequired = (await _api.GetAsync<SetupStatusDto>("api/setup/status")).SetupRequired;
        User = null;
        if (!SetupRequired)
        {
            try
            {
                User = await _api.GetAsync<CurrentUserDto>("api/auth/me");
            }
            catch (ApiException)
            {
                User = null;
            }
        }

        Initialized = true;
        Changed?.Invoke();
    }

    public async Task LoginAsync(string userName, string password)
    {
        await _api.PostAsync("api/auth/login", new LoginRequestDto(userName, password));
        await InitializeAsync();
    }

    public async Task LogoutAsync()
    {
        await _api.PostAsync("api/auth/logout");
        User = null;
        Changed?.Invoke();
    }
}

/// <summary>SignalR connection for live session progress and logs.</summary>
public sealed class LiveConnection(Microsoft.AspNetCore.Components.NavigationManager navigation) : IAsyncDisposable
{
    private HubConnection? _hub;

    public event Action<SessionSummaryDto>? SessionUpdated;

    public event Action<SessionTaskDto>? TaskUpdated;

    public event Action<LogEntryDto>? LogEntryUpserted;

    public async Task EnsureStartedAsync()
    {
        if (_hub is not null)
        {
            return;
        }

        _hub = new HubConnectionBuilder().WithUrl(navigation.ToAbsoluteUri("hubs/live")).WithAutomaticReconnect().Build();
        _hub.On<SessionSummaryDto>(nameof(ILiveClient.SessionUpdated), s => SessionUpdated?.Invoke(s));
        _hub.On<SessionTaskDto>(nameof(ILiveClient.TaskUpdated), t => TaskUpdated?.Invoke(t));
        _hub.On<LogEntryDto>(nameof(ILiveClient.LogEntryUpserted), e => LogEntryUpserted?.Invoke(e));
        await _hub.StartAsync();
    }

    public async Task JoinSessionAsync(Guid sessionId)
    {
        await EnsureStartedAsync();
        await _hub!.InvokeAsync("JoinSession", sessionId);
    }

    public async Task LeaveSessionAsync(Guid sessionId)
    {
        if (_hub is { State: HubConnectionState.Connected })
        {
            await _hub.InvokeAsync("LeaveSession", sessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
        }
    }
}

public static class Fmt
{
    public static string Bytes(long? bytes)
    {
        if (bytes is not { } b)
        {
            return "–";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = b;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string Rate(double? bytesPerSecond) => bytesPerSecond is > 0 and var rate ? Bytes((long)rate) + "/s" : "–";

    public static string Ratio(double? ratio) => ratio switch
    {
        null => "–",
        double.PositiveInfinity => "∞",
        { } r => r.ToString("0.0", CultureInfo.CurrentCulture) + "x",
    };

    public static string Percent(double share) => (share * 100).ToString("0", CultureInfo.CurrentCulture) + " %";

    public static string Seconds(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    public static string Time(DateTimeOffset? value) => value?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "–";

    public static string Duration(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null)
        {
            return "–";
        }

        var span = (end ?? DateTimeOffset.UtcNow) - start.Value;
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    public static MudBlazor.Color StateColor(string? state) => state switch
    {
        "Succeeded" or "Success" or "Available" or "Online" or "Connected" => MudBlazor.Color.Success,
        "Warning" => MudBlazor.Color.Warning,
        "Failed" or "Error" or "Offline" => MudBlazor.Color.Error,
        "Running" or "Stopping" or "Queued" => MudBlazor.Color.Info,
        _ => MudBlazor.Color.Default,
    };
}
