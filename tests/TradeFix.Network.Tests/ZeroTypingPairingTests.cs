using Microsoft.Data.Sqlite;
using TradeFix.Database;
using TradeFix.Database.Migrations;
using TradeFix.Database.Repositories;
using TradeFix.Network;
using TradeFix.Network.Client;
using TradeFix.Network.Discovery;
using TradeFix.Network.Metrics;
using TradeFix.Network.Server;
using TradeFix.Shared.Enums;

namespace TradeFix.Network.Tests;

/// <summary>
/// Real end-to-end coverage of zero-typing pairing: a real MasterServer with a discovery
/// identity, a real UDP beacon, real discovery probes, and a real AgentConnection joining with
/// an EMPTY pairing code that the operator (simulated approver) allows or declines — the whole
/// "nobody reads an IP, nobody types a code" flow.
/// </summary>
public sealed class ZeroTypingPairingTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tradefix-ztp-{Guid.NewGuid():n}.db");

    [Fact]
    public async Task Discovery_FindsARealLocalMaster_ByNameViaIdentityEndpoint()
    {
        var (server, port) = StartServer("Discovery Test Master");
        await using var _ = server;

        var found = await MasterDiscovery.FindAsync(port);

        Assert.Contains(found, m => m.Name == "Discovery Test Master" && m.Port == port);
    }

    [Fact]
    public async Task Beacon_AnswersUdpProbes_WithNameAndPort()
    {
        using var beacon = new DiscoveryBeacon("Beacon Master", 12345);

        // Give the beacon's receive loop a moment to bind before probing.
        await Task.Delay(200);
        var found = await MasterDiscovery.FindAsync(controlPort: 1); // port 1: HTTP probes all fail fast

        Assert.Contains(found, m => m.Name == "Beacon Master" && m.Port == 12345);
    }

    [Fact]
    public async Task CodelessJoin_ApprovedByOperator_ReachesOnline_WithoutAnyPairingCode()
    {
        var (server, port) = StartServer("Approving Master");
        await using var _ = server;

        var approvalRequests = new List<string>();
        server.JoinApprover = (nodeName, _) =>
        {
            approvalRequests.Add(nodeName);
            return Task.FromResult(true);
        };

        await using var agent = MakeAgent(port);
        agent.Start();
        agent.SubmitPairingCode(string.Empty); // the code-less join request

        await WaitForStateAsync(agent, NodeConnectionState.Online, TimeSpan.FromSeconds(10));
        Assert.Equal(["ZeroTypingNode"], approvalRequests);
        Assert.NotNull(agent.Credentials); // real credentials issued, same as a coded pairing
    }

    [Fact]
    public async Task CodelessJoin_Declined_DoesNotAuthenticate()
    {
        var (server, port) = StartServer("Declining Master");
        await using var _ = server;
        server.JoinApprover = (_, _) => Task.FromResult(false);

        await using var agent = MakeAgent(port);
        agent.Start();
        agent.SubmitPairingCode(string.Empty);

        // The agent must never reach Online; give it a real window to try (and fail).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            Assert.NotEqual(NodeConnectionState.Online, agent.State);
            await Task.Delay(100);
        }

        Assert.Null(agent.Credentials);
    }

    [Fact]
    public async Task CodelessJoin_WithNoApproverConfigured_IsRejectedLikeABadCode()
    {
        var (server, port) = StartServer("Codes-Only Master"); // JoinApprover left null

        await using var _ = server;
        await using var agent = MakeAgent(port);
        agent.Start();
        agent.SubmitPairingCode(string.Empty);

        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            Assert.NotEqual(NodeConnectionState.Online, agent.State);
            await Task.Delay(100);
        }

        Assert.Null(agent.Credentials);
    }

    private (MasterServer Server, int Port) StartServer(string name)
    {
        var factory = new SqliteConnectionFactory(_dbPath);
        using (var connection = factory.Open())
        {
            Migrator.Apply(connection);
        }

        var server = new MasterServer(new NodeRegistry(), new PairedNodeRepository(factory),
            new PairingCodeRepository(factory), name, "1.0.0-test");
        var port = GetFreeTcpPort();
        server.Start(port, bindAllInterfaces: false);
        return (server, port);
    }

    private static AgentConnection MakeAgent(int port) => new(
        WebSocketTransportFactory.ForMaster("localhost", port),
        new BasicNodeMetricsProvider(),
        "ZeroTypingNode",
        osVersion: "test",
        appVersion: "1.0.0-test");

    private static async Task WaitForStateAsync(AgentConnection agent, NodeConnectionState state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (agent.State != state && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Equal(state, agent.State);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
