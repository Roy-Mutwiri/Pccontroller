using Microsoft.Data.Sqlite;
using TradeFix.Database;
using TradeFix.Database.Migrations;
using TradeFix.Database.Repositories;
using TradeFix.Network.Auth;
using TradeFix.Network.Client;
using TradeFix.Network.Metrics;
using TradeFix.Network.Server;
using TradeFix.Shared.Enums;
using TradeFix.Shared.Models;

namespace TradeFix.Network.Tests;

/// <summary>
/// Mirrors exactly what TradeFix.Agent's MainViewModel.ConnectWithCode does: parse a single
/// pasted connect code into (pairingCode, host, port), connect, and auto-submit the parsed code
/// — the flow that replaced separately typing a Master IP, port, and pairing code (see
/// docs/NODE_SYSTEM.md, "single connect code").
/// </summary>
public sealed class ConnectCodeFlowIntegrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tradefix-connectcode-test-{Guid.NewGuid():n}.db");

    [Fact]
    public async Task PastedConnectCode_AutoConnectsAndAutoPairs_NoManualCodeEntry()
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

        // What Master's "Pair New Node" button now produces:
        var rawCode = pairingService.IssueCode();
        var connectCode = ConnectCode.Format(rawCode, "localhost", port);

        // What the Agent does with the pasted string — no separate IP/port/code entry:
        Assert.True(ConnectCode.TryParse(connectCode, out var parsedCode, out var parsedHost, out var parsedPort));

        await using var agent = new AgentConnection(
            WebSocketTransportFactory.ForMaster(parsedHost, parsedPort),
            new StubMetricsProvider(),
            "Auto-Paired Agent",
            osVersion: "test-os",
            appVersion: "1.0.0-test");

        agent.StateChanged += state =>
        {
            if (state == NodeConnectionState.Pairing)
            {
                agent.SubmitPairingCode(parsedCode); // exactly what MainViewModel does automatically
            }
        };

        agent.Start();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && agent.State != NodeConnectionState.Online)
        {
            await Task.Delay(100);
        }

        Assert.Equal(NodeConnectionState.Online, agent.State);
        Assert.NotNull(agent.Credentials);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class StubMetricsProvider : INodeMetricsProvider
    {
        public NodeMetrics Sample() => new() { CpuPercent = 1, Fps = 60 };
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
