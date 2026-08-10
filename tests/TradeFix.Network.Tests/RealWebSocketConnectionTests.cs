using Microsoft.Data.Sqlite;
using TradeFix.Database;
using TradeFix.Database.Migrations;
using TradeFix.Database.Repositories;
using TradeFix.Network.Auth;
using TradeFix.Network.Client;
using TradeFix.Network.Metrics;
using TradeFix.Network.Server;
using TradeFix.Shared.Enums;

namespace TradeFix.Network.Tests;

/// <summary>
/// Exercises the real WebSocket transport (HttpListener server + ClientWebSocket client) end to
/// end, as opposed to <see cref="NodeSimulatorIntegrationTests"/> which uses the in-process
/// transport. This is the same code path a physical PC2/PC3 Agent uses to reach the Master.
/// </summary>
public sealed class RealWebSocketConnectionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tradefix-ws-test-{Guid.NewGuid():n}.db");

    [Fact]
    public async Task RealAgentConnection_PairsOverLoopbackWebSocket_AndReachesOnline()
    {
        var factory = new SqliteConnectionFactory(_dbPath);
        using (var connection = factory.Open())
        {
            Migrator.Apply(connection);
        }

        var registry = new NodeRegistry();
        var pairedNodes = new PairedNodeRepository(factory);
        var pairingCodes = new PairingCodeRepository(factory);
        var pairingService = new PairingService(pairingCodes);

        var port = GetFreeTcpPort();
        await using var master = new MasterServer(registry, pairedNodes, pairingCodes, "Test Master", "1.0.0-test");
        master.Start(port, bindAllInterfaces: false);

        await using var agent = new AgentConnection(
            WebSocketTransportFactory.ForMaster("localhost", port),
            new NoopMetricsProvider(),
            "Real Loopback Agent",
            osVersion: "test-os",
            appVersion: "1.0.0-test");

        agent.Start();

        var code = pairingService.IssueCode();
        agent.SubmitPairingCode(code);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && agent.State != NodeConnectionState.Online)
        {
            await Task.Delay(100);
        }

        Assert.Equal(NodeConnectionState.Online, agent.State);
        Assert.NotNull(agent.Credentials);

        var registered = registry.All.FirstOrDefault(n => n.Name == "Real Loopback Agent");
        Assert.NotNull(registered);
        Assert.Equal(NodeConnectionState.Online, registered!.ConnectionState);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class NoopMetricsProvider : INodeMetricsProvider
    {
        public Shared.Models.NodeMetrics Sample() => new() { CpuPercent = 1, Fps = 60 };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
        }
    }
}
