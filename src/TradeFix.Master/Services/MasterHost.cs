using System.IO;
using System.Net;
using System.Text.Json;
using TradeFix.Assets;
using TradeFix.Common;
using TradeFix.Common.Logging;
using TradeFix.Database;
using TradeFix.Database.Migrations;
using TradeFix.Database.Repositories;
using TradeFix.Network;
using TradeFix.Network.Auth;
using TradeFix.Network.Media;
using TradeFix.Network.Server;
using TradeFix.Network.Simulation;
using TradeFix.Protocol;
using TradeFix.Protocol.Messages;
using TradeFix.Shared.Enums;
using TradeFix.Shared.Models;
using TradeFix.Sources.Audio;
using TradeFix.Sources.Capture;

namespace TradeFix.Master.Services;

/// <summary>Composition root for the Master's backend: database, node registry, control server,
/// pairing, and logging. Owned for the lifetime of the application (App.xaml.cs).</summary>
public sealed class MasterHost : IAsyncDisposable
{
    public const string AppVersion = "1.0.0";

    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromMilliseconds(50);

    private readonly List<NodeSimulator> _simulators = [];
    private readonly System.Threading.Timer _broadcastTimer;
    private readonly object _dirtyLock = new();
    private readonly Dictionary<string, SourceDefinition> _dirtyTransforms = new();
    private readonly Dictionary<string, ScreenCaptureService> _activeCaptures = new();
    private readonly Dictionary<string, AudioCaptureService> _activeAudioCaptures = new();

    public MasterSettings Settings { get; }
    public LogBus Log { get; } = new();
    public InMemoryLogSink LogSink { get; } = new();
    public NodeRegistry Registry { get; } = new();
    public MasterServer Server { get; }
    public PairingService Pairing { get; }
    public ProjectState Project { get; } = new();
    public AssetStore Assets { get; }
    public MediaHub MediaHub { get; }
    public MediaHub AudioHub { get; }

    /// <summary>Fires on every frame this Master captures locally — (sourceId, jpegBytes) — so
    /// the Master's own canvas can preview a capture instead of only showing a static
    /// placeholder. Fired from a background capture thread; subscribers must marshal to the UI
    /// thread themselves.</summary>
    public event Action<string, byte[]>? LocalCaptureFrame;

    private string? _startupWarning;
    public string? StartupWarning => _startupWarning;

    public MasterHost(MasterSettings settings)
    {
        Settings = settings;

        Log.AddSink(LogSink);
        Log.AddSink(new FileLogSink(AppPaths.LogsDirectory("Master")));

        var dbFactory = new SqliteConnectionFactory(AppPaths.DatabasePath("Master", "master.db"));
        using (var connection = dbFactory.Open())
        {
            Migrator.Apply(connection);
        }

        var pairedNodes = new PairedNodeRepository(dbFactory);
        var pairingCodes = new PairingCodeRepository(dbFactory);
        Pairing = new PairingService(pairingCodes);
        Assets = new AssetStore(Path.Combine(AppPaths.DataRoot("Master"), "assets"));
        MediaHub = new MediaHub(Log);
        AudioHub = new MediaHub(Log);

        Server = new MasterServer(Registry, pairedNodes, pairingCodes, settings.ServerName, AppVersion, Log, Assets, MediaHub, AudioHub);
        Server.SessionReady += OnSessionReady;

        Project.Changed += () => _ = Server.BroadcastAsync(Envelope.Create(CommandType.LoadScene, Project.BuildActiveScenePayload()));
        Project.SourceTransformChanged += source =>
        {
            lock (_dirtyLock)
            {
                _dirtyTransforms[source.Id] = source;
            }
        };

        // Coalesces rapid drag/resize updates to a fixed max send rate instead of one network
        // message per mouse-move event (spec section 37: don't flood the network).
        _broadcastTimer = new System.Threading.Timer(_ => FlushDirtyTransforms(), null, BroadcastInterval, BroadcastInterval);
    }

    private void FlushDirtyTransforms()
    {
        List<SourceDefinition> snapshot;
        lock (_dirtyLock)
        {
            if (_dirtyTransforms.Count == 0)
            {
                return;
            }

            snapshot = [.. _dirtyTransforms.Values];
            _dirtyTransforms.Clear();
        }

        foreach (var source in snapshot)
        {
            _ = Server.BroadcastAsync(Envelope.Create(CommandType.UpdateSource, new UpdateSourcePayload { Source = source }));
        }
    }

    private void OnSessionReady(NodeSession session)
    {
        _ = session.SendAsync(
            Envelope.Create(CommandType.LoadScene, Project.BuildActiveScenePayload()),
            CancellationToken.None);
    }

    public void Start()
    {
        try
        {
            Server.Start(Settings.ControlPort, Settings.BindAllInterfaces);
        }
        catch (HttpListenerException) when (Settings.BindAllInterfaces)
        {
            _startupWarning =
                $"Could not bind to all network interfaces on port {Settings.ControlPort} " +
                "(Windows requires either Administrator elevation or a one-time URL ACL reservation — " +
                "see docs/NODE_SYSTEM.md). Falling back to localhost-only; render nodes on other PCs " +
                "will not be able to connect until this is resolved.";
            Log.Write(LogCategory.Error, "MasterHost", _startupWarning);
            Server.Start(Settings.ControlPort, bindAllInterfaces: false);
        }

        Log.Write(LogCategory.Info, "MasterHost", $"Master started on port {Settings.ControlPort}");
    }

    /// <summary>Starts a live capture — either one specific app/window (pass <paramref name="window"/>,
    /// picked via <see cref="TradeFix.Sources.Capture.WindowEnumerator"/>) or, if null, the whole
    /// primary monitor — and adds it as a source. Frames stream to every subscribed node over
    /// <see cref="MediaHub"/> — a separate channel from the JSON control messages this same class
    /// broadcasts for the source's position/size (spec section 38). Each call starts an
    /// independent capture, so multiple different apps can be captured side by side.</summary>
    public SourceDefinition AddCaptureSource(CapturableWindow? window)
    {
        const int defaultFps = 12;
        const int defaultMaxDimension = 3840;
        const int defaultQuality = 100;
        const bool defaultIncludeAudio = true;

        var name = window?.Title ?? "Screen Capture";
        var config = JsonSerializer.SerializeToElement(
            new { live = true, windowTitle = window?.Title, fps = defaultFps, maxDimension = defaultMaxDimension, quality = defaultQuality, audio = defaultIncludeAudio },
            ProtocolSerializer.Options);
        var source = Project.AddSource(SourceType.DisplayCapture, name, config,
            new Transform2D { X = 200, Y = 120, Width = 640, Height = 360 });

        StartCapture(source.Id, window?.Handle, defaultFps, defaultMaxDimension, defaultQuality);
        if (defaultIncludeAudio)
        {
            StartAudioCapture(source.Id);
        }

        Log.Write(LogCategory.Media, "MasterHost", $"Started capture '{name}' for source {source.Id}");
        return source;
    }

    /// <summary>Restarts an existing capture at new FPS/quality/audio settings without changing
    /// which window it targets, and updates the source's Config so the new settings survive a
    /// resync (e.g. a node reconnecting) and show correctly if the Properties panel is reopened.</summary>
    public void UpdateCaptureSettings(string sourceId, int fps, int maxDimension, int quality, bool includeAudio)
    {
        if (!_activeCaptures.TryGetValue(sourceId, out var existing))
        {
            return;
        }

        var currentSource = Project.ActiveSceneSources.FirstOrDefault(s => s.Id == sourceId);
        string? windowTitle = currentSource is { Config.ValueKind: JsonValueKind.Object } &&
            currentSource.Config.TryGetProperty("windowTitle", out var titleProp) && titleProp.ValueKind == JsonValueKind.String
                ? titleProp.GetString()
                : null;

        var targetWindow = existing.TargetWindow;
        existing.Stop();
        existing.Dispose();
        StartCapture(sourceId, targetWindow, fps, maxDimension, quality);

        StopAudioCapture(sourceId);
        if (includeAudio)
        {
            StartAudioCapture(sourceId);
        }

        var config = JsonSerializer.SerializeToElement(
            new { live = true, windowTitle, fps, maxDimension, quality, audio = includeAudio },
            ProtocolSerializer.Options);
        Project.UpdateConfig(sourceId, config);

        Log.Write(LogCategory.Media, "MasterHost", $"Restarted capture {sourceId} at {fps} FPS / {maxDimension}px / quality={quality} / audio={includeAudio}");
    }

    private void StartCapture(string sourceId, IntPtr? targetWindow, int fps, int maxDimension, int quality)
    {
        var capture = new ScreenCaptureService(fps, maxDimension, targetWindow, quality);
        capture.FrameCaptured += bytes =>
        {
            // Master already has these bytes in-process — no need to round-trip through its own
            // MediaHub/WebSocket to preview what it's sending, unlike a remote node.
            LocalCaptureFrame?.Invoke(sourceId, bytes);
            return MediaHub.BroadcastFrameAsync(sourceId, bytes, CancellationToken.None);
        };
        _activeCaptures[sourceId] = capture;
        capture.Start();
    }

    private void StartAudioCapture(string sourceId)
    {
        var audioCapture = new AudioCaptureService();
        audioCapture.ChunkCaptured += bytes => AudioHub.BroadcastFrameAsync(sourceId, bytes, CancellationToken.None);
        _activeAudioCaptures[sourceId] = audioCapture;
        audioCapture.Start();
    }

    private void StopAudioCapture(string sourceId)
    {
        if (_activeAudioCaptures.Remove(sourceId, out var audioCapture))
        {
            audioCapture.Stop();
            audioCapture.Dispose();
        }
    }

    /// <summary>Removes a source, stopping any live capture (video and audio) it owns first.</summary>
    public void RemoveSource(string sourceId)
    {
        if (_activeCaptures.Remove(sourceId, out var capture))
        {
            capture.Stop();
            capture.Dispose();
            Log.Write(LogCategory.Media, "MasterHost", $"Stopped screen capture for source {sourceId}");
        }

        StopAudioCapture(sourceId);

        Project.RemoveSource(sourceId);
    }

    public NodeSimulator AddSimulatedNode(string name)
    {
        var seed = Random.Shared.Next();
        var simulator = NodeSimulator.Start(Server, Pairing, name, seed);
        _simulators.Add(simulator);
        Log.Write(LogCategory.Node, "MasterHost", $"Started simulated node '{name}'");
        return simulator;
    }

    public async ValueTask DisposeAsync()
    {
        await _broadcastTimer.DisposeAsync();

        foreach (var capture in _activeCaptures.Values)
        {
            capture.Stop();
            capture.Dispose();
        }

        foreach (var audioCapture in _activeAudioCaptures.Values)
        {
            audioCapture.Stop();
            audioCapture.Dispose();
        }

        foreach (var simulator in _simulators)
        {
            await simulator.DisposeAsync();
        }

        await Server.DisposeAsync();
    }
}
