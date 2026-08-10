using System.Net.WebSockets;
using Microsoft.Data.Sqlite;
using TradeFix.Database;
using TradeFix.Database.Migrations;
using TradeFix.Database.Repositories;
using TradeFix.Network.Media;
using TradeFix.Network.Server;

namespace TradeFix.Network.Tests;

/// <summary>
/// Proves the video relay MasterHost's capture pipeline will use: a plain WebSocket client
/// subscribes to /media/{sourceId}, and bytes broadcast via MediaHub arrive intact — the piece
/// of the live-capture feature that doesn't depend on actual screen-capture hardware/drivers and
/// so can be fully verified here (see docs/PROGRESS.md for what still needs live verification).
/// </summary>
public sealed class MediaHubIntegrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tradefix-media-test-{Guid.NewGuid():n}.db");

    [Fact]
    public async Task Subscriber_ReceivesBroadcastFrame()
    {
        var factory = new SqliteConnectionFactory(_dbPath);
        using (var connection = factory.Open())
        {
            Migrator.Apply(connection);
        }

        var registry = new NodeRegistry();
        var pairedNodes = new PairedNodeRepository(factory);
        var pairingCodes = new PairingCodeRepository(factory);
        var mediaHub = new MediaHub();

        var port = GetFreeTcpPort();
        await using var master = new MasterServer(registry, pairedNodes, pairingCodes, "Test Master", "1.0.0-test", mediaHub: mediaHub);
        master.Start(port, bindAllInterfaces: false);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://localhost:{port}/media/source-abc"), CancellationToken.None);

        // Give the server a moment to register the subscriber before we broadcast.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && mediaHub.SubscriberCount("source-abc") == 0)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, mediaHub.SubscriberCount("source-abc"));

        var frame = new byte[] { 1, 2, 3, 4, 5, 255, 0, 128 };
        await mediaHub.BroadcastFrameAsync("source-abc", frame, CancellationToken.None);

        var buffer = new byte[64];
        var result = await client.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.Equal(frame, buffer[..result.Count]);
    }

    [Fact]
    public async Task BroadcastToSourceWithNoSubscribers_DoesNotThrow()
    {
        var mediaHub = new MediaHub();
        await mediaHub.BroadcastFrameAsync("nobody-listening", new byte[] { 1, 2, 3 }, CancellationToken.None);
        // no assertion needed — just proving it doesn't throw
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
