using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeFix.Protocol;

public static class ProtocolSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(Envelope envelope) => JsonSerializer.Serialize(envelope, Options);

    public static Envelope? Deserialize(string json) => JsonSerializer.Deserialize<Envelope>(json, Options);

    public static T? ReadPayload<T>(this Envelope envelope) =>
        envelope.Payload.ValueKind == JsonValueKind.Undefined
            ? default
            : envelope.Payload.Deserialize<T>(Options);
}
