namespace TradeFix.Shared.Models;

/// <summary>
/// Per-node output configuration. Implemented in Phase 8 (see docs/OUTPUT.md); the schema is
/// defined now so projects serialize forward-compatibly.
/// </summary>
public sealed record OutputSettings
{
    public bool ObsIntegrationEnabled { get; init; }
    public string? ObsWebSocketUrl { get; init; }
    public bool VirtualCameraEnabled { get; init; }
}
