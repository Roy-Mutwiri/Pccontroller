namespace TradeFix.Shared.Enums;

/// <summary>
/// Lifecycle of a render node's connection to the Master, per docs/NODE_SYSTEM.md.
/// </summary>
public enum NodeConnectionState
{
    Connecting,
    Pairing,
    Online,
    Syncing,
    Synced,
    Warning,
    Error,
    Offline
}
