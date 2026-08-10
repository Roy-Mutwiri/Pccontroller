namespace TradeFix.Protocol.Messages;

public sealed record PairResponsePayload
{
    public required bool Approved { get; init; }
    public string? NodeId { get; init; }
    public string? SessionToken { get; init; }
    public string? Reason { get; init; }
}
