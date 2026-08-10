using System.Net.WebSockets;
using TradeFix.Network.Transport;

namespace TradeFix.Network.Client;

public static class WebSocketTransportFactory
{
    public static Func<CancellationToken, Task<IMessageTransport>> ForMaster(string host, int port) =>
        async cancellationToken =>
        {
            var socket = new ClientWebSocket();
            var uri = new Uri($"ws://{host}:{port}/ws/");
            await socket.ConnectAsync(uri, cancellationToken);
            return new WebSocketMessageTransport(socket);
        };
}
