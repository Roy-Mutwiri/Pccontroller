namespace TradeFix.Protocol.Messages;

/// <summary>Sent by an already-paired Agent reconnecting, to resume its session without re-pairing.</summary>
public sealed record AuthRequestPayload
{
    public required string NodeId { get; init; }
    public required string SessionToken { get; init; }
}
