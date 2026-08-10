using System.Collections.Concurrent;
using TradeFix.Shared.Enums;
using TradeFix.Shared.Models;

namespace TradeFix.Network;

/// <summary>
/// Master's authoritative, in-memory view of all nodes (including itself). The Node dashboard
/// (spec section 7/31) binds to <see cref="NodeChanged"/>. Persistence of paired identity lives
/// in TradeFix.Database; this registry is the live/runtime state.
/// </summary>
public sealed class NodeRegistry
{
    private readonly ConcurrentDictionary<string, NodeInfo> _nodes = new();

    public event Action<NodeInfo>? NodeChanged;
    public event Action<string>? NodeRemoved;

    public IReadOnlyCollection<NodeInfo> All => (IReadOnlyCollection<NodeInfo>)_nodes.Values;

    public NodeInfo? Get(string nodeId) => _nodes.GetValueOrDefault(nodeId);

    public void Upsert(NodeInfo node)
    {
        _nodes[node.NodeId] = node;
        NodeChanged?.Invoke(node);
    }

    public void UpdateConnectionState(string nodeId, NodeConnectionState state)
    {
        if (!_nodes.TryGetValue(nodeId, out var existing))
        {
            return;
        }

        var updated = existing with { ConnectionState = state };
        _nodes[nodeId] = updated;
        NodeChanged?.Invoke(updated);
    }

    public void ApplyHeartbeat(string nodeId, NodeMetrics metrics, string? currentSceneId, int? currentProjectVersion)
    {
        if (!_nodes.TryGetValue(nodeId, out var existing))
        {
            return;
        }

        var updated = existing with
        {
            Metrics = metrics,
            CurrentSceneId = currentSceneId,
            CurrentProjectVersion = currentProjectVersion,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            ConnectionState = existing.ConnectionState is NodeConnectionState.Online or NodeConnectionState.Synced
                ? existing.ConnectionState
                : NodeConnectionState.Online
        };
        _nodes[nodeId] = updated;
        NodeChanged?.Invoke(updated);
    }

    public void Remove(string nodeId)
    {
        if (_nodes.TryRemove(nodeId, out _))
        {
            NodeRemoved?.Invoke(nodeId);
        }
    }
}
