namespace TradeFix.Shared.Models;

/// <summary>A source's placement within one scene: which layer it occupies and any scene-local transform tweak.</summary>
public sealed record SceneSourceRef
{
    public required string SourceId { get; init; }
    public int Layer { get; init; }
    public Transform2D? TransformOverride { get; init; }
}
