using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using TradeFix.Common.Logging;

namespace TradeFix.Network.Media;

/// <summary>
/// Relays binary video/audio frames from a capture source on Master to every subscribed node, over
/// a connection deliberately separate from the JSON control channel (spec section 38: "Separate
/// CONTROL CHANNEL and MEDIA/PREVIEW CHANNEL"). One MediaHub per Master; sourceId identifies
/// which live capture a subscriber wants (a node may subscribe to several).
///
/// Each subscriber has its own single-slot, latest-frame-wins queue and a dedicated background
/// sender — <see cref="BroadcastFrameAsync"/> itself never waits on a socket write. This matters
/// because capture quality was raised to (near-)maximum on request (see PROGRESS.md), which makes
/// frames large enough that a slow link (e.g. Tailscale/WAN to a render node) can take noticeably
/// longer to send than the capture interval. Before this, <c>BroadcastFrameAsync</c> awaited every
/// subscriber's socket send in sequence — one slow node stalled the whole capture loop (including
/// the local preview and every other node), and frames could pile up behind it, ever-growing lag
/// as the backlog fell further behind real time. Now a slow node simply misses frames and always
/// gets shown the most current one once its link catches up — standard live-broadcast behavior
/// (the same tradeoff OBS makes when encoding falls behind: drop, don't queue).
///
/// Dropping frames fixes the lag, but it's also a live signal that the current encode settings
/// exceed what a subscriber's link can carry — <see cref="FrameDropped"/> surfaces that so
/// <c>AdaptiveEncodingController</c> can step quality/resolution down automatically instead of a
/// subscriber just perpetually missing frames at a fixed high data rate.
/// </summary>
/// <param name="subscriberQueueDepth">How many unsent messages a subscriber's queue holds before
/// the oldest is dropped. 1 (the default) is correct for independent-frame payloads (JPEG, audio
/// chunks): only the newest matters. The H.264 video pipeline passes a deeper queue instead,
/// because its messages are consecutive byte ranges of ONE continuous compressed stream — dropping
/// any of them corrupts the picture until the next keyframe, so short send bursts should be
/// absorbed rather than dropped. Sustained overload still drops (and still fires
/// <see cref="FrameDropped"/> to drive adaptive quality); the corruption then self-heals at the
/// next keyframe (≤1s).</param>
public sealed class MediaHub(ILogSink? log = null, int subscriberQueueDepth = 1)
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<WebSocket, SubscriberPump>> _subscribers = new();

    public void RegisterSubscriber(string sourceId, WebSocket socket)
    {
        var set = _subscribers.GetOrAdd(sourceId, _ => new ConcurrentDictionary<WebSocket, SubscriberPump>());
        set[socket] = new SubscriberPump(socket, subscriberQueueDepth, log, sourceId, () => RemoveSubscriber(sourceId, socket));
    }

    public void RemoveSubscriber(string sourceId, WebSocket socket)
    {
        if (_subscribers.TryGetValue(sourceId, out var set) && set.TryRemove(socket, out var pump))
        {
            pump.Stop();
        }
    }

    public int SubscriberCount(string sourceId) =>
        _subscribers.TryGetValue(sourceId, out var set) ? set.Count : 0;

    /// <summary>Fires when a broadcast frame had to replace one a subscriber hadn't finished
    /// sending yet — the real signal that a subscriber's link can't keep up with the current
    /// encode settings, used to drive <c>TradeFix.Master.Services.AdaptiveEncodingController</c>.
    /// Fired at most once per <see cref="BroadcastFrameAsync"/> call, even if several subscribers
    /// of the same source dropped a frame on the same call.</summary>
    public event Action<string>? FrameDropped;

    /// <summary>Hands the frame to every current subscriber's queue and returns immediately —
    /// never waits on a network send, so a slow subscriber can never slow down capture or other
    /// subscribers. If a subscriber hasn't finished sending the previous frame yet, this one
    /// silently replaces it (only the latest frame is ever worth sending for a live feed) and
    /// <see cref="FrameDropped"/> fires for that source.</summary>
    public Task BroadcastFrameAsync(string sourceId, ReadOnlyMemory<byte> frameBytes, CancellationToken cancellationToken)
    {
        if (_subscribers.TryGetValue(sourceId, out var set))
        {
            var anyDropped = false;
            foreach (var pump in set.Values)
            {
                if (pump.Post(frameBytes))
                {
                    anyDropped = true;
                }
            }

            if (anyDropped)
            {
                FrameDropped?.Invoke(sourceId);
            }
        }

        return Task.CompletedTask;
    }

    private sealed class SubscriberPump
    {
        private readonly WebSocket _socket;
        private readonly int _queueDepth;
        private readonly Channel<ReadOnlyMemory<byte>> _channel;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pumpTask;

        public SubscriberPump(WebSocket socket, int queueDepth, ILogSink? log, string sourceId, Action onDead)
        {
            _socket = socket;
            _queueDepth = Math.Max(1, queueDepth);
            _channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(
                new BoundedChannelOptions(_queueDepth) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
            _pumpTask = Task.Run(() => PumpAsync(log, sourceId, onDead));
        }

        /// <returns>true if this write replaced a frame the pump hadn't sent yet (i.e. this
        /// subscriber is falling behind at the current data rate).</returns>
        public bool Post(ReadOnlyMemory<byte> frame)
        {
            var droppingExisting = _channel.Reader.Count >= _queueDepth;
            _channel.Writer.TryWrite(frame);
            return droppingExisting;
        }

        public void Stop()
        {
            _cts.Cancel();
            _channel.Writer.TryComplete();
        }

        private async Task PumpAsync(ILogSink? log, string sourceId, Action onDead)
        {
            try
            {
                await foreach (var frame in _channel.Reader.ReadAllAsync(_cts.Token))
                {
                    if (_socket.State != WebSocketState.Open)
                    {
                        onDead();
                        return;
                    }

                    try
                    {
                        await _socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, _cts.Token);
                    }
                    catch (Exception ex)
                    {
                        log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Media, "MediaHub", $"Dropping dead subscriber for {sourceId}", ex.ToString()));
                        onDead();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stop() was called — normal shutdown, not an error.
            }
        }
    }
}
