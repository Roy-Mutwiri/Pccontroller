using System.Net.WebSockets;
using Microsoft.Data.Sqlite;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TradeFix.Database;
using TradeFix.Database.Migrations;
using TradeFix.Database.Repositories;
using TradeFix.Network.Media;
using TradeFix.Network.Server;
using TradeFix.Sources.Audio;

namespace TradeFix.Network.Tests;

/// <summary>
/// The audio equivalent of <see cref="ScreenCaptureEndToEndTests"/>: plays a real, audible tone
/// through the normal Windows audio output, captures it with the real <see cref="AudioCaptureService"/>,
/// relays it through a real <see cref="MasterServer"/>'s audio channel, and asserts a real
/// WebSocket subscriber receives genuinely non-silent PCM — not just that some bytes arrived.
///
/// This exact test (with a temporary diagnostic harness, not this permanent version) is what
/// caught a real bug during development: <c>MediaFoundationResampler</c> measurably produced
/// near-silence for about the first second of every capture session regardless of quality
/// setting, before "waking up". The bug was invisible to "does it throw" testing — only checking
/// actual sample amplitudes against genuinely-playing audio caught it. See PROGRESS.md for the
/// measurements and AudioCaptureService's remarks for why the resampling approach changed.
/// </summary>
public sealed class AudioCaptureEndToEndTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tradefix-audio-e2e-{Guid.NewGuid():n}.db");

    [Fact]
    public async Task RealTone_SurvivesCaptureAndRelay_ToRealSubscriber_AsNonSilentPcm()
    {
        var factory = new SqliteConnectionFactory(_dbPath);
        using (var connection = factory.Open())
        {
            Migrator.Apply(connection);
        }

        var registry = new NodeRegistry();
        var pairedNodes = new PairedNodeRepository(factory);
        var pairingCodes = new PairingCodeRepository(factory);
        var audioHub = new MediaHub();

        var port = GetFreeTcpPort();
        await using var master = new MasterServer(registry, pairedNodes, pairingCodes, "Test Master", "1.0.0-test", audioHub: audioHub);
        master.Start(port, bindAllInterfaces: false);

        const string sourceId = "audio-e2e-source";

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://localhost:{port}/audio/{sourceId}"), CancellationToken.None);

        var subscriberReady = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < subscriberReady && audioHub.SubscriberCount(sourceId) == 0)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, audioHub.SubscriberCount(sourceId));

        var toneGenerator = new SignalGenerator(24000, 1) { Type = SignalGeneratorType.Sin, Frequency = 440, Gain = 0.8 };
        using var player = new WaveOutEvent();
        player.Init(toneGenerator);
        player.Play();

        using var capture = new AudioCaptureService(sampleRate: 24000, channels: 1);
        capture.ChunkCaptured += (bytes, timestampMs) =>
            audioHub.BroadcastFrameAsync(sourceId, AudioChunkFraming.Encode(timestampMs, bytes), CancellationToken.None);
        capture.Start();

        var chunksReceived = new List<byte[]>();
        var buffer = new byte[128 * 1024];
        var receiveDeadline = DateTime.UtcNow.AddSeconds(10);

        while (chunksReceived.Count < 5 && DateTime.UtcNow < receiveDeadline)
        {
            using var chunkStream = new MemoryStream();
            var receiveTask = ReceiveOneChunkAsync(client, buffer, chunkStream);
            var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(5)));
            if (completed != receiveTask)
            {
                break;
            }

            chunksReceived.Add(chunkStream.ToArray());
        }

        capture.Stop();
        player.Stop();

        Assert.True(chunksReceived.Count >= 5, $"Expected at least 5 real audio chunks end-to-end, got {chunksReceived.Count}");

        // Skip the very first chunk (may be a partial/quiet read as the pipeline spins up) and
        // require the rest to show genuine signal — exactly the check that would have failed
        // against the MediaFoundationResampler-based implementation.
        var sustainedChunks = chunksReceived.Skip(1)
            .Select(framed =>
            {
                Assert.True(AudioChunkFraming.TryDecode(framed, out _, out var pcm), "Chunk is missing the timestamp header");
                return pcm.ToArray();
            })
            .ToList();
        foreach (var chunk in sustainedChunks)
        {
            Assert.True(chunk.Length > 0 && chunk.Length % 2 == 0, "Chunk is not well-formed 16-bit PCM");
        }

        var peakAmplitude = sustainedChunks
            .SelectMany(chunk => Enumerable.Range(0, chunk.Length / 2).Select(i => Math.Abs((double)BitConverter.ToInt16(chunk, i * 2)) / short.MaxValue))
            .DefaultIfEmpty(0)
            .Max();

        Assert.True(peakAmplitude > 0.3, $"Peak amplitude across sustained chunks was only {peakAmplitude:P1} despite a 440Hz tone genuinely playing — audio pipeline is producing near-silence.");
    }

    private static async Task ReceiveOneChunkAsync(ClientWebSocket client, byte[] buffer, MemoryStream chunkStream)
    {
        WebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(buffer, CancellationToken.None);
            chunkStream.Write(buffer, 0, result.Count);
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
