using TradeFix.Common.Logging;
using TradeFix.Database.Repositories;
using TradeFix.Network.Auth;
using TradeFix.Network.Transport;
using TradeFix.Protocol;
using TradeFix.Protocol.Messages;
using TradeFix.Shared.Enums;
using TradeFix.Shared.Models;

namespace TradeFix.Network.Server;

/// <summary>
/// Master-side handler for one connected node's transport: handshake (Hello), pairing or
/// reconnect-auth, then heartbeat processing. One instance per connection; disposed when the
/// transport closes. See docs/PROTOCOL.md "Connection Lifecycle".
/// </summary>
public sealed class NodeSession(
    IMessageTransport transport,
    NodeRegistry registry,
    PairedNodeRepository pairedNodes,
    PairingCodeRepository pairingCodes,
    string serverName,
    string appVersion,
    ILogSink? log = null) : IAsyncDisposable
{
    public string? NodeId { get; private set; }
    public string? RemoteAddress { get; set; }

    /// <summary>Fires once, right after this session becomes authenticated (fresh pairing or a
    /// reconnect's re-auth) — the hook point for sending it a full state resync.</summary>
    public event Action<NodeSession>? Ready;

    /// <summary>Pushes a command to this specific node (e.g. UPDATE_SOURCE after a Master-side
    /// drag/resize). Safe to call concurrently with the receive loop — <see cref="WebSocketMessageTransport"/>
    /// serializes concurrent sends internally.</summary>
    public Task SendAsync(Envelope envelope, CancellationToken cancellationToken) =>
        transport.SendAsync(envelope, cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await transport.SendAsync(
            Envelope.Create(CommandType.Hello, new HelloPayload
            {
                ServerName = serverName,
                ProtocolVersion = ProtocolVersion.Current,
                AppVersion = appVersion,
                RequiresPairing = true
            }),
            cancellationToken);

        try
        {
            while (transport.IsConnected)
            {
                var envelope = await transport.ReceiveAsync(cancellationToken);
                if (envelope is null)
                {
                    break;
                }

                await HandleAsync(envelope, cancellationToken);
            }
        }
        finally
        {
            if (NodeId is not null)
            {
                registry.UpdateConnectionState(NodeId, NodeConnectionState.Offline);
                log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Node, "NodeSession", $"{NodeId} disconnected"));
            }
        }
    }

    private async Task HandleAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Type)
        {
            case CommandType.PairRequest:
                await HandlePairRequestAsync(envelope, cancellationToken);
                break;

            case CommandType.AuthRequest:
                await HandleAuthRequestAsync(envelope, cancellationToken);
                break;

            case CommandType.Heartbeat:
                HandleHeartbeat(envelope);
                break;

            case CommandType.Ping:
                await transport.SendAsync(Envelope.Create(CommandType.Pong, null), cancellationToken);
                break;

            default:
                log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Network, "NodeSession",
                    $"Ignoring {envelope.Type} from unauthenticated or unhandled context (node={NodeId ?? "unauthenticated"})"));
                break;
        }
    }

    private async Task HandlePairRequestAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var request = envelope.ReadPayload<PairRequestPayload>();
        if (request is null)
        {
            return;
        }

        if (!pairingCodes.TryConsume(request.PairingCode, DateTimeOffset.UtcNow))
        {
            await transport.SendAsync(
                Envelope.Create(CommandType.PairResponse, new PairResponsePayload
                {
                    Approved = false,
                    Reason = "Invalid or expired pairing code."
                }),
                cancellationToken);
            log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Network, "NodeSession",
                $"Rejected pairing attempt from {RemoteAddress} (bad/expired code)"));
            return;
        }

        var nodeId = Guid.NewGuid().ToString("n");
        var token = SessionTokenService.GenerateToken();

        pairedNodes.Upsert(new PairedNodeRecord
        {
            NodeId = nodeId,
            Name = request.NodeName,
            Role = nameof(NodeRole.RenderNode),
            SessionTokenHash = SessionTokenService.Hash(token),
            LastKnownIp = RemoteAddress,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        });

        NodeId = nodeId;
        registry.Upsert(new NodeInfo
        {
            NodeId = nodeId,
            Name = request.NodeName,
            Role = NodeRole.RenderNode,
            IpAddress = RemoteAddress,
            OsVersion = request.OsVersion,
            AppVersion = request.AppVersion,
            ProtocolVersion = envelope.ProtocolVersionNumber,
            ConnectionState = NodeConnectionState.Online,
            LastHeartbeatAt = DateTimeOffset.UtcNow
        });

        await transport.SendAsync(
            Envelope.Create(CommandType.PairResponse, new PairResponsePayload
            {
                Approved = true,
                NodeId = nodeId,
                SessionToken = token
            }),
            cancellationToken);

        log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Node, "NodeSession",
            $"Paired new node '{request.NodeName}' as {nodeId}"));

        Ready?.Invoke(this);
    }

    private async Task HandleAuthRequestAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var request = envelope.ReadPayload<AuthRequestPayload>();
        if (request is null)
        {
            return;
        }

        var record = pairedNodes.GetByNodeId(request.NodeId);
        var valid = record is not null && SessionTokenService.Verify(request.SessionToken, record.SessionTokenHash);

        if (!valid)
        {
            await transport.SendAsync(
                Envelope.Create(CommandType.AuthResponse, new AuthResponsePayload
                {
                    Success = false,
                    Reason = "Unknown node or invalid session token."
                }),
                cancellationToken);
            log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Network, "NodeSession",
                $"Rejected auth for node {request.NodeId} from {RemoteAddress}"));
            return;
        }

        NodeId = record!.NodeId;
        pairedNodes.Upsert(record with { LastKnownIp = RemoteAddress, LastSeenAt = DateTimeOffset.UtcNow });

        registry.Upsert(new NodeInfo
        {
            NodeId = record.NodeId,
            Name = record.Name,
            Role = Enum.Parse<NodeRole>(record.Role),
            IpAddress = RemoteAddress,
            AppVersion = "unknown",
            ProtocolVersion = envelope.ProtocolVersionNumber,
            ConnectionState = NodeConnectionState.Syncing,
            LastHeartbeatAt = DateTimeOffset.UtcNow
        });

        await transport.SendAsync(
            Envelope.Create(CommandType.AuthResponse, new AuthResponsePayload { Success = true }),
            cancellationToken);

        log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Node, "NodeSession",
            $"Node {NodeId} re-authenticated"));

        Ready?.Invoke(this);
    }

    private void HandleHeartbeat(Envelope envelope)
    {
        var heartbeat = envelope.ReadPayload<HeartbeatPayload>();
        if (heartbeat is null || NodeId is null || heartbeat.NodeId != NodeId)
        {
            return;
        }

        registry.ApplyHeartbeat(NodeId, heartbeat.Metrics, heartbeat.CurrentSceneId, heartbeat.CurrentProjectVersion);
    }

    public ValueTask DisposeAsync() => transport.DisposeAsync();
}
