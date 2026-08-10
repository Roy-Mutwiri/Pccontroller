namespace TradeFix.Protocol.Messages;

/// <summary>First message sent by Master immediately after a WebSocket connection is accepted,
/// before authentication, so the Agent can decide whether it needs to pair or can resume a
/// known session.</summary>
public sealed record HelloPayload
{
    public required string ServerName { get; init; }
    public required int ProtocolVersion { get; init; }
    public required string AppVersion { get; init; }
    public required bool RequiresPairing { get; init; }
}
