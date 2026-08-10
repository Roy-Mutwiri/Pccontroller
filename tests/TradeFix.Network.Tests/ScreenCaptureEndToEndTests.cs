using System.Net.WebSockets;
using Microsoft.Data.Sqlite;
using TradeFix.Database;
using TradeFix.Database.Migrations;
using TradeFix.Database.Repositories;
using TradeFix.Network.Media;
using TradeFix.Network.Server;
using TradeFix.Sources.Capture;

namespace TradeFix.Network.Tests;

/// <summary>
/// The harder test the earlier <see cref="MediaHubIntegrationTests"/> deliberately didn't cover:
/// this one doesn't fake the frame data. It runs a real <see cref="ScreenCaptureService"/>
/// against this machine's actual screen, relays through a real <see cref="MasterServer"/>, and
/// asserts a real subscriber receives genuine, validly-encoded JPEG video frames end to end —
/// the same three-hop path (capture → MediaHub → media WebSocket) the Master/Agent apps use.
/// This is why "PC2 shows nothing" was diagnosable as a stale-build delivery problem rather than
/// a pipeline bug: this path is proven to work independent of either app's UI.
/// </summary>
public sealed class ScreenCaptureEndToEndTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tradefix-capture-e2e-{Guid.NewGuid():n}.db");

    [Fact]
    public async Task RealCapture_RelaysThroughRealServer_ToRealSubscriber_AsValidJpegFrames()
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

        const string sourceId = "real-capture-source";

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://localhost:{port}/media/{sourceId}"), CancellationToken.None);

        var subscriberReady = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < subscriberReady && mediaHub.SubscriberCount(sourceId) == 0)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, mediaHub.SubscriberCount(sourceId));

        using var capture = new ScreenCaptureService(targetFps: 5, maxDimension: 1280);
        capture.FrameCaptured += bytes => mediaHub.BroadcastFrameAsync(sourceId, bytes, CancellationToken.None);
        capture.Start();

        var framesReceived = new List<byte[]>();
        var receiveDeadline = DateTime.UtcNow.AddSeconds(10);
        var buffer = new byte[512 * 1024];

        while (framesReceived.Count < 3 && DateTime.UtcNow < receiveDeadline)
        {
            using var frameStream = new MemoryStream();
            var receiveTask = ReceiveOneFrameAsync(client, buffer, frameStream);
            var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(5)));
            if (completed != receiveTask)
            {
                break; // no frame arrived within this window; loop will hit the outer deadline
            }

            framesReceived.Add(frameStream.ToArray());
        }

        capture.Stop();

        Assert.True(framesReceived.Count >= 3, $"Expected at least 3 real frames end-to-end, got {framesReceived.Count}");

        foreach (var frame in framesReceived)
        {
            Assert.True(frame.Length > 1000, $"Frame suspiciously small ({frame.Length} bytes) for a real screen capture");
            Assert.True(frame is [0xFF, 0xD8, ..] && frame[^2] == 0xFF && frame[^1] == 0xD9,
                "Frame is missing valid JPEG SOI/EOI markers — not a real, intact JPEG");
        }
    }

    private static async Task ReceiveOneFrameAsync(ClientWebSocket client, byte[] buffer, MemoryStream frameStream)
    {
        WebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(buffer, CancellationToken.None);
            frameStream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
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
