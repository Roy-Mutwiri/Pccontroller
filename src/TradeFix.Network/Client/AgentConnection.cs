using System.Threading.Channels;
using TradeFix.Common.Logging;
using TradeFix.Network.Metrics;
using TradeFix.Network.Transport;
using TradeFix.Protocol;
using TradeFix.Protocol.Messages;
using TradeFix.Shared.Enums;

namespace TradeFix.Network.Client;

/// <summary>
/// Agent-side connection state machine: connect, pair-or-authenticate, heartbeat, and
/// auto-reconnect with exponential backoff (spec section 27). The transport is created by a
/// factory delegate so the exact same state machine drives both a real WebSocket (production)
/// and an in-process transport (Simulation Mode, spec section 47).
/// </summary>
public sealed class AgentConnection(
    Func<CancellationToken, Task<IMessageTransport>> transportFactory,
    INodeMetricsProvider metricsProvider,
    string nodeName,
    string osVersion,
    string appVersion,
    ILogSink? log = null) : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

    private readonly ReconnectPolicy _reconnectPolicy = new();

    // Unbounded and persistent for the connection's lifetime (not recreated per pairing attempt)
    // so a code submitted before the state machine reaches Pairing is buffered, not dropped.
    private readonly Channel<string> _pairingCodeChannel = Channel.CreateUnbounded<string>();
    private CancellationTokenSource? _cts;
    private Task? _runLoop;
    private IMessageTransport? _transport;

    public NodeConnectionState State { get; private set; } = NodeConnectionState.Offline;
    public AgentCredentials? Credentials { get; private set; }
    public string? CurrentSceneId { get; set; }
    public int? CurrentProjectVersion { get; set; }

    public event Action<NodeConnectionState>? StateChanged;
    public event Action<AgentCredentials>? Paired;
    public event Action<Envelope>? MessageReceived;

    public void UseStoredCredentials(AgentCredentials credentials) => Credentials = credentials;

    /// <summary>Supplies a pairing code. Safe to call at any time — including before the
    /// connection has reached the Pairing state — since submissions are buffered.
    /// Typically wired to a UI prompt shown when State == Pairing.</summary>
    public void SubmitPairingCode(string code) => _pairingCodeChannel.Writer.TryWrite(code);

    public void Start()
    {
        if (_runLoop is not null)
        {
            throw new InvalidOperationException("Already started.");
        }

        _cts = new CancellationTokenSource();
        _runLoop = RunLoopAsync(_cts.Token);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(cancellationToken);
                _reconnectPolicy.Reset();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Network, "AgentConnection", "Connection attempt failed", ex.ToString()));
            }

            if (_transport is not null)
            {
                await _transport.DisposeAsync();
                _transport = null;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            SetState(NodeConnectionState.Offline);
            var delay = _reconnectPolicy.NextDelay();
            log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Network, "AgentConnection", $"Reconnecting in {delay.TotalSeconds:0}s"));
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        SetState(NodeConnectionState.Connecting);
        _transport = await transportFactory(cancellationToken);

        var hello = await ReceiveAsync<HelloPayload>(CommandType.Hello, cancellationToken)
            ?? throw new InvalidOperationException("Master did not send Hello.");

        if (Credentials is not null)
        {
            var authenticated = await TryAuthenticateAsync(cancellationToken);
            if (!authenticated)
            {
                Credentials = null;
            }
        }

        if (Credentials is null)
        {
            await PairAsync(cancellationToken);
        }

        SetState(NodeConnectionState.Online);
        await RunAuthenticatedLoopAsync(cancellationToken);
    }

    private async Task<bool> TryAuthenticateAsync(CancellationToken cancellationToken)
    {
        SetState(NodeConnectionState.Connecting);
        await _transport!.SendAsync(
            Envelope.Create(CommandType.AuthRequest, new AuthRequestPayload
            {
                NodeId = Credentials!.NodeId,
                SessionToken = Credentials.SessionToken
            }),
            cancellationToken);

        var response = await ReceiveAsync<AuthResponsePayload>(CommandType.AuthResponse, cancellationToken);
        return response?.Success == true;
    }

    private async Task PairAsync(CancellationToken cancellationToken)
    {
        SetState(NodeConnectionState.Pairing);
        var code = await _pairingCodeChannel.Reader.ReadAsync(cancellationToken);

        await _transport!.SendAsync(
            Envelope.Create(CommandType.PairRequest, new PairRequestPayload
            {
                PairingCode = code,
                NodeName = nodeName,
                OsVersion = osVersion,
                AppVersion = appVersion
            }),
            cancellationToken);

        var response = await ReceiveAsync<PairResponsePayload>(CommandType.PairResponse, cancellationToken);
        if (response is not { Approved: true, NodeId: not null, SessionToken: not null })
        {
            throw new InvalidOperationException($"Pairing rejected: {response?.Reason ?? "unknown reason"}");
        }

        Credentials = new AgentCredentials(response.NodeId, response.SessionToken);
        Paired?.Invoke(Credentials);
    }

    private async Task RunAuthenticatedLoopAsync(CancellationToken cancellationToken)
    {
        using var heartbeatTimer = new PeriodicTimer(HeartbeatInterval);
        var heartbeatLoop = SendHeartbeatsAsync(heartbeatTimer, cancellationToken);

        try
        {
            while (true)
            {
                var envelope = await _transport!.ReceiveAsync(cancellationToken);
                if (envelope is null)
                {
                    break;
                }

                if (envelope.Type == CommandType.Ping)
                {
                    await _transport.SendAsync(Envelope.Create(CommandType.Pong, null), cancellationToken);
                    continue;
                }

                MessageReceived?.Invoke(envelope);
            }
        }
        finally
        {
            heartbeatTimer.Dispose();
            try
            {
                await heartbeatLoop;
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }
    }

    private async Task SendHeartbeatsAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (Credentials is null || _transport is not { IsConnected: true })
            {
                continue;
            }

            await _transport.SendAsync(
                Envelope.Create(CommandType.Heartbeat, new HeartbeatPayload
                {
                    NodeId = Credentials.NodeId,
                    ConnectionState = State,
                    Metrics = metricsProvider.Sample(),
                    CurrentSceneId = CurrentSceneId,
                    CurrentProjectVersion = CurrentProjectVersion
                }),
                cancellationToken);
        }
    }

    private async Task<T?> ReceiveAsync<T>(CommandType expectedType, CancellationToken cancellationToken)
    {
        var envelope = await _transport!.ReceiveAsync(cancellationToken);
        if (envelope is null || envelope.Type != expectedType)
        {
            return default;
        }

        return envelope.ReadPayload<T>();
    }

    private void SetState(NodeConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_runLoop is not null)
        {
            try
            {
                await _runLoop;
            }
            catch
            {
                // shutting down
            }
        }

        if (_transport is not null)
        {
            await _transport.DisposeAsync();
        }

        _cts?.Dispose();
    }
}
