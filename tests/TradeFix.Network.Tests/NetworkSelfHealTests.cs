using Microsoft.Data.Sqlite;
using TradeFix.Database;
using TradeFix.Database.Migrations;
using TradeFix.Database.Repositories;
using TradeFix.Network.Discovery;
using TradeFix.Network.Server;

namespace TradeFix.Network.Tests;

/// <summary>
/// The pieces behind the Master's self-healing network banner: parsing real Tailscale CLI status
/// shapes (so "signed out" vs "just disconnected" vs "healthy" is diagnosed correctly), and
/// MasterServer's in-place listener restart (the no-app-restart upgrade after the one-click fix).
/// </summary>
public sealed class NetworkSelfHealTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tradefix-heal-{Guid.NewGuid():n}.db");

    [Theory]
    [InlineData("Running", true, TailscaleBackendState.Running, true)]
    [InlineData("Running", false, TailscaleBackendState.Running, false)]
    [InlineData("Stopped", false, TailscaleBackendState.Stopped, false)]
    [InlineData("NeedsLogin", false, TailscaleBackendState.NeedsLogin, false)]
    [InlineData("NoState", false, TailscaleBackendState.NeedsLogin, false)]
    public void TailscaleStatus_Parses_RealCliShapes(string backendState, bool online,
        TailscaleBackendState expectedState, bool expectedOnline)
    {
        var json = $$$"""{"Version":"1.86.2","BackendState":"{{{backendState}}}","Self":{"ID":"x","Online":{{{(online ? "true" : "false")}}},"TailscaleIPs":["100.92.11.83"]},"Peer":{}}""";

        var status = TailscaleHealth.Parse(json);

        Assert.Equal(expectedState, status.State);
        Assert.Equal(expectedOnline, status.SelfOnline);
    }

    [Fact]
    public void TailscaleStatus_GarbageInput_ReportsUnknown_NotThrow()
    {
        Assert.Equal(TailscaleBackendState.Unknown, TailscaleHealth.Parse("not json at all").State);
        Assert.Equal(TailscaleBackendState.Unknown, TailscaleHealth.Parse("{}").State);
        Assert.Equal(TailscaleBackendState.Unknown, TailscaleHealth.Parse("").State);
    }

    [Fact]
    public async Task MasterServer_Restart_ServesRequestsAgain_OnTheSamePort()
    {
        var factory = new SqliteConnectionFactory(_dbPath);
        using (var connection = factory.Open())
        {
            Migrator.Apply(connection);
        }

        var server = new MasterServer(new NodeRegistry(), new PairedNodeRepository(factory),
            new PairingCodeRepository(factory), "Restart Master", "1.0.0-test");
        await using var _ = server;
        var port = GetFreeTcpPort();
        server.Start(port, bindAllInterfaces: false);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var before = await http.GetStringAsync($"http://localhost:{port}/assets/discover");
        Assert.Contains("tradefix-master", before);

        // The upgrade path can't bind all interfaces in a test (no URL ACL) — restarting back
        // into localhost mode still exercises the same teardown + rebind machinery.
        server.Restart(bindAllInterfaces: false);

        var after = await http.GetStringAsync($"http://localhost:{port}/assets/discover");
        Assert.Contains("Restart Master", after);
        Assert.Equal(port, server.Port);
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
