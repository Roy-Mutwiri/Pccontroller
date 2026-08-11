using System.Collections.Concurrent;
using System.Net.WebSockets;
using TradeFix.Common.Logging;

namespace TradeFix.Network.Media;

/// <summary>
/// Relays binary video/audio frames from a capture source on Master to every subscribed node, over
/// a connection deliberately separate from the JSON control channel (spec section 38: "Separate
/// CONTROL CHANNEL and MEDIA/PREVIEW CHANNEL"). One MediaHub per Master; sourceId identifies
/// which live capture a subscriber wants (a node may subscribe to several).
///
/// Each subscriber has its own bounded queue and a dedicated background sender —
/// <see cref="BroadcastFrameAsync"/> itself never waits on a socket write, so one slow node can
/// never stall capture or other nodes. The queue is bounded by BYTES, not message count
/// (<paramref name="subscriberQueueBudgetBytes"/>), because bytes are what actually govern the
/// two failure modes this protects against:
///
/// - Backlog latency: whatever is queued must cross the subscriber's link before anything newer —
///   a byte budget IS a latency budget (e.g. 384KB ≈ 0.4s on an 8Mbps link), where a fixed
///   message count was either seconds of standing lag for large messages or nothing for small.
/// - Payload semantics: H.264 stream chunks are small and consecutive (drops corrupt until the
///   next keyframe), so bursts should buffer; JPEG frames are large and independent (only the
///   newest matters), so with a byte budget a big JPEG naturally evicts everything older —
///   latest-frame-wins falls out of the same rule with no mode switch. Budget 0 means strict
///   latest-wins (used for audio, whose receiver silence-fills any gap).
///
/// Overflow evicts the OLDEST data message (and fires <see cref="FrameDropped"/>, driving
/// <c>AdaptiveEncodingController</c>) — but never a control message: H.264 restart markers
/// (posted with <c>neverDrop: true</c>) must survive congestion, because a lost marker leaves the
/// subscriber's decoder locked to a stale resolution with no self-heal (measured; see
/// H264StreamProtocol).
/// </summary>
public sealed class MediaHub(ILogSink? log = null, int subscriberQueueBudgetBytes = 0)
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<WebSocket, SubscriberPump>> _subscribers = new();

    public void RegisterSubscriber(string sourceId, WebSocket socket)
    {
        var set = _subscribers.GetOrAdd(sourceId, _ => new ConcurrentDictionary<WebSocket, SubscriberPump>());
        set[socket] = new SubscriberPump(socket, subscriberQueueBudgetBytes, log, sourceId, () => RemoveSubscriber(sourceId, socket));
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

    /// <summary>Fires when a broadcast frame had to evict one a subscriber hadn't finished
    /// sending yet — the real signal that a subscriber's link can't keep up with the current
    /// encode settings, used to drive <c>TradeFix.Master.Services.AdaptiveEncodingController</c>.
    /// Fired at most once per <see cref="BroadcastFrameAsync"/> call, even if several subscribers
    /// of the same source dropped a frame on the same call.</summary>
    public event Action<string>? FrameDropped;

    /// <summary>Hands the frame to every current subscriber's queue and returns immediately —
    /// never waits on a network send. <paramref name="neverDrop"/> marks a control message
    /// (e.g. an H.264 restart marker) that congestion must not evict; it also doesn't count
    /// against the byte budget.</summary>
    public Task BroadcastFrameAsync(string sourceId, ReadOnlyMemory<byte> frameBytes, CancellationToken cancellationToken, bool neverDrop = false)
    {
        if (_subscribers.TryGetValue(sourceId, out var set))
        {
            var anyDropped = false;
            foreach (var entry in set)
            {
                if (entry.Value.Post(frameBytes, neverDrop))
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
        private readonly long _budgetBytes;
        private readonly object _sync = new();
        private readonly LinkedList<(ReadOnlyMemory<byte> Frame, bool IsControl)> _queue = new();
        private long _queuedDataBytes;
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _cts = new();

        public SubscriberPump(WebSocket socket, int budgetBytes, ILogSink? log, string sourceId, Action onDead)
        {
            _socket = socket;
            _budgetBytes = Math.Max(0, budgetBytes);
            _ = Task.Run(() => PumpAsync(log, sourceId, onDead));
        }

        /// <returns>true if admitting this frame evicted unsent data (i.e. this subscriber is
        /// falling behind at the current data rate).</returns>
        public bool Post(ReadOnlyMemory<byte> frame, bool isControl)
        {
            var evictedAnything = false;
            lock (_sync)
            {
                if (!isControl)
                {
                    // Evict oldest DATA until this frame fits the byte budget. Control messages
                    // are skipped over and always survive. With budget 0 this clears all queued
                    // data — strict latest-wins.
                    while (_queuedDataBytes > 0 && _queuedDataBytes + frame.Length > _budgetBytes)
                    {
                        var node = _queue.First;
                        while (node is not null && node.Value.IsControl)
                        {
                            node = node.Next;
                        }

                        if (node is null)
                        {
                            break;
                        }

                        _queuedDataBytes -= node.Value.Frame.Length;
                        _queue.Remove(node);
                        evictedAnything = true;
                    }

                    _queuedDataBytes += frame.Length;
                }

                _queue.AddLast((frame, isControl));
            }

            _signal.Release();
            return evictedAnything;
        }

        public void Stop() => _cts.Cancel();

        private async Task PumpAsync(ILogSink? log, string sourceId, Action onDead)
        {
            try
            {
                while (true)
                {
                    await _signal.WaitAsync(_cts.Token);

                    ReadOnlyMemory<byte> frame;
                    lock (_sync)
                    {
                        if (_queue.First is not { } first)
                        {
                            continue; // its frame was evicted after the signal — nothing to send
                        }

                        frame = first.Value.Frame;
                        if (!first.Value.IsControl)
                        {
                            _queuedDataBytes -= frame.Length;
                        }

                        _queue.RemoveFirst();
                    }

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
