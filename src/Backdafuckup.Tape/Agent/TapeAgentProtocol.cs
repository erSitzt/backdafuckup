using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backdafuckup.Core.Agents;
using Backdafuckup.Tape.Scsi;

namespace Backdafuckup.Tape.Agent;

/// <summary>
/// The tape agent's control WebSocket. Every message is binary: a 4-byte big-endian length, a JSON header of that
/// length, then the data of the command (WRITE data to the agent, READ data back). The agent forwards SCSI commands of
/// devices it reported itself; everything else (what to send, retries, positions) stays on the server.
/// </summary>
public static class TapeAgentProtocol
{
    /// <summary>2: posted SCSI commands in chains (<see cref="TapeAgentMessage.Chain"/>), run in order per device.</summary>
    public const int Version = 2;

    /// <summary>The oldest agents the server still takes: they run every command on its own, without posting.</summary>
    public const int OldestVersion = 1;

    /// <summary>Largest data of one command (a block of up to 1 MiB) plus room for the header.</summary>
    public const int MaxMessage = (1024 * 1024) + (64 * 1024);

    public static string ControlPath(Guid agentId) => $"api/agent/tape-hosts/{agentId:D}/control";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static Task SendAsync(WebSocket socket, SemaphoreSlim sendLock, TapeAgentMessage header, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        AgentSocketFraming.SendAsync(socket, sendLock, header, data, Json, cancellationToken);

    /// <summary>The next message, or null when the other side closed.</summary>
    public static Task<(TapeAgentMessage Header, byte[] Data)?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken) =>
        AgentSocketFraming.ReceiveAsync<TapeAgentMessage>(socket, MaxMessage, Json, "tape agent", cancellationToken);
}

public static class TapeAgentMessageTypes
{
    /// <summary>Agent → server, first: version, host name, devices.</summary>
    public const string Hello = "hello";

    /// <summary>Server → agent: list the devices again; answered with <see cref="Devices"/>.</summary>
    public const string Discover = "discover";

    public const string Devices = "devices";

    /// <summary>Server → agent: open the device with a serial for this connection alone; answered with <see cref="Opened"/>.</summary>
    public const string Open = "open";

    public const string Opened = "opened";

    public const string Close = "close";

    public const string Closed = "closed";

    /// <summary>Server → agent: one SCSI command (data out in the message); answered with <see cref="Result"/> (data in).</summary>
    public const string Scsi = "scsi";

    public const string Result = "result";

    /// <summary>Agent → server: a request failed; <see cref="TapeAgentMessage.Transport"/> when the command did not reach the device.</summary>
    public const string Error = "error";
}

/// <summary>The JSON header of a tape agent message; which fields are set depends on <see cref="Type"/>.</summary>
public sealed record TapeAgentMessage
{
    public required string Type { get; init; }

    /// <summary>The request a reply belongs to.</summary>
    public long Id { get; init; }

    /// <summary>The open device a command is for.</summary>
    public long Handle { get; init; }

    public int Protocol { get; init; }

    public string? AgentVersion { get; init; }

    public string? HostName { get; init; }

    public IReadOnlyList<ScsiDeviceInfo>? Devices { get; init; }

    public string? Serial { get; init; }

    public string? Path { get; init; }

    public int MaxTransfer { get; init; }

    public string? Name { get; init; }

    public byte[]? Cdb { get; init; }

    public ScsiDataDirection Direction { get; init; }

    /// <summary>Size of the data buffer (for reads, what the server can take).</summary>
    public int Length { get; init; }

    public long TimeoutMs { get; init; }

    public bool Sensitive { get; init; }

    public byte Status { get; init; }

    public byte[]? Sense { get; init; }

    public int Residual { get; init; }

    public string? Error { get; init; }

    public bool Transport { get; init; }

    /// <summary>A posted command's chain: after one of its commands ended other than GOOD, the rest are not run but answered <see cref="Skipped"/>.</summary>
    public long? Chain { get; init; }

    /// <summary>With an error: the command was not run because its chain was broken before it.</summary>
    public bool Skipped { get; init; }
}
