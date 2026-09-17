using System.Buffers;
using System.Net.WebSockets;

namespace Rc.Protocol;

/// <summary>
/// Wire envelope: every WebSocket message is <c>[1 byte MsgType][payload]</c>.
/// Control payloads are UTF-8 JSON; frame payloads use <see cref="FrameCodec"/>.
/// </summary>
public static class WsFraming
{
    private const int MaxMessageBytes = 64 * 1024 * 1024;

    public static Task SendJsonAsync<T>(WebSocket ws, byte type, T value, CancellationToken ct = default)
        => SendAsync(ws, type, ProtoJson.Serialize(value), ct);

    public static async Task SendAsync(WebSocket ws, byte type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var rented = ArrayPool<byte>.Shared.Rent(payload.Length + 1);
        try
        {
            rented[0] = type;
            payload.Span.CopyTo(rented.AsSpan(1));
            await ws.SendAsync(rented.AsMemory(0, payload.Length + 1), WebSocketMessageType.Binary, true, ct)
                    .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Receives one complete application message. Returns null when the peer closed the socket.
    /// The returned buffer is freshly allocated and owned by the caller.
    /// </summary>
    public static async ValueTask<(byte Type, byte[] Payload)?> ReceiveAsync(WebSocket ws, CancellationToken ct = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                ms.Write(buffer, 0, result.Count);
                if (ms.Length > MaxMessageBytes)
                {
                    throw new InvalidOperationException($"Incoming message exceeded {MaxMessageBytes} bytes.");
                }
            }
            while (!result.EndOfMessage);

            var all = ms.ToArray();
            if (all.Length == 0)
            {
                throw new InvalidOperationException("Received an empty message.");
            }
            return (all[0], all.Length == 1 ? [] : all[1..]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads a JSON control payload from an already-received message.</summary>
    public static T? ReadJson<T>(byte[] payload) => ProtoJson.Deserialize<T>(payload);
}
