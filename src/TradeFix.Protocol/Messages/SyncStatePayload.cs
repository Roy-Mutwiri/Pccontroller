namespace TradeFix.Protocol.Messages;

/// <summary>Implemented in Phase 2. Reserved now so the connection-lifecycle code can reference
/// the command type when a node finishes pairing/reconnecting and needs a full state refresh.</summary>
public sealed record SyncStatePayload
{
    public required int ProjectSchemaVersion { get; init; }
    public required bool RequiresFullSync { get; init; }
}
