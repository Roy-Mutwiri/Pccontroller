using TradeFix.Network.Auth;
using TradeFix.Network.Client;
using TradeFix.Network.Server;
using TradeFix.Network.Transport;

namespace TradeFix.Network.Simulation;

/// <summary>
/// A fake render node that behaves exactly like a real Agent from the Master's point of view:
/// it flows through the real pairing handshake and heartbeat loop, just over an in-process
/// transport instead of a real WebSocket. This lets the full Master ⇄ Agent flow be exercised
/// and demonstrated on a single dev PC before physical PC2/PC3 are available (spec section 47).
/// </summary>
public sealed class NodeSimulator : IAsyncDisposable
{
    private readonly AgentConnection _connection;
    private readonly CancellationTokenSource _cts = new();
    private Task? _masterSideTask;

    public string NodeName { get; }

    private NodeSimulator(string nodeName, AgentConnection connection)
    {
        NodeName = nodeName;
        _connection = connection;
    }

    public static NodeSimulator Start(MasterServer masterServer, PairingService pairingService, string nodeName, int metricsSeed)
    {
        var (server, client) = InProcessMessageTransport.CreatePair();

        var connection = new AgentConnection(
            _ => Task.FromResult<IMessageTransport>(client),
            new SimulatedMetricsProvider(metricsSeed),
            nodeName,
            osVersion: "Windows 11 (simulated)",
            appVersion: "1.0.0-sim");

        var simulator = new NodeSimulator(nodeName, connection);
        simulator._masterSideTask = masterServer.AcceptTransportAsync(server, $"127.0.0.1 (sim:{nodeName})", CancellationToken.None);

        var code = pairingService.IssueCode();
        connection.Start();
        connection.SubmitPairingCode(code);

        return simulator;
    }

    public AgentConnection Connection => _connection;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _connection.DisposeAsync();
        if (_masterSideTask is not null)
        {
            try
            {
                await _masterSideTask;
            }
            catch
            {
                // shutting down
            }
        }

        _cts.Dispose();
    }
}
