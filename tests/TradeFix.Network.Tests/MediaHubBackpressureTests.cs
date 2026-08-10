using System.Diagnostics;
using System.Net.WebSockets;
using TradeFix.Network.Media;

namespace TradeFix.Network.Tests;

/// <summary>
/// Regression coverage for the fix made after the user reported real video lag: raising capture
/// quality to (near-)maximum (see PROGRESS.md) made frames large enough that a slow subscriber's
/// socket send could take noticeably longer than the capture interval. The original
/// <see cref="MediaHub"/> awaited every subscriber's send in sequence inside <c>BroadcastFrameAsync</c>
/// — one slow node stalled capture for everyone. This uses a controllable fake <see cref="WebSocket"/>
/// (real network timing can't be forced deterministically) to prove the two concrete properties the
/// fix is supposed to guarantee.
/// </summary>
public sealed class MediaHubBackpressureTests
{
    [Fact]
    public async Task BroadcastFrameAsync_NeverBlocks_OnASubscriberWhoseSendHasNotCompleted()
    {
        var hub = new MediaHub();
        var socket = new GatedWebSocket();
        hub.RegisterSubscriber("src", socket);

        var frame1 = new byte[] { 1 };
        await hub.BroadcastFrameAsync("src", frame1, CancellationToken.None);

        // Give the background pump a moment to dequeue frame1 and start (and get stuck on) its send.
        var sendStarted = DateTime.UtcNow.AddSeconds(5);
        while (!socket.SendInProgress && DateTime.UtcNow < sendStarted)
        {
            await Task.Delay(20);
        }
        Assert.True(socket.SendInProgress, "The fake socket's send never started — test setup is broken");

        // The whole point of the fix: broadcasting more frames while a subscriber's send is stuck
        // must return immediately, not wait for that send to finish.
        var stopwatch = Stopwatch.StartNew();
        await hub.BroadcastFrameAsync("src", new byte[] { 2 }, CancellationToken.None);
        await hub.BroadcastFrameAsync("src", new byte[] { 3 }, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 500,
            $"BroadcastFrameAsync took {stopwatch.ElapsedMilliseconds}ms while a subscriber's send was stuck — " +
            "it should never wait on a network send (this is exactly the lag the user reported).");

        socket.ReleaseSend();
    }

    [Fact]
    public async Task SubscriberThatFallsBehind_ReceivesOnlyTheLatestFrame_NotAGrowingBacklogOfStaleOnes()
    {
        var hub = new MediaHub();
        var socket = new GatedWebSocket();
        hub.RegisterSubscriber("src", socket);

        var frame1 = new byte[] { 1 };
        var frame2 = new byte[] { 2 };
        var frame3 = new byte[] { 3 };

        await hub.BroadcastFrameAsync("src", frame1, CancellationToken.None);

        var sendStarted = DateTime.UtcNow.AddSeconds(5);
        while (!socket.SendInProgress && DateTime.UtcNow < sendStarted)
        {
            await Task.Delay(20);
        }
        Assert.True(socket.SendInProgress, "The fake socket's send never started — test setup is broken");

        // frame1's send is stuck "in flight" (simulating a slow network write already handed to the
        // socket). While stuck, two more frames arrive — a real live capture wouldn't wait around.
        await hub.BroadcastFrameAsync("src", frame2, CancellationToken.None);
        await hub.BroadcastFrameAsync("src", frame3, CancellationToken.None);

        socket.ReleaseSend();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (socket.SentFrames.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        var sent = socket.SentFrames;
        Assert.Equal(2, sent.Count); // frame1 (already committed to the wire) + frame3 (the latest) — frame2 dropped
        Assert.Equal(frame1, sent[0]);
        Assert.Equal(frame3, sent[1]);
    }

    /// <summary>Drives <c>AdaptiveEncodingController</c> on the Master side — proves MediaHub
    /// raises the signal exactly when a subscriber actually falls behind, and not merely because
    /// its previous frame is still being sent (that alone doesn't drop anything — the channel has
    /// room for one frame to wait). A drop only happens when a THIRD frame arrives while a second
    /// one is already waiting behind the one currently in flight.</summary>
    [Fact]
    public async Task FrameDropped_FiresOnlyWhenASubscriberActuallyFallsBehind()
    {
        var hub = new MediaHub();
        var socket = new GatedWebSocket();
        hub.RegisterSubscriber("src", socket);

        var droppedSourceIds = new List<string>();
        hub.FrameDropped += sourceId => droppedSourceIds.Add(sourceId);

        // frame1: nothing pending yet, subscriber is idle — no drop. Its send then gets stuck.
        await hub.BroadcastFrameAsync("src", new byte[] { 1 }, CancellationToken.None);

        var sendStarted = DateTime.UtcNow.AddSeconds(5);
        while (!socket.SendInProgress && DateTime.UtcNow < sendStarted)
        {
            await Task.Delay(20);
        }
        Assert.True(socket.SendInProgress, "The fake socket's send never started — test setup is broken");

        // frame2: frame1's send is stuck, but the channel has room for one frame to simply wait
        // its turn — not a drop yet.
        await hub.BroadcastFrameAsync("src", new byte[] { 2 }, CancellationToken.None);
        Assert.Empty(droppedSourceIds);

        // frame3: frame2 is still waiting (frame1 hasn't finished sending), so this one has to
        // overwrite frame2 — a genuine drop.
        await hub.BroadcastFrameAsync("src", new byte[] { 3 }, CancellationToken.None);
        Assert.Equal(["src"], droppedSourceIds);

        socket.ReleaseSend();
    }

    /// <summary>A WebSocket test double whose SendAsync blocks until released — simulates a
    /// subscriber whose network link is too slow to keep up, deterministically rather than relying
    /// on real (flaky, unforceable) network delay.</summary>
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

        public override void Abort()
        {
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake is send-only — MediaHub never receives from a media subscriber.");

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            // Only the very first send is gated — simulates one slow/stuck write on an otherwise-
            // live link, then the subscriber catching back up, which is the realistic scenario:
            // a link doesn't stay permanently stuck, it just falls behind and needs to skip ahead.
            if (!_firstSendGateOpened)
            {
                SendInProgress = true;
                await _release.Task;
                SendInProgress = false;
                _firstSendGateOpened = true;
            }

            lock (_gate)
            {
                _sentFrames.Add(buffer.ToArray());
            }
        }
    }
}
