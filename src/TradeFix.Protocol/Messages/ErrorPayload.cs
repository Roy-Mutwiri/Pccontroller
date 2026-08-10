namespace TradeFix.Protocol.Messages;

public sealed record ErrorPayload
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}
