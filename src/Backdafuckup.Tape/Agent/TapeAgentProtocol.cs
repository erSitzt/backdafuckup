using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backdafuckup.Tape.Scsi;

namespace Backdafuckup.Tape.Agent;

/// <summary>
/// The tape agent's control WebSocket. Every message is binary: a 4-byte big-endian length, a JSON header of that
/// length, then the data of the command (WRITE data to the agent, READ data back). The agent forwards SCSI commands of
/// devices it reported itself; everything else (what to send, retries, positions) stays on the server.
/// </summary>
public static class TapeAgentProtocol
{
    public const int Version = 1;

    /// <summary>Largest data of one command (a block of up to 1 MiB) plus room for the header.</summary>
    public const int MaxMessage = (1024 * 1024) + (64 * 1024);

    public static string ControlPath(Guid agentId) => $"api/agent/tape-hosts/{agentId:D}/control";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static async Task SendAsync(WebSocket socket, SemaphoreSlim sendLock, TapeAgentMessage header, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header, Json);
        var message = new byte[4 + json.Length + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(message, json.Length);
        json.CopyTo(message, 4);
        data.Span.CopyTo(message.AsSpan(4 + json.Length));
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>The next message, or null when the other side closed.</summary>
    public static async Task<(TapeAgentMessage Header, byte[] Data)?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (message.Length + result.Count > MaxMessage)
            {
                throw new InvalidDataException($"A tape agent message is larger than {MaxMessage} bytes.");
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        var bytes = message.GetBuffer().AsSpan(0, (int)message.Length);
        if (bytes.Length < 4 || BinaryPrimitives.ReadInt32BigEndian(bytes) is var length && (length < 2 || 4 + length > bytes.Length))
        {
            throw new InvalidDataException("A tape agent message has no valid header.");
        }

        var header = JsonSerializer.Deserialize<TapeAgentMessage>(bytes.Slice(4, length), Json) ?? throw new InvalidDataException("A tape agent message has an empty header.");
        return (header, bytes[(4 + length)..].ToArray());
    }
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
}
