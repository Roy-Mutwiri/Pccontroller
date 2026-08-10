using TradeFix.Protocol;

namespace TradeFix.Network.Transport;

/// <summary>
/// Abstraction over a bidirectional Envelope stream. Real connections implement this over a
/// WebSocket (<see cref="WebSocketMessageTransport"/>); Simulation Mode (spec section 47) uses
/// <see cref="InProcessMessageTransport"/> so the exact same session/connection code paths run
/// against fake nodes as against real ones.
/// </summary>
public interface IMessageTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    Task SendAsync(Envelope envelope, CancellationToken cancellationToken);

    /// <summary>Reads the next message. Returns null when the connection closes gracefully.</summary>
    Task<Envelope?> ReceiveAsync(CancellationToken cancellationToken);
}
