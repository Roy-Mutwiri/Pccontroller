using TradeFix.Shared.Enums;
using TradeFix.Shared.Models;

namespace TradeFix.Protocol.Messages;

/// <summary>Sent periodically by an Agent (and internally by Master for itself) to report liveness and metrics.</summary>
public sealed record HeartbeatPayload
{
    public required string NodeId { get; init; }
    public required NodeConnectionState ConnectionState { get; init; }
    public required NodeMetrics Metrics { get; init; }
    public string? CurrentSceneId { get; init; }
    public int? CurrentProjectVersion { get; init; }
}
