namespace TradeFix.Protocol.Messages;

public sealed record PairRequestPayload
{
    public required string PairingCode { get; init; }
    public required string NodeName { get; init; }
    public required string OsVersion { get; init; }
    public required string AppVersion { get; init; }
}
