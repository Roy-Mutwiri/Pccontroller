namespace TradeFix.Shared.Models;

public sealed record SceneDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public int Order { get; init; }
    public IReadOnlyList<SceneSourceRef> Sources { get; init; } = [];
}
