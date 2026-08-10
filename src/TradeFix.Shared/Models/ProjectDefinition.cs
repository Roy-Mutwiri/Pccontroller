namespace TradeFix.Shared.Models;

/// <summary>
/// Root, versioned, serializable project schema. Spec section 4. <see cref="SchemaVersion"/>
/// is the on-disk/wire schema version, independent of <see cref="ProtocolVersion"/> in
/// TradeFix.Protocol (application-level project format vs. transport-level command format).
/// </summary>
public sealed record ProjectDefinition
{
    public const int CurrentSchemaVersion = 1;

    public required string ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<SceneDefinition> Scenes { get; init; } = [];
    public IReadOnlyList<SourceDefinition> Sources { get; init; } = [];
    public IReadOnlyList<AssetReference> Assets { get; init; } = [];
    public IReadOnlyList<DeviceMapping> DeviceMappings { get; init; } = [];
    public IReadOnlyDictionary<string, OutputSettings> OutputsByNode { get; init; } =
        new Dictionary<string, OutputSettings>();

    public string? ActiveSceneId { get; init; }
    public string? PreviewSceneId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
