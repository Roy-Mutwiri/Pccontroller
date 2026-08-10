using System.Threading.Channels;
using TradeFix.Protocol;

namespace TradeFix.Network.Transport;

/// <summary>
/// In-memory transport pair used by Simulation Mode (spec section 47) so simulated nodes flow
/// through the identical NodeSession / AgentConnection code as a real WebSocket would.
/// </summary>
public sealed class InProcessMessageTransport : IMessageTransport
{
    private readonly Channel<Envelope> _inbound;
    private readonly Channel<Envelope> _outbound;
    private volatile bool _connected = true;

    private InProcessMessageTransport(Channel<Envelope> inbound, Channel<Envelope> outbound)
    {
        _inbound = inbound;
        _outbound = outbound;
    }

    public static (InProcessMessageTransport Server, InProcessMessageTransport Client) CreatePair()
    {
        var serverToClient = Channel.CreateUnbounded<Envelope>();
        var clientToServer = Channel.CreateUnbounded<Envelope>();

        var server = new InProcessMessageTransport(clientToServer, serverToClient);
        var client = new InProcessMessageTransport(serverToClient, clientToServer);
        return (server, client);
    }

    public bool IsConnected => _connected;

    public async Task SendAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Transport is closed.");
        }

        await _outbound.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Envelope?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        _outbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
