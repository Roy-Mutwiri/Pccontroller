using System.Net.WebSockets;
using TradeFix.Network.Media;
using TradeFix.Sources.Video;

namespace TradeFix.Network.Tests;

/// <summary>
/// Coverage for <see cref="MediaHub"/>'s byte-budget subscriber queues — the H.264-era semantics
/// (production video runs with a 384KB budget; the pre-existing MediaHubBackpressureTests cover
/// the default budget-0 latest-wins behavior). The two properties that matter most in production:
/// bursts below the budget are buffered IN ORDER (dropping an H.264 chunk corrupts the picture
/// until the next keyframe), and restart markers posted as neverDrop control messages survive
/// congestion that evicts every data frame around them — a lost marker leaves a node's decoder
/// locked to a stale resolution with no self-heal.
/// </summary>
public sealed class MediaHubByteBudgetTests
{
    [Fact]
    public async Task BurstWithinBudget_IsBufferedAndDeliveredInOrder_WithNoDropSignal()
    {
        var hub = new MediaHub(subscriberQueueBudgetBytes: 1024);
        var socket = new GatedWebSocket();
        hub.RegisterSubscriber("src", socket);

        var dropped = false;
        hub.FrameDropped += _ => dropped = true;

        // First frame gets stuck in flight; five more 100-byte frames (500B < 1KB budget) queue.
        await hub.BroadcastFrameAsync("src", MakeFrame(0, 100), CancellationToken.None);
        await WaitForSendInProgress(socket);
        for (byte i = 1; i <= 5; i++)
        {
            await hub.BroadcastFrameAsync("src", MakeFrame(i, 100), CancellationToken.None);
        }

        Assert.False(dropped, "Nothing should drop while the queued bytes fit the budget");

        socket.ReleaseSend();
        await WaitForSentCount(socket, 6);

        var sent = socket.SentFrames;
        Assert.Equal(6, sent.Count);
        for (byte i = 0; i < 6; i++)
        {
            Assert.Equal(i, sent[i][0]); // strict arrival order — H.264 chunks are one stream
        }
    }

    [Fact]
    public async Task OverflowingTheBudget_EvictsTheOldestDataFirst_AndFiresFrameDropped()
    {
        var hub = new MediaHub(subscriberQueueBudgetBytes: 250);
        var socket = new GatedWebSocket();
        hub.RegisterSubscriber("src", socket);

        var dropCount = 0;
        hub.FrameDropped += _ => dropCount++;

        await hub.BroadcastFrameAsync("src", MakeFrame(0, 100), CancellationToken.None); // goes in flight
        await WaitForSendInProgress(socket);

        // 100B frames against a 250B budget: 1 and 2 queue (200B); 3 must evict 1; 4 must evict 2.
        await hub.BroadcastFrameAsync("src", MakeFrame(1, 100), CancellationToken.None);
        await hub.BroadcastFrameAsync("src", MakeFrame(2, 100), CancellationToken.None);
        Assert.Equal(0, dropCount);

        await hub.BroadcastFrameAsync("src", MakeFrame(3, 100), CancellationToken.None);
        await hub.BroadcastFrameAsync("src", MakeFrame(4, 100), CancellationToken.None);
        Assert.Equal(2, dropCount);

        socket.ReleaseSend();
        await WaitForSentCount(socket, 3);

        var sent = socket.SentFrames;
        Assert.Equal(3, sent.Count);
        Assert.Equal(0, sent[0][0]); // was already in flight
        Assert.Equal(3, sent[1][0]); // survivors, oldest-evicted-first preserved order
        Assert.Equal(4, sent[2][0]);
    }

    [Fact]
    public async Task RestartMarker_SurvivesCongestion_ThatEvictsEveryDataFrameAroundIt()
    {
        var hub = new MediaHub(subscriberQueueBudgetBytes: 120);
        var socket = new GatedWebSocket();
        hub.RegisterSubscriber("src", socket);

        await hub.BroadcastFrameAsync("src", MakeFrame(0, 100), CancellationToken.None); // in flight
        await WaitForSendInProgress(socket);

        // Old-sequence tail queues, then the marker, then a new-sequence burst big enough to
        // evict every queued data frame on both sides of the marker.
        await hub.BroadcastFrameAsync("src", MakeFrame(1, 100), CancellationToken.None);
        await hub.BroadcastFrameAsync("src", H264StreamProtocol.RestartMarker, CancellationToken.None, neverDrop: true);
        await hub.BroadcastFrameAsync("src", MakeFrame(2, 100), CancellationToken.None); // evicts 1
        await hub.BroadcastFrameAsync("src", MakeFrame(3, 100), CancellationToken.None); // evicts 2

        socket.ReleaseSend();
        await WaitForSentCount(socket, 3);

        var sent = socket.SentFrames;
        Assert.Equal(3, sent.Count);
        Assert.Equal(0, sent[0][0]);
        Assert.True(H264StreamProtocol.IsRestartMarker(sent[1]),
            "The restart marker must survive congestion — losing it strands the subscriber's decoder on a stale resolution");
        Assert.Equal(3, sent[2][0]); // marker still arrives BEFORE the new sequence's surviving data
    }

    [Fact]
    public async Task SingleFrameLargerThanTheBudget_IsStillAdmitted_AsLatestWins()
    {
        // A 4K JPEG fallback frame far exceeds the video budget — the rule must degrade to
        // latest-frame-wins, never to "frame can never be sent".
        var hub = new MediaHub(subscriberQueueBudgetBytes: 100);
        var socket = new GatedWebSocket();
        hub.RegisterSubscriber("src", socket);

        await hub.BroadcastFrameAsync("src", MakeFrame(0, 50), CancellationToken.None); // in flight
        await WaitForSendInProgress(socket);

        await hub.BroadcastFrameAsync("src", MakeFrame(1, 500), CancellationToken.None); // over budget alone
        await hub.BroadcastFrameAsync("src", MakeFrame(2, 500), CancellationToken.None); // evicts 1

        socket.ReleaseSend();
        await WaitForSentCount(socket, 2);

        var sent = socket.SentFrames;
        Assert.Equal(2, sent.Count);
        Assert.Equal(0, sent[0][0]);
        Assert.Equal(2, sent[1][0]); // only the newest oversized frame survived
    }

    private static byte[] MakeFrame(byte tag, int length)
    {
        var frame = new byte[length];
        frame[0] = tag;
        return frame;
    }

    private static async Task WaitForSendInProgress(GatedWebSocket socket)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!socket.SendInProgress && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(socket.SendInProgress, "The fake socket's send never started — test setup is broken");
    }

    private static async Task WaitForSentCount(GatedWebSocket socket, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (socket.SentFrames.Count < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>Same technique as MediaHubBackpressureTests' double (kept private per test class
    /// by that file's design): the FIRST send blocks until released, everything after flows.</summary>
    private sealed class GatedWebSocket : WebSocket
    {
        private readonly List<byte[]> _sentFrames = [];
        private readonly object _gate = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _firstSendGateOpened;

        public bool SendInProgress { get; private set; }

        public IReadOnlyList<byte[]> SentFrames
        {
            get { lock (_gate) { return _sentFrames.ToList(); } }
        }

        public void ReleaseSend() => _release.TrySetResult();

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake is send-only — MediaHub never receives from a media subscriber.");

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var shouldBlock = false;
            lock (_gate)
            {
                if (!_firstSendGateOpened)
                {
                    _firstSendGateOpened = true;
                    shouldBlock = true;
                    SendInProgress = true;
                }
            }

            if (shouldBlock)
            {
                await _release.Task;
                lock (_gate)
                {
                    SendInProgress = false;
                }
            }

            lock (_gate)
            {
                _sentFrames.Add(buffer.ToArray());
            }
        }
    }
}
