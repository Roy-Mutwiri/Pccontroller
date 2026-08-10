namespace TradeFix.Protocol.Messages;

public sealed record AuthResponsePayload
{
    public required bool Success { get; init; }
    public string? Reason { get; init; }
}
