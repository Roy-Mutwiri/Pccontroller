using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using NAudio.Wave;
using TradeFix.Assets;
using TradeFix.Common;
using TradeFix.Common.Logging;
using TradeFix.Network.Client;
using TradeFix.Network.Media;
using TradeFix.Network.Metrics;
using TradeFix.Protocol;
using TradeFix.Protocol.Messages;
using TradeFix.Shared.Enums;
using TradeFix.Shared.Models;
using TradeFix.Sources.Video;

namespace TradeFix.Agent.Services;

/// <summary>Composition root for the Agent's backend: connection state machine, metrics, credentials, logging.</summary>
public sealed class AgentHost : IAsyncDisposable
{
    public const string AppVersion = "1.0.0";

    private static readonly WaveFormat AudioFormat = new(24000, 16, 1);

    /// <summary>Must match MasterHost.SharedAudioSourceId. Master merges every audio-enabled
    /// capture source into one desktop-audio loopback instead of one capture per source (WASAPI
    /// loopback captures the whole system output regardless of which app is targeted, so N
    /// per-source streams were always N copies of the identical signal) — that's what caused the
    /// audible echo nodes used to hear when more than one capture had audio on: two unsynchronized
    /// copies of the same audio, played through two separate players. One shared channel, one
    /// subscription, one player, regardless of how many sources have audio enabled.</summary>
    private const string SharedAudioSourceId = "desktop-audio";

    /// <summary>Largest single media/audio WebSocket message the Agent will assemble. Real
    /// messages are ≤64KB H.264 chunks, JPEG frames (a few MB at 4K/quality-100), or ~10KB audio
    /// chunks — anything bigger means a corrupted/hostile stream, and buffering it unbounded
    /// would balloon memory until OOM.</summary>
    private const int MaxIncomingMessageBytes = 32 * 1024 * 1024;

    private readonly HttpClient _httpClient = new();

    /// <summary>Guards <see cref="_liveSubscriptions"/> and <see cref="_audioSubscription"/> —
    /// scene syncs arrive on the connection's receive thread, while Logout/Dispose run on the UI
    /// thread; plain Dictionary mutation from both would race.</summary>
    private readonly object _subscriptionsLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _liveSubscriptions = new();
    private CancellationTokenSource? _audioSubscription;
    private (WaveOutEvent Player, BufferedWaveProvider Buffer)? _audioPlayer;

    private long _videoBytesReceived;
    private long _videoFramesDisplayed;

    /// <summary>Total compressed video bytes received off the network across all media
    /// subscriptions — the UI diffs this once a second into a live bitrate readout, so an
    /// operator can *see* what the link is actually carrying instead of guessing about lag.</summary>
    public long VideoBytesReceived => Interlocked.Read(ref _videoBytesReceived);

    /// <summary>Total frames handed to the render window (decoded H.264 frames or fallback
    /// JPEGs) — diffed into a live displayed-FPS readout.</summary>
    public long VideoFramesDisplayed => Interlocked.Read(ref _videoFramesDisplayed);

    public AgentSettings Settings { get; private set; }
    public LogBus Log { get; } = new();
    public InMemoryLogSink LogSink { get; } = new();
    public AgentConnection? Connection { get; private set; }
    public AssetStore Assets { get; } = new(Path.Combine(AppPaths.DataRoot("Agent"), "assets"));

    /// <summary>Fires (on a background thread — subscribers must marshal to the UI thread
    /// themselves) whenever the Master pushes an updated source. Drives the render window.</summary>
    public event Action<SourceDefinition>? SourceUpdated;

    /// <summary>Fires on a full scene (re)load — replace-all, not a patch. Sent by Master on
    /// scene switch, source add/remove, or when this node first connects/reconnects.</summary>
    public event Action<LoadSceneDefinitionPayload>? SceneLoaded;

    /// <summary>Fires once an Image source's asset has been downloaded and cached locally (or
    /// was already cached) — (sourceId, localFilePath).</summary>
    public event Action<string, string>? AssetReady;

    /// <summary>Fires on every frame received from a live screen-capture source's media
    /// subscription — (sourceId, jpegBytes). Decoding into a displayable bitmap and marshaling
    /// to the UI thread is the subscriber's job (see RenderWindow).</summary>
    public event Action<string, byte[]>? LiveFrameReceived;

    public AgentHost(AgentSettings settings)
    {
        Settings = settings;
        Log.AddSink(LogSink);
        Log.AddSink(new FileLogSink(AppPaths.LogsDirectory("Agent")));
    }

    /// <summary>"Logs out" of the currently-paired Master: disconnects, forgets the stored
    /// credentials and Master address (node name is kept — it describes this PC, not the
    /// pairing), and clears the render window. After this the Agent is back to its first-launch
    /// state, ready to accept a connect code from a different Master — previously a node was
    /// permanently bound to whichever Master it first paired with unless someone hand-deleted
    /// its credential files.</summary>
    public async Task LogoutAsync()
    {
        lock (_subscriptionsLock)
        {
            foreach (var cts in _liveSubscriptions.Values)
            {
                cts.Cancel();
            }

            _liveSubscriptions.Clear();
            _audioSubscription?.Cancel();
            _audioSubscription = null;
        }

        if (Connection is not null)
        {
            await Connection.DisposeAsync();
            Connection = null;
        }

        CredentialStore.Clear();
        Settings = new AgentSettings { NodeName = Settings.NodeName };
        AgentSettingsStore.Save(Settings);

        // Clear whatever scene the render window is still showing — an empty scene load is the
        // same replace-all mechanism a real scene switch uses.
        SceneLoaded?.Invoke(new LoadSceneDefinitionPayload
        {
            Scene = new SceneDefinition { Id = "logged-out", Name = "Logged out" },
            Sources = []
        });
        Log.Write(LogCategory.Node, "AgentHost", "Logged out — pairing and Master address forgotten");
    }

    public AgentConnection Connect(AgentSettings settings)
    {
        // A previous connection (retrying a bad address, or an old Master after log-out) must
        // not keep reconnect-looping in the background alongside the new one.
        if (Connection is { } previous)
        {
            _ = previous.DisposeAsync();
            Connection = null;
        }

        Settings = settings;

        var connection = new AgentConnection(
            WebSocketTransportFactory.ForMaster(settings.MasterHost, settings.MasterPort),
            new BasicNodeMetricsProvider(),
            settings.NodeName,
            osVersion: Environment.OSVersion.VersionString,
            appVersion: AppVersion,
            Log);

        var stored = CredentialStore.Load();
        if (stored is not null)
        {
            connection.UseStoredCredentials(stored);
        }

        connection.Paired += credentials =>
        {
            CredentialStore.Save(credentials);
            Log.Write(LogCategory.Node, "AgentHost", $"Paired with Master as {credentials.NodeId}");
        };

        // Persist the Master's address only once this attempt actually succeeds — persisting on
        // attempt meant one mistyped/expired connect code permanently hid the first-run connect
        // UI behind a saved address that never worked.
        var settingsPersisted = false;
        connection.StateChanged += state =>
        {
            if (state == NodeConnectionState.Online && !settingsPersisted)
            {
                settingsPersisted = true;
                AgentSettingsStore.Save(settings);
            }

            Log.Write(LogCategory.Network, "AgentHost", $"Connection state -> {state}");
        };

        connection.MessageReceived += envelope =>
        {
            switch (envelope.Type)
            {
                case CommandType.UpdateSource:
                    var updatePayload = envelope.ReadPayload<UpdateSourcePayload>();
                    if (updatePayload is not null)
                    {
                        SourceUpdated?.Invoke(updatePayload.Source);
                    }

                    break;

                case CommandType.LoadScene:
                    var scenePayload = envelope.ReadPayload<LoadSceneDefinitionPayload>();
                    if (scenePayload is not null)
                    {
                        SceneLoaded?.Invoke(scenePayload);
                        foreach (var source in scenePayload.Sources)
                        {
                            EnsureAssetDownloaded(source);
                        }

                        SyncLiveSubscriptions(scenePayload.Sources);
                        SyncAudioSubscriptions(scenePayload.Sources);
                    }

                    break;
            }
        };

        Connection = connection;
        connection.Start();
        return connection;
    }

    /// <summary>Downloads an Image source's file from Master if not already cached locally
    /// (spec section 16 — never re-transfer a file the node already has). Fire-and-forget: the
    /// render window shows nothing for that source until <see cref="AssetReady"/> fires.</summary>
    private void EnsureAssetDownloaded(SourceDefinition source)
    {
        if (source.Type != SourceType.Image || source.Config.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!source.Config.TryGetProperty("assetHash", out var hashProp)
            || hashProp.ValueKind != JsonValueKind.String
            || hashProp.GetString() is not { } hash)
        {
            // A non-string assetHash (malformed scene payload) must not throw here — this runs
            // on the connection's receive thread, and an exception would poison every LOAD_SCENE.
            return;
        }

        var existingPath = Assets.GetFilePath(hash);
        if (existingPath is not null)
        {
            AssetReady?.Invoke(source.Id, existingPath);
            return;
        }

        var fileName = source.Config.TryGetProperty("fileName", out var fileNameProp) ? fileNameProp.GetString() : null;
        var extension = string.IsNullOrEmpty(fileName) ? string.Empty : Path.GetExtension(fileName);

        _ = DownloadAssetAsync(hash, extension, source.Id);
    }

    private async Task DownloadAssetAsync(string hash, string extension, string sourceId)
    {
        try
        {
            var url = $"http://{Settings.MasterHost}:{Settings.MasterPort}/assets/{hash}";
            var bytes = await _httpClient.GetByteArrayAsync(url);

            if (Assets.TrySaveBytes(hash, extension, bytes))
            {
                var path = Assets.GetFilePath(hash);
                if (path is not null)
                {
                    AssetReady?.Invoke(sourceId, path);
                }
            }
            else
            {
                Log.Write(LogCategory.Error, "AgentHost", $"Downloaded asset {hash} failed hash verification.");
            }
        }
        catch (Exception ex)
        {
            Log.Write(LogCategory.Error, "AgentHost", $"Failed to download asset {hash}", ex);
        }
    }

    /// <summary>Opens/closes media subscriptions to match the current scene: a subscription per
    /// live-capture source present, none for sources that were removed or switched away from.
    /// Separate WebSocket per source, deliberately not multiplexed onto the control channel
    /// (spec section 38).</summary>
    private void SyncLiveSubscriptions(IReadOnlyList<SourceDefinition> currentSources)
    {
        lock (_subscriptionsLock)
        {
            var liveIds = currentSources.Where(IsLiveCaptureSource).Select(s => s.Id).ToHashSet();

            foreach (var staleId in _liveSubscriptions.Keys.Where(id => !liveIds.Contains(id)).ToList())
            {
                _liveSubscriptions[staleId].Cancel();
                _liveSubscriptions.Remove(staleId);
            }

            foreach (var source in currentSources.Where(IsLiveCaptureSource))
            {
                if (_liveSubscriptions.ContainsKey(source.Id))
                {
                    continue;
                }

                var cts = new CancellationTokenSource();
                _liveSubscriptions[source.Id] = cts;
                _ = RunMediaSubscriptionAsync(source.Id, cts.Token);
            }
        }
    }

    private static bool IsLiveCaptureSource(SourceDefinition source) =>
        source.Type is SourceType.DisplayCapture or SourceType.WindowCapture
        && source.Config.ValueKind == JsonValueKind.Object
        && source.Config.TryGetProperty("live", out var liveProp)
        && liveProp.ValueKind == JsonValueKind.True;

    /// <summary>Keeps a source's media subscription alive for as long as the source exists in the
    /// scene: the inner body runs one WebSocket's lifetime, and this loop reconnects with backoff
    /// whenever it drops. Without this, a single network blip (or a Master restart) silently and
    /// permanently froze that source's video on this node until a scene change happened to arrive —
    /// the subscription entry stayed registered but nothing was listening anymore.</summary>
    private async Task RunMediaSubscriptionAsync(string sourceId, CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                connected = await RunMediaSubscriptionOnceAsync(sourceId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            backoff = connected ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 15));
            try
            {
                await Task.Delay(backoff, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <returns>true if the socket connected at all (used to reset the retry backoff).</returns>
    private async Task<bool> RunMediaSubscriptionOnceAsync(string sourceId, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(new Uri($"ws://{Settings.MasterHost}:{Settings.MasterPort}/media/{sourceId}"), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write(LogCategory.Error, "AgentHost", $"Failed to connect media subscription for {sourceId} — will retry", ex);
            return false;
        }

        Log.Write(LogCategory.Media, "AgentHost", $"Media subscription connected for {sourceId}");

        // Per-message routing (see H264StreamProtocol): a restart marker resets the decoder, a
        // complete JPEG goes straight to display (the fallback pipeline, unchanged), anything
        // else is a chunk of the continuous H.264 stream. Decoded H.264 frames arrive as BMPs and
        // flow through the *same* LiveFrameReceived display path as JPEGs — the renderer's
        // decoder auto-detects the format.
        H264VideoDecoder? decoder = null;
        var loggedNoFfmpeg = false;

        var buffer = new byte[64 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return true;
                    }

                    frame.Write(buffer, 0, result.Count);
                    if (frame.Length > MaxIncomingMessageBytes)
                    {
                        Log.Write(LogCategory.Error, "AgentHost", $"Media message for {sourceId} exceeded {MaxIncomingMessageBytes} bytes — dropping the connection");
                        return true;
                    }
                }
                while (!result.EndOfMessage);

                var message = frame.ToArray();
                Interlocked.Add(ref _videoBytesReceived, message.Length);

                if (H264StreamProtocol.IsRestartMarker(message))
                {
                    decoder?.Dispose();
                    decoder = null;
                    continue;
                }

                if (H264StreamProtocol.IsCompleteJpeg(message))
                {
                    Interlocked.Increment(ref _videoFramesDisplayed);
                    LiveFrameReceived?.Invoke(sourceId, message);
                    continue;
                }

                if (decoder is null)
                {
                    var ffmpegPath = FfmpegLocator.Find();
                    if (ffmpegPath is null)
                    {
                        if (!loggedNoFfmpeg)
                        {
                            loggedNoFfmpeg = true;
                            Log.Write(LogCategory.Error, "AgentHost",
                                $"Master is sending H.264 video for {sourceId} but ffmpeg isn't available on this node — video disabled for this source (reinstall from the current package to get ffmpeg)");
                        }

                        continue;
                    }

                    decoder = new H264VideoDecoder(ffmpegPath);
                    decoder.FrameDecoded += bmp =>
                    {
                        Interlocked.Increment(ref _videoFramesDisplayed);
                        LiveFrameReceived?.Invoke(sourceId, bmp);
                    };
                }

                try
                {
                    await decoder.WriteAsync(message, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Decoder process died — tear it down; the next chunk recreates it and the
                    // picture recovers at the next keyframe (≤1s).
                    Log.Write(LogCategory.Error, "AgentHost", $"H.264 decoder for {sourceId} failed — restarting it", ex);
                    decoder.Dispose();
                    decoder = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw; // subscription cancelled — scene switch/source removal; outer loop stops
        }
        catch (Exception ex)
        {
            Log.Write(LogCategory.Error, "AgentHost", $"Media subscription for {sourceId} dropped — will retry", ex);
        }
        finally
        {
            decoder?.Dispose();
        }

        return true;
    }

    /// <summary>One subscription+player for the Master's single shared desktop-audio channel,
    /// active whenever at least one current source has audio enabled — not one per source (see
    /// <see cref="SharedAudioSourceId"/> for why per-source audio subscriptions caused an audible
    /// echo). A separate WebSocket from the video media channel (spec section 38) — video and
    /// audio are independent streams, each tolerant of the other dropping out.</summary>
    private void SyncAudioSubscriptions(IReadOnlyList<SourceDefinition> currentSources)
    {
        lock (_subscriptionsLock)
        {
            var wantsAudio = currentSources.Any(IsAudioCaptureSource);

            if (!wantsAudio && _audioSubscription is not null)
            {
                _audioSubscription.Cancel();
                _audioSubscription = null;
            }
            else if (wantsAudio && _audioSubscription is null)
            {
                var cts = new CancellationTokenSource();
                _audioSubscription = cts;
                _ = RunAudioSubscriptionAsync(cts);
            }
        }
    }

    private static bool IsAudioCaptureSource(SourceDefinition source) =>
        IsLiveCaptureSource(source)
        && source.Config.TryGetProperty("audio", out var audioProp)
        && audioProp.ValueKind == JsonValueKind.True;

    /// <summary>Retry wrapper mirroring <see cref="RunMediaSubscriptionAsync"/>: audio keeps
    /// reconnecting with backoff for as long as some source wants it, instead of silently staying
    /// dead after a network blip until the next scene change.</summary>
    private async Task RunAudioSubscriptionAsync(CancellationTokenSource ownCts)
    {
        var backoff = TimeSpan.FromSeconds(1);
        try
        {
            while (!ownCts.Token.IsCancellationRequested)
            {
                var connected = false;
                try
                {
                    connected = await RunAudioSubscriptionOnceAsync(ownCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (ownCts.Token.IsCancellationRequested)
                {
                    return;
                }

                backoff = connected ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 15));
                try
                {
                    await Task.Delay(backoff, ownCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        finally
        {
            lock (_subscriptionsLock)
            {
                // Clear only if we're still the current subscription — a newer SyncAudioSubscriptions
                // may already have replaced us; don't stomp on it.
                if (ReferenceEquals(_audioSubscription, ownCts))
                {
                    _audioSubscription = null;
                }
            }
        }
    }

    private async Task<bool> RunAudioSubscriptionOnceAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(new Uri($"ws://{Settings.MasterHost}:{Settings.MasterPort}/audio/{SharedAudioSourceId}"), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write(LogCategory.Error, "AgentHost", "Failed to connect audio subscription — will retry", ex);
            return false;
        }

        var bufferedWaveProvider = new BufferedWaveProvider(AudioFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2)
        };

        // WaveOutEvent.Init/Play throws if this PC has no default playback device (WASAPI
        // "NoDriver", e.g. a headless render node or one with audio disabled/muted at the OS
        // level). Fail this one attempt gracefully — video keeps working even when a node
        // genuinely has no audio output. DesiredLatency trims WaveOut's default ~300ms internal
        // buffering; 150ms keeps playback comfortably glitch-free while shaving noticeable
        // audio-behind-video delay.
        WaveOutEvent player;
        try
        {
            player = new WaveOutEvent { DesiredLatency = 150 };
            player.Init(bufferedWaveProvider);
            player.Play();
        }
        catch (Exception ex)
        {
            Log.Write(LogCategory.Error, "AgentHost", "No audio playback device available on this PC — audio disabled, video unaffected", ex);
            return true; // connected fine; the device, not the network, is the problem
        }

        _audioPlayer = (player, bufferedWaveProvider);

        var gapFiller = new AudioSyncGapFiller(AudioFormat.AverageBytesPerSecond);

        Log.Write(LogCategory.Audio, "AgentHost", "Audio subscription connected");

        var buffer = new byte[64 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var chunk = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return true;
                    }

                    chunk.Write(buffer, 0, result.Count);
                    if (chunk.Length > MaxIncomingMessageBytes)
                    {
                        Log.Write(LogCategory.Error, "AgentHost", "Audio message exceeded the size cap — dropping the connection");
                        return true;
                    }
                }
                while (!result.EndOfMessage);

                var framed = chunk.ToArray();
                if (!AudioChunkFraming.TryDecode(framed, out var timestampMs, out var pcm))
                {
                    continue; // malformed/truncated message — skip rather than play garbage
                }

                // Drift guard: chunks arrive at exactly real-time rate, so any backlog that ever
                // accumulates (network jitter batching chunks, a UI stall, the PC sleeping) NEVER
                // drains on its own — playback just runs permanently that far behind. The old 1s
                // threshold let sound settle ~1s late, which reads as broken lip-sync now that
                // same-LAN video is near-instant. 350ms keeps a healthy jitter cushion while
                // capping standing audio delay at ~0.5s worst (350ms buffer + 150ms WaveOut);
                // the reset itself skips at most a third of a second — a barely-audible blip
                // that buys back sync for good.
                if (bufferedWaveProvider.BufferedDuration > TimeSpan.FromMilliseconds(350))
                {
                    bufferedWaveProvider.ClearBuffer();
                    gapFiller.Reset();
                }

                var silenceBytes = gapFiller.SilenceBytesBefore(timestampMs, pcm.Count);
                if (silenceBytes > 0)
                {
                    // Filling a gap left by a dropped chunk keeps audio anchored to the same
                    // timeline as video instead of quietly playing back-to-back and drifting ahead.
                    bufferedWaveProvider.AddSamples(new byte[silenceBytes], 0, silenceBytes);
                }

                bufferedWaveProvider.AddSamples(pcm.Array!, pcm.Offset, pcm.Count);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw; // audio toggled off / scene switch — outer loop stops
        }
        catch (Exception ex)
        {
            Log.Write(LogCategory.Error, "AgentHost", "Audio subscription dropped — will retry", ex);
            return true;
        }
        finally
        {
            // Tear down THIS attempt's player specifically — never whatever the field currently
            // holds, which may already belong to a newer attempt (the old code's shared-field
            // teardown could dispose a successor's live player).
            player.Stop();
            player.Dispose();
            if (_audioPlayer is { } current && ReferenceEquals(current.Player, player))
            {
                _audioPlayer = null;
            }
        }
    }

    private void StopAudioPlayer()
    {
        if (_audioPlayer is { } entry)
        {
            entry.Player.Stop();
            entry.Player.Dispose();
            _audioPlayer = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_subscriptionsLock)
        {
            foreach (var cts in _liveSubscriptions.Values)
            {
                cts.Cancel();
            }

            _audioSubscription?.Cancel();
        }

        StopAudioPlayer();

        if (Connection is not null)
        {
            await Connection.DisposeAsync();
        }
    }
}
