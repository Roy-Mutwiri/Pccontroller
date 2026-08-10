using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeFix.Protocol;

/// <summary>
/// Wire envelope for every control-channel message. See docs/PROTOCOL.md.
/// </summary>
public sealed record Envelope
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersionNumber { get; init; } = ProtocolVersion.Current;

    public required string MessageId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required CommandType Type { get; init; }
    public string? ProjectId { get; init; }
    public string? SceneId { get; init; }
    public JsonElement Payload { get; init; }

    public static Envelope Create(CommandType type, object? payload, string? projectId = null, string? sceneId = null)
    {
        var payloadElement = payload is null
            ? default
            : JsonSerializer.SerializeToElement(payload, ProtocolSerializer.Options);

        return new Envelope
        {
            MessageId = Guid.NewGuid().ToString("n"),
            Timestamp = DateTimeOffset.UtcNow,
            Type = type,
            ProjectId = projectId,
            SceneId = sceneId,
            Payload = payloadElement
        };
    }
}
