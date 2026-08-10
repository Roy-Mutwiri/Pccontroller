using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeFix.Database;
using TradeFix.Database.Migrations;
using TradeFix.Database.Repositories;
using TradeFix.Network.Auth;
using TradeFix.Network.Client;
using TradeFix.Network.Metrics;
using TradeFix.Network.Simulation;
using TradeFix.Protocol;
using TradeFix.Protocol.Messages;
using TradeFix.Shared.Enums;
using TradeFix.Shared.Models;

namespace TradeFix.Network.Tests;

/// <summary>
/// Proves the Master→Agent live scene sync loop MainViewModel/MasterHost drive in the real app:
/// a node already connected receives a broadcast UpdateSource, and a node that connects
/// afterwards immediately receives the current state via NodeSession.Ready/OnSessionReady
/// (the resync-on-connect path), without needing a second UpdateSource.
/// </summary>
public sealed class SourceBroadcastIntegrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tradefix-broadcast-test-{Guid.NewGuid():n}.db");

    private static SourceDefinition MakeSource(double x) => new()
    {
        Id = "demo-box-1",
        Name = "Demo Box",
        Type = SourceType.Background,
        Transform = new Transform2D { X = x, Y = 160, Width = 400, Height = 250 },
        Config = JsonSerializer.SerializeToElement(new { color = "#3E8EF7" }, ProtocolSerializer.Options)
    };

    [Fact]
    public async Task Broadcast_ReachesAlreadyConnectedNode()
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

        await using var master = new Server.MasterServer(registry, pairedNodes, pairingCodes, "Test Master", "1.0.0-test");

        await using var node = NodeSimulator.Start(master, pairingService, "Sim Node", metricsSeed: 1);

        SourceDefinition? received = null;
        node.Connection.MessageReceived += envelope =>
        {
            if (envelope.Type == CommandType.UpdateSource)
            {
                received = envelope.ReadPayload<UpdateSourcePayload>()?.Source;
            }
        };

        // wait for pairing to complete first
        var pairDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < pairDeadline && node.Connection.State != NodeConnectionState.Online)
        {
            await Task.Delay(50);
        }

        Assert.Equal(NodeConnectionState.Online, node.Connection.State);

        await master.BroadcastAsync(Envelope.Create(CommandType.UpdateSource, new UpdateSourcePayload { Source = MakeSource(500) }));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && received is null)
        {
            await Task.Delay(50);
        }

        Assert.NotNull(received);
        Assert.Equal(500, received!.Transform.X);
    }

    [Fact]
    public async Task NewlyConnectedNode_ReceivesCurrentStateViaSessionReady_WithoutSeparateBroadcast()
    {
        var factory = new SqliteConnectionFactory(_dbPath);
        using (var dbConnection = factory.Open())
        {
            Migrator.Apply(dbConnection);
        }

        var registry = new NodeRegistry();
        var pairedNodes = new PairedNodeRepository(factory);
        var pairingCodes = new PairingCodeRepository(factory);
        var pairingService = new PairingService(pairingCodes);

        var port = GetFreeTcpPort();
        await using var master = new Server.MasterServer(registry, pairedNodes, pairingCodes, "Test Master", "1.0.0-test");
        master.Start(port, bindAllInterfaces: false);

        // Simulate what MasterHost does: push current state to any session the moment it's Ready.
        master.SessionReady += session =>
            _ = session.SendAsync(
                Envelope.Create(CommandType.UpdateSource, new UpdateSourcePayload { Source = MakeSource(777) }),
                CancellationToken.None);

        // Built manually (not via NodeSimulator) so MessageReceived can be wired BEFORE Start() —
        // otherwise the SessionReady push could win the race and arrive before we're listening.
        SourceDefinition? received = null;
        await using var connection = new AgentConnection(
            WebSocketTransportFactory.ForMaster("localhost", port),
            new StubMetricsProvider(),
            "Late Node",
            osVersion: "test-os",
            appVersion: "1.0.0-test");

        connection.MessageReceived += envelope =>
        {
            if (envelope.Type == CommandType.UpdateSource)
            {
                received = envelope.ReadPayload<UpdateSourcePayload>()?.Source;
            }
        };

        connection.Start();
        connection.SubmitPairingCode(pairingService.IssueCode());

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && received is null)
        {
            await Task.Delay(50);
        }

        Assert.NotNull(received);
        Assert.Equal(777, received!.Transform.X);
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
