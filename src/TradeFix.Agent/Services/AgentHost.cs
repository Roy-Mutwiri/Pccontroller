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

namespace TradeFix.Agent.Services;

/// <summary>Composition root for the Agent's backend: connection state machine, metrics, credentials, logging.</summary>
public sealed class AgentHost : IAsyncDisposable
{
    public const string AppVersion = "1.0.0";

    private static readonly WaveFormat AudioFormat = new(24000, 16, 1);

    private readonly HttpClient _httpClient = new();
    private readonly Dictionary<string, CancellationTokenSource> _liveSubscriptions = new();
    private readonly Dictionary<string, CancellationTokenSource> _audioSubscriptions = new();
    private readonly Dictionary<string, (WaveOutEvent Player, BufferedWaveProvider Buffer)> _audioPlayers = new();

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

    public AgentConnection Connect(AgentSettings settings)
    {
        Settings = settings;
        AgentSettingsStore.Save(settings);

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

        connection.StateChanged += state =>
            Log.Write(LogCategory.Network, "AgentHost", $"Connection state -> {state}");

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

        if (!source.Config.TryGetProperty("assetHash", out var hashProp) || hashProp.GetString() is not { } hash)
        {
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

    private static bool IsLiveCaptureSource(SourceDefinition source) =>
        source.Type is SourceType.DisplayCapture or SourceType.WindowCapture
        && source.Config.ValueKind == JsonValueKind.Object
        && source.Config.TryGetProperty("live", out var liveProp)
        && liveProp.ValueKind == JsonValueKind.True;

    private async Task RunMediaSubscriptionAsync(string sourceId, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(new Uri($"ws://{Settings.MasterHost}:{Settings.MasterPort}/media/{sourceId}"), cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Write(LogCategory.Error, "AgentHost", $"Failed to connect media subscription for {sourceId}", ex);
            return;
        }

        Log.Write(LogCategory.Media, "AgentHost", $"Media subscription connected for {sourceId}");

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
                        return;
                    }

                    frame.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                LiveFrameReceived?.Invoke(sourceId, frame.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            // subscription cancelled — expected on scene switch/source removal
        }
        catch (Exception ex)
        {
            Log.Write(LogCategory.Error, "AgentHost", $"Media subscription for {sourceId} dropped", ex);
        }
    }

    /// <summary>Mirrors <see cref="SyncLiveSubscriptions"/> for audio: one subscription+player
    /// per capture source that has audio enabled, torn down when that source is removed or
    /// audio is turned off. A separate WebSocket from the video media channel (spec section 38)
    /// — video and audio are independent streams, each tolerant of the other dropping out.</summary>
    private void SyncAudioSubscriptions(IReadOnlyList<SourceDefinition> currentSources)
    {
        var audioIds = currentSources.Where(IsAudioCaptureSource).Select(s => s.Id).ToHashSet();

        foreach (var staleId in _audioSubscriptions.Keys.Where(id => !audioIds.Contains(id)).ToList())
        {
            _audioSubscriptions[staleId].Cancel();
            _audioSubscriptions.Remove(staleId);
            StopAudioPlayer(staleId);
        }

        foreach (var source in currentSources.Where(IsAudioCaptureSource))
        {
            if (_audioSubscriptions.ContainsKey(source.Id))
            {
                continue;
            }

            var cts = new CancellationTokenSource();
            _audioSubscriptions[source.Id] = cts;
            _ = RunAudioSubscriptionAsync(source.Id, cts.Token);
        }
    }

    private static bool IsAudioCaptureSource(SourceDefinition source) =>
        IsLiveCaptureSource(source)
        && source.Config.TryGetProperty("audio", out var audioProp)
        && audioProp.ValueKind == JsonValueKind.True;

    private async Task RunAudioSubscriptionAsync(string sourceId, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(new Uri($"ws://{Settings.MasterHost}:{Settings.MasterPort}/audio/{sourceId}"), cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Write(LogCategory.Error, "AgentHost", $"Failed to connect audio subscription for {sourceId}", ex);
            return;
        }

        var bufferedWaveProvider = new BufferedWaveProvider(AudioFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2)
        };
        var player = new WaveOutEvent();
        player.Init(bufferedWaveProvider);
        player.Play();
        _audioPlayers[sourceId] = (player, bufferedWaveProvider);

        var gapFiller = new AudioSyncGapFiller(AudioFormat.AverageBytesPerSecond);

        Log.Write(LogCategory.Audio, "AgentHost", $"Audio subscription connected for {sourceId}");

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
                        return;
                    }

                    chunk.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var framed = chunk.ToArray();
                if (!AudioChunkFraming.TryDecode(framed, out var timestampMs, out var pcm))
                {
                    continue; // malformed/truncated message — skip rather than play garbage
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
        }
        catch (OperationCanceledException)
        {
            // subscription cancelled — expected on scene switch/source removal/audio toggled off
        }
        catch (Exception ex)
        {
            Log.Write(LogCategory.Error, "AgentHost", $"Audio subscription for {sourceId} dropped", ex);
        }
        finally
        {
            StopAudioPlayer(sourceId);
        }
    }

    private void StopAudioPlayer(string sourceId)
    {
        if (_audioPlayers.Remove(sourceId, out var entry))
        {
            entry.Player.Stop();
            entry.Player.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var cts in _liveSubscriptions.Values)
        {
            cts.Cancel();
        }

        foreach (var cts in _audioSubscriptions.Values)
        {
            cts.Cancel();
        }

        foreach (var sourceId in _audioPlayers.Keys.ToList())
        {
            StopAudioPlayer(sourceId);
        }

        if (Connection is not null)
        {
            await Connection.DisposeAsync();
        }
    }
}
