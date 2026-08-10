using System.Net;
using System.Net.WebSockets;
using TradeFix.Assets;
using TradeFix.Common.Logging;
using TradeFix.Database.Repositories;
using TradeFix.Network.Media;
using TradeFix.Network.Transport;
using TradeFix.Protocol;

namespace TradeFix.Network.Server;

/// <summary>
/// Listens for Agent WebSocket connections on the LAN (spec section 9/29: LAN-first, no public
/// exposure by default). Built on <see cref="HttpListener"/> rather than a full ASP.NET Core
/// host to keep the control-channel server dependency-free and embeddable directly inside the
/// WPF Master process.
/// </summary>
public sealed class MasterServer(
    NodeRegistry registry,
    PairedNodeRepository pairedNodes,
    PairingCodeRepository pairingCodes,
    string serverName,
    string appVersion,
    ILogSink? log = null,
    AssetStore? assetStore = null,
    MediaHub? mediaHub = null,
    MediaHub? audioHub = null) : IAsyncDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private readonly List<NodeSession> _sessions = [];
    private readonly object _sessionsLock = new();

    public int Port { get; private set; }
    public MediaHub? MediaHub => mediaHub;
    public MediaHub? AudioHub => audioHub;

    /// <summary>Fires whenever any node (real or simulated) becomes authenticated — the hook
    /// point for pushing it a full state resync (e.g. the current scene's sources).</summary>
    public event Action<NodeSession>? SessionReady;

    /// <summary>
    /// Starts listening for Agent connections. Binding to all interfaces (the LAN-facing default)
    /// requires either an elevated process or a one-time URL ACL reservation on Windows —
    /// see docs/NODE_SYSTEM.md "First-Time Network Setup". Pass <paramref name="bindAllInterfaces"/>
    /// = false (localhost-only) for local development/tests where no reservation is available.
    /// </summary>
    public void Start(int port, bool bindAllInterfaces = true)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("Server already started.");
        }

        var listener = new HttpListener();
        var host = bindAllInterfaces ? "+" : "localhost";
        listener.Prefixes.Add($"http://{host}:{port}/ws/");
        listener.Prefixes.Add($"http://{host}:{port}/assets/");
        listener.Prefixes.Add($"http://{host}:{port}/media/");
        listener.Prefixes.Add($"http://{host}:{port}/audio/");
        listener.Start(); // may throw HttpListenerException; _listener is only assigned on success so a retry can follow.

        Port = port;
        _listener = listener;
        _cts = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(_cts.Token);

        log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Network, "MasterServer", $"Listening on port {port}"));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener!.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Network, "MasterServer", "Accept failed", ex.ToString()));
                continue;
            }

            _ = HandleConnectionAsync(context, cancellationToken);
        }
    }

    private async Task HandleConnectionAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (context.Request.Url?.AbsolutePath.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase) == true)
        {
            ServeAsset(context);
            return;
        }

        if (context.Request.Url?.AbsolutePath.StartsWith("/media/", StringComparison.OrdinalIgnoreCase) == true)
        {
            await HandleBinarySubscriberAsync(context, mediaHub, "video", cancellationToken);
            return;
        }

        if (context.Request.Url?.AbsolutePath.StartsWith("/audio/", StringComparison.OrdinalIgnoreCase) == true)
        {
            await HandleBinarySubscriberAsync(context, audioHub, "audio", cancellationToken);
            return;
        }

        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
        var transport = new WebSocketMessageTransport(wsContext.WebSocket);
        var session = new NodeSession(transport, registry, pairedNodes, pairingCodes, serverName, appVersion, log)
        {
            RemoteAddress = context.Request.RemoteEndPoint?.Address.ToString()
        };
        session.Ready += s => SessionReady?.Invoke(s);

        AddSession(session);
        try
        {
            await session.RunAsync(cancellationToken);
        }
        finally
        {
            RemoveSession(session);
            await session.DisposeAsync();
        }
    }

    /// <summary>Serves GET /assets/{hash} — how a node fetches a file it doesn't have cached yet
    /// (spec section 16). Deliberately plain HTTP, not part of the WebSocket protocol: large
    /// binary transfer doesn't belong multiplexed over the same JSON-envelope control channel.</summary>
    private void ServeAsset(HttpListenerContext context)
    {
        try
        {
            var hash = context.Request.Url!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            var filePath = hash is not null ? assetStore?.GetFilePath(hash) : null;

            if (filePath is null || !File.Exists(filePath))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            context.Response.ContentType = "application/octet-stream";
            using var fileStream = File.OpenRead(filePath);
            context.Response.ContentLength64 = fileStream.Length;
            fileStream.CopyTo(context.Response.OutputStream);
            context.Response.Close();
        }
        catch (Exception ex)
        {
            log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Network, "MasterServer", "Failed to serve asset", ex.ToString()));
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
                // response may already be unusable; nothing more we can do
            }
        }
    }

    /// <summary>Handles GET /media/{sourceId} or /audio/{sourceId} — a node subscribing to a
    /// live capture's video frames or audio chunks. Deliberately plain WebSockets carrying raw
    /// binary data, separate from each other and from the JSON control channel's Envelope
    /// protocol (spec section 38) — video, audio, and control are three independent channels.</summary>
    private async Task HandleBinarySubscriberAsync(HttpListenerContext context, MediaHub? hub, string kind, CancellationToken cancellationToken)
    {
        if (!context.Request.IsWebSocketRequest || hub is null)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        var sourceId = context.Request.Url!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (sourceId is null)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
        var socket = wsContext.WebSocket;
        hub.RegisterSubscriber(sourceId, socket);
        log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Media, "MasterServer", $"{kind} subscriber joined for {sourceId}"));

        try
        {
            // This connection only ever receives data; block here until the client disconnects.
            var buffer = new byte[16];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            // subscriber disconnected abnormally; fall through to cleanup
        }
        finally
        {
            hub.RemoveSubscriber(sourceId, socket);
            log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Media, "MasterServer", $"{kind} subscriber left for {sourceId}"));
        }
    }

    /// <summary>Accepts an already-established transport directly, bypassing HttpListener. Used
    /// by Simulation Mode to run fake nodes through the exact same session logic in-process.</summary>
    public async Task AcceptTransportAsync(IMessageTransport transport, string remoteAddress, CancellationToken cancellationToken)
    {
        var session = new NodeSession(transport, registry, pairedNodes, pairingCodes, serverName, appVersion, log)
        {
            RemoteAddress = remoteAddress
        };
        session.Ready += s => SessionReady?.Invoke(s);

        AddSession(session);
        try
        {
            await session.RunAsync(cancellationToken);
        }
        finally
        {
            RemoveSession(session);
            await session.DisposeAsync();
        }
    }

    /// <summary>Sends a command to every currently-connected (paired or re-authenticated) node,
    /// including simulated ones — the same call site used for real and Simulation Mode nodes.
    /// Failures for one node don't block delivery to the others.</summary>
    public async Task BroadcastAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        NodeSession[] snapshot;
        lock (_sessionsLock)
        {
            snapshot = _sessions.Where(s => s.NodeId is not null).ToArray();
        }

        foreach (var session in snapshot)
        {
            try
            {
                await session.SendAsync(envelope, cancellationToken);
            }
            catch (Exception ex)
            {
                log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Network, "MasterServer",
                    $"Broadcast to {session.NodeId} failed", ex.ToString()));
            }
        }
    }

    private void AddSession(NodeSession session)
    {
        lock (_sessionsLock)
        {
            _sessions.Add(session);
        }
    }

    private void RemoveSession(NodeSession session)
    {
        lock (_sessionsLock)
        {
            _sessions.Remove(session);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch
            {
                // shutting down
            }
        }
    }
}
