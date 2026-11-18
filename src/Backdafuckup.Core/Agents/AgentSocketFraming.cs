using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;

namespace Backdafuckup.Core.Agents;

/// <summary>
/// Messages of the agents' control WebSockets (tape agent, proxy agent): each one binary WebSocket message with a 4-byte
/// big-endian length, a JSON header of that length, then the message's data.
/// </summary>
public static class AgentSocketFraming
{
    public static async Task SendAsync<THeader>(
        WebSocket socket, SemaphoreSlim sendLock, THeader header, ReadOnlyMemory<byte> data, JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(sendLock);
        var encoded = JsonSerializer.SerializeToUtf8Bytes(header, json);
        var message = new byte[4 + encoded.Length + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(message, encoded.Length);
        encoded.CopyTo(message, 4);
        data.Span.CopyTo(message.AsSpan(4 + encoded.Length));
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

    /// <summary>The next message, or null when the other side closed. <paramref name="what"/> names the agent in errors.</summary>
    public static async Task<(THeader Header, byte[] Data)?> ReceiveAsync<THeader>(
        WebSocket socket, int maxMessage, JsonSerializerOptions json, string what, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (message.Length + result.Count > maxMessage)
            {
                throw new InvalidDataException($"A {what} message is larger than {maxMessage} bytes.");
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
            throw new InvalidDataException($"A {what} message has no valid header.");
        }

        var header = JsonSerializer.Deserialize<THeader>(bytes.Slice(4, length), json) ?? throw new InvalidDataException($"A {what} message has an empty header.");
        return (header, bytes[(4 + length)..].ToArray());
    }
}
