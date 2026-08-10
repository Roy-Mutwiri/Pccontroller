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
/// </summary>
public sealed class MediaHub(ILogSink? log = null)
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<WebSocket, SubscriberPump>> _subscribers = new();

    public void RegisterSubscriber(string sourceId, WebSocket socket)
    {
        var set = _subscribers.GetOrAdd(sourceId, _ => new ConcurrentDictionary<WebSocket, SubscriberPump>());
        set[socket] = new SubscriberPump(socket, log, sourceId, () => RemoveSubscriber(sourceId, socket));
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

    /// <summary>Hands the frame to every current subscriber's queue and returns immediately —
    /// never waits on a network send, so a slow subscriber can never slow down capture or other
    /// subscribers. If a subscriber hasn't finished sending the previous frame yet, this one
    /// silently replaces it (only the latest frame is ever worth sending for a live feed).</summary>
    public Task BroadcastFrameAsync(string sourceId, ReadOnlyMemory<byte> frameBytes, CancellationToken cancellationToken)
    {
        if (_subscribers.TryGetValue(sourceId, out var set))
        {
            foreach (var pump in set.Values)
            {
                pump.Post(frameBytes);
            }
        }

        return Task.CompletedTask;
    }

    private sealed class SubscriberPump
    {
        private readonly WebSocket _socket;
        private readonly Channel<ReadOnlyMemory<byte>> _channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pumpTask;

        public SubscriberPump(WebSocket socket, ILogSink? log, string sourceId, Action onDead)
        {
            _socket = socket;
            _pumpTask = Task.Run(() => PumpAsync(log, sourceId, onDead));
        }

        public void Post(ReadOnlyMemory<byte> frame) => _channel.Writer.TryWrite(frame);

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
