using TradeFix.Shared.Enums;

namespace TradeFix.Shared.Models;

/// <summary>Master's authoritative view of one node (itself included as the Master node). Spec sections 7/31.</summary>
public sealed record NodeInfo
{
    public required string NodeId { get; init; }
    public required string Name { get; init; }
    public required NodeRole Role { get; init; }
    public string? IpAddress { get; init; }
    public string? OsVersion { get; init; }
    public required string AppVersion { get; init; }
    public required int ProtocolVersion { get; init; }
    public NodeConnectionState ConnectionState { get; init; } = NodeConnectionState.Offline;
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    public string? CurrentSceneId { get; init; }
    public int? CurrentProjectVersion { get; init; }
    public NodeMetrics Metrics { get; init; } = new();
}
