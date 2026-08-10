using TradeFix.Protocol;
using TradeFix.Protocol.Messages;
using TradeFix.Shared.Enums;
using TradeFix.Shared.Models;

namespace TradeFix.Protocol.Tests;

public class EnvelopeSerializationTests
{
    [Fact]
    public void RoundTrip_PreservesTypeAndPayload()
    {
        var payload = new HeartbeatPayload
        {
            NodeId = "node-123",
            ConnectionState = NodeConnectionState.Online,
            Metrics = new NodeMetrics { CpuPercent = 42.5, Fps = 60, LatencyMs = 3 },
            CurrentSceneId = "scene-main"
        };

        var envelope = Envelope.Create(CommandType.Heartbeat, payload, projectId: "proj-1");
        var json = ProtocolSerializer.Serialize(envelope);

        var deserialized = ProtocolSerializer.Deserialize(json);
        Assert.NotNull(deserialized);
        Assert.Equal(CommandType.Heartbeat, deserialized!.Type);
        Assert.Equal("proj-1", deserialized.ProjectId);
        Assert.Equal(ProtocolVersion.Current, deserialized.ProtocolVersionNumber);

        var roundTrippedPayload = deserialized.ReadPayload<HeartbeatPayload>();
        Assert.NotNull(roundTrippedPayload);
        Assert.Equal("node-123", roundTrippedPayload!.NodeId);
        Assert.Equal(42.5, roundTrippedPayload.Metrics.CpuPercent);
        Assert.Equal("scene-main", roundTrippedPayload.CurrentSceneId);
    }

    [Fact]
    public void Create_GeneratesUniqueMessageIds()
    {
        var a = Envelope.Create(CommandType.Ping, null);
        var b = Envelope.Create(CommandType.Ping, null);

        Assert.NotEqual(a.MessageId, b.MessageId);
    }
}
