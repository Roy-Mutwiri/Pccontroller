using TradeFix.Shared.Models;

namespace TradeFix.Protocol.Messages;

/// <summary>
/// Carries a full replacement of one source's definition. Phase 2 broadcasts the whole
/// <see cref="SourceDefinition"/> rather than granular field deltas (MOVE_SOURCE/RESIZE_SOURCE
/// exist in the protocol catalog for a future, more bandwidth-efficient version) — simpler to
/// get correct first, and Master already debounces rapid drags before sending.
/// </summary>
public sealed record UpdateSourcePayload
{
    public required SourceDefinition Source { get; init; }
}

/// <summary>Full scene contents, sent when an Agent first connects/reconnects so it can render
/// without waiting for individual UpdateSource messages.</summary>
public sealed record LoadSceneDefinitionPayload
{
    public required SceneDefinition Scene { get; init; }
    public required IReadOnlyList<SourceDefinition> Sources { get; init; }
}
