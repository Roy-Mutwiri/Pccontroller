using System.Net.WebSockets;
using System.Text;
using TradeFix.Protocol;

namespace TradeFix.Network.Transport;

/// <summary>
/// Wraps a .NET <see cref="WebSocket"/> (works for both the Master's HttpListener-accepted
/// server-side socket and the Agent's <see cref="ClientWebSocket"/>) as an <see cref="IMessageTransport"/>.
/// Frames are newline-free UTF8 JSON text messages, one Envelope per message.
/// </summary>
public sealed class WebSocketMessageTransport(WebSocket socket) : IMessageTransport
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => socket.State == WebSocketState.Open;

    public async Task SendAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var json = ProtocolSerializer.Serialize(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<Envelope?> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            buffer.Write(chunk, 0, result.Count);
            if (buffer.Length > MaxMessageBytes)
            {
                throw new InvalidOperationException("Message exceeds maximum allowed size.");
            }

            if (result.EndOfMessage)
            {
                break;
            }
        }

        var json = Encoding.UTF8.GetString(buffer.ToArray());
        return string.IsNullOrWhiteSpace(json) ? null : ProtocolSerializer.Deserialize(json);
    }

    public async ValueTask DisposeAsync()
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // best-effort close
            }
        }

        socket.Dispose();
        _sendLock.Dispose();
    }
}
