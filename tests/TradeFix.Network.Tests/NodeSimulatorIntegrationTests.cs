using Microsoft.Data.Sqlite;
using TradeFix.Database;
using TradeFix.Database.Migrations;
using TradeFix.Database.Repositories;
using TradeFix.Network;
using TradeFix.Network.Auth;
using TradeFix.Network.Server;
using TradeFix.Network.Simulation;
using TradeFix.Shared.Enums;

namespace TradeFix.Network.Tests;

public sealed class NodeSimulatorIntegrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tradefix-test-{Guid.NewGuid():n}.db");

    [Fact]
    public async Task SimulatedNode_PairsAndReachesOnline()
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

        await using var master = new MasterServer(registry, pairedNodes, pairingCodes, "Test Master", "1.0.0-test");

        await using var simulator = NodeSimulator.Start(master, pairingService, "Simulated PC2", metricsSeed: 1);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var node = registry.All.FirstOrDefault(n => n.Name == "Simulated PC2");
            if (node is { ConnectionState: NodeConnectionState.Online } && node.Metrics.Fps > 0)
            {
                return; // success
            }

            await Task.Delay(100);
        }

        Assert.Fail("Simulated node did not reach Online with metrics within timeout.");
    }

    [Fact]
    public async Task TwoSimulatedNodes_BothReachOnline_Independently()
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

        await using var master = new MasterServer(registry, pairedNodes, pairingCodes, "Test Master", "1.0.0-test");

        await using var pc2 = NodeSimulator.Start(master, pairingService, "Simulated PC2", metricsSeed: 2);
        await using var pc3 = NodeSimulator.Start(master, pairingService, "Simulated PC3", metricsSeed: 3);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var online = registry.All.Count(n => n.ConnectionState == NodeConnectionState.Online);
            if (online >= 2)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Both simulated nodes did not reach Online within timeout.");
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
            // best-effort cleanup of a temp file; not test-relevant.
        }
    }
}
