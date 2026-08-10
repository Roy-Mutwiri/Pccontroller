using System.Collections.Concurrent;
using System.Net.WebSockets;
using TradeFix.Common.Logging;

namespace TradeFix.Network.Media;

/// <summary>
/// Relays binary video frames from a capture source on Master to every subscribed node, over a
/// connection deliberately separate from the JSON control channel (spec section 38: "Separate
/// CONTROL CHANNEL and MEDIA/PREVIEW CHANNEL"). One MediaHub per Master; sourceId identifies
/// which live capture a subscriber wants (a node may subscribe to several).
/// </summary>
public sealed class MediaHub(ILogSink? log = null)
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<WebSocket, byte>> _subscribers = new();

    public void RegisterSubscriber(string sourceId, WebSocket socket)
    {
        var set = _subscribers.GetOrAdd(sourceId, _ => new ConcurrentDictionary<WebSocket, byte>());
        set[socket] = 0;
    }

    public void RemoveSubscriber(string sourceId, WebSocket socket)
    {
        if (_subscribers.TryGetValue(sourceId, out var set))
        {
            set.TryRemove(socket, out _);
        }
    }

    public int SubscriberCount(string sourceId) =>
        _subscribers.TryGetValue(sourceId, out var set) ? set.Count : 0;

    /// <summary>Sends a frame to every current subscriber of this source. Never throws — a dead
    /// socket is dropped from the subscriber set rather than failing the whole broadcast.</summary>
    public async Task BroadcastFrameAsync(string sourceId, ReadOnlyMemory<byte> frameBytes, CancellationToken cancellationToken)
    {
        if (!_subscribers.TryGetValue(sourceId, out var set) || set.IsEmpty)
        {
            return;
        }

        foreach (var socket in set.Keys)
        {
            if (socket.State != WebSocketState.Open)
            {
                set.TryRemove(socket, out _);
                continue;
            }

            try
            {
                await socket.SendAsync(frameBytes, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
            }
            catch (Exception ex)
            {
                log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Media, "MediaHub", $"Dropping dead subscriber for {sourceId}", ex.ToString()));
                set.TryRemove(socket, out _);
            }
        }
    }
}
