using System.Text.Json;
using TradeFix.Shared.Enums;

namespace TradeFix.Shared.Models;

/// <summary>
/// A logical, node-agnostic definition of a source. Machine-specific concerns (which physical
/// camera, which monitor, which window) live in <see cref="DeviceMapping"/>, never here.
/// </summary>
public sealed record SourceDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required SourceType Type { get; init; }
    public SourceCategory Category => Type is SourceType.Camera or SourceType.Microphone
        or SourceType.DisplayCapture or SourceType.WindowCapture
        ? SourceCategory.DeviceMapped
        : SourceCategory.Logical;

    public bool Enabled { get; init; } = true;
    public Transform2D Transform { get; init; } = new();
    public AudioSourceConfig? Audio { get; init; }

    /// <summary>Type-specific configuration payload (e.g. URL for Browser, file asset id for Video).</summary>
    public JsonElement Config { get; init; }

    public IReadOnlyList<SourceFilter> Filters { get; init; } = [];
    public string? GroupId { get; init; }

    /// <summary>Per-node explicit overrides, keyed by NodeId. See <see cref="NodeOverride"/>.</summary>
    public IReadOnlyDictionary<string, NodeOverride> NodeOverrides { get; init; } =
        new Dictionary<string, NodeOverride>();
}
