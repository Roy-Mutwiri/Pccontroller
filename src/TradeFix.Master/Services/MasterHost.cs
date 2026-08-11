using System.Diagnostics;
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
using TradeFix.Sources.Video;

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

    /// <summary>One H.264 encoder per capture source when ffmpeg is available (see
    /// <see cref="StartCapture"/> — the quality fix that replaced JPEG-per-frame). Null entries
    /// never exist; sources running the JPEG fallback simply aren't in this dictionary.</summary>
    private readonly Dictionary<string, H264VideoEncoder> _activeEncoders = new();

    /// <summary>Serializes every capture/encoder/audio lifecycle mutation. These dictionaries are
    /// reached from at least four different threads — the UI thread (add/edit/remove source), the
    /// 50ms broadcast timer (adaptive step-up), capture/encoder-pump threads (adaptive step-down
    /// via FrameDropped), and thread-pool threads (encoder-failure fallback) — and plain
    /// Dictionary is documented unsafe under concurrent mutation: the failure mode is internal
    /// corruption/InvalidOperationException precisely under network pressure, i.e. mid-broadcast.
    /// Everything under this lock is non-blocking (starting/stopping captures, dictionary writes);
    /// events and logging fire outside it.</summary>
    private readonly object _capturesLock = new();

    /// <summary>Set when an encoder gives up mid-run (ffmpeg dying repeatedly) — from then on
    /// every capture (re)start uses the JPEG fallback instead of burning a broken ffmpeg per
    /// source. Never reset within a run; ffmpeg doesn't come back mid-session.</summary>
    private bool _ffmpegBroken;

    /// <summary>WASAPI loopback captures the *entire* system output, not the individual app being
    /// captured — so if two capture sources each had audio enabled, the old code (one
    /// AudioCaptureService per source) started two independent loopbacks of the identical signal
    /// and sent them to nodes as two unsynchronized streams, which nodes then played through two
    /// separate WaveOutEvent players. Two copies of the same audio, drifting out of phase, is
    /// exactly what an operator hears as an echo. Fixed by sharing one capture across every source
    /// that wants audio (ref-counted by <see cref="_audioEnabledSources"/>) and broadcasting it on
    /// one well-known channel — see <see cref="SharedAudioSourceId"/> and AgentHost's matching
    /// single subscription.</summary>
    private const string SharedAudioSourceId = "desktop-audio";
    private readonly HashSet<string> _audioEnabledSources = new();
    private AudioCaptureService? _sharedAudioCapture;

    private readonly object _adaptiveLock = new();
    private readonly Dictionary<string, AdaptiveEncodingController> _adaptiveControllers = new();

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

    /// <summary>Fires whenever <see cref="AdaptiveEncodingController"/> steps a capture's
    /// quality/resolution up or down — (sourceId, isThrottled, quality, maxDimension) — so the UI
    /// can honestly show when a source is running below its configured target instead of that
    /// happening silently. Fired from whichever thread triggered the step (a capture thread for a
    /// step-down, the broadcast timer thread for a step-up); subscribers must marshal themselves.</summary>
    public event Action<string, bool, int, int>? AdaptiveSettingsChanged;

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
        // Video gets a ~384KB per-subscriber byte budget: bursts of H.264 chunks buffer (a drop
        // would corrupt the picture until the next keyframe) while total backlog — and therefore
        // standing latency — stays bounded (~0.4s on an 8Mbps link). Large JPEG fallback frames
        // exceed the budget and naturally evict older ones, so latest-frame-wins still holds
        // there with no mode switch. Audio uses budget 0 (strict latest-wins) — chunks are
        // independent and timestamped, and the Agent's AudioSyncGapFiller silence-fills drops.
        MediaHub = new MediaHub(Log, subscriberQueueBudgetBytes: 384 * 1024);
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
        // message per mouse-move event (spec section 37: don't flood the network). Also drives
        // AdaptiveEncodingController's step-up recovery check — cheap enough to piggyback on the
        // same timer rather than adding a second one just for that.
        _broadcastTimer = new System.Threading.Timer(_ =>
        {
            // An unhandled exception in a System.Threading.Timer callback takes down the whole
            // process — never let one escape.
            try
            {
                FlushDirtyTransforms();
                TickAdaptiveControllers();
            }
            catch (Exception ex)
            {
                Log.Write(LogCategory.Error, "MasterHost", "Broadcast timer tick failed", ex);
            }
        }, null, BroadcastInterval, BroadcastInterval);

        MediaHub.FrameDropped += OnVideoFrameDropped;
    }

    /// <summary>A subscriber of this video source couldn't keep up with the last frame — the
    /// real-world signal that current quality/resolution exceeds what its link can carry. Steps
    /// the source's <see cref="AdaptiveEncodingController"/> down if this is the 3rd drop within
    /// its rolling window; see that class for why quality is reduced before resolution.</summary>
    private void OnVideoFrameDropped(string sourceId)
    {
        AdaptiveEncodingController? controller;
        bool steppedDown;
        int quality, maxDimension;
        lock (_adaptiveLock)
        {
            if (!_adaptiveControllers.TryGetValue(sourceId, out controller))
            {
                return;
            }

            steppedDown = controller.RecordDrop(DateTimeOffset.UtcNow);
            quality = controller.CurrentQuality;
            maxDimension = controller.CurrentMaxDimension;
        }

        if (steppedDown)
        {
            // Never restart from this thread: FrameDropped fires synchronously inside
            // BroadcastFrameAsync, which in H.264 mode runs on the encoder's own stdout-pump
            // thread — restarting there means H264VideoEncoder.Dispose waiting on the very task
            // that's calling it (a guaranteed timeout-length video stall), plus lifecycle
            // mutations on a hot media thread. A thread-pool hop makes the restart safe and
            // keeps the pump moving.
            _ = Task.Run(() => RestartCaptureWithAdaptiveSettings(sourceId, quality, maxDimension));
        }
    }

    private void TickAdaptiveControllers()
    {
        List<(string SourceId, int Quality, int MaxDimension)>? toRestart = null;
        lock (_adaptiveLock)
        {
            foreach (var (sourceId, controller) in _adaptiveControllers)
            {
                if (controller.Tick(DateTimeOffset.UtcNow))
                {
                    (toRestart ??= []).Add((sourceId, controller.CurrentQuality, controller.CurrentMaxDimension));
                }
            }
        }

        if (toRestart is null)
        {
            return;
        }

        foreach (var (sourceId, quality, maxDimension) in toRestart)
        {
            RestartCaptureWithAdaptiveSettings(sourceId, quality, maxDimension);
        }
    }

    /// <summary>Restarts a capture at auto-adjusted settings without touching the source's saved
    /// Config — deliberately separate from <see cref="UpdateCaptureSettings"/>, which is the
    /// explicit-user-edit path and persists to Config so it survives a resync. An automatic,
    /// transient throttle shouldn't silently overwrite what the operator actually configured.</summary>
    private void RestartCaptureWithAdaptiveSettings(string sourceId, int quality, int maxDimension)
    {
        lock (_capturesLock)
        {
            if (!_activeCaptures.TryGetValue(sourceId, out var existing))
            {
                return;
            }

            var targetWindow = existing.TargetWindow;
            var fps = existing.TargetFps;
            existing.Stop();
            existing.Dispose();
            StartCapture(sourceId, targetWindow, fps, maxDimension, quality);
        }

        bool isThrottled;
        lock (_adaptiveLock)
        {
            isThrottled = _adaptiveControllers.TryGetValue(sourceId, out var controller) && controller.IsThrottled;
        }

        AdaptiveSettingsChanged?.Invoke(sourceId, isThrottled, quality, maxDimension);
        Log.Write(LogCategory.Media, "MasterHost",
            $"Auto-adjusted capture {sourceId} to quality={quality} / {maxDimension}px ({(isThrottled ? "link can't sustain target settings" : "recovered toward target")})");
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
            try
            {
                Server.Start(Settings.ControlPort, bindAllInterfaces: false);
            }
            catch (HttpListenerException ex)
            {
                // Even localhost failed — almost always "port already in use" (a second Master
                // instance). Surface it instead of crashing at startup; the UI stays usable for
                // reading the warning and closing the duplicate.
                _startupWarning =
                    $"Port {Settings.ControlPort} is unavailable (is another Master already running on this PC?). " +
                    "Nodes cannot connect until this instance owns the port — close the other instance and restart.";
                Log.Write(LogCategory.Error, "MasterHost", _startupWarning, ex);
            }
        }
        catch (HttpListenerException ex)
        {
            _startupWarning =
                $"Port {Settings.ControlPort} is unavailable (is another Master already running on this PC?). " +
                "Nodes cannot connect until this instance owns the port — close the other instance and restart.";
            Log.Write(LogCategory.Error, "MasterHost", _startupWarning, ex);
        }

        Log.Write(LogCategory.Info, "MasterHost", $"Master started on port {Settings.ControlPort}");
    }

    /// <summary>Starts a live capture — either one specific app/window (pass <paramref name="window"/>,
    /// picked via <see cref="TradeFix.Sources.Capture.WindowEnumerator"/>) or, if null, the whole
    /// primary monitor — and adds it as a source. Frames stream to every subscribed node over
    /// <see cref="MediaHub"/> — a separate channel from the JSON control messages this same class
    /// broadcasts for the source's position/size (spec section 38). Each call starts an
    /// independent capture, so multiple different apps can be captured side by side.</summary>
    public SourceDefinition AddCaptureSource(CapturableWindow? window) =>
        AddCaptureSourceForWindow(window, window?.Title ?? "Screen Capture", browserUrl: null);

    /// <summary>Launches a dedicated, capture-friendly Chrome/Edge window for <paramref name="url"/>
    /// (see <see cref="BrowserLauncher"/> for why a dedicated launch, not the user's regular
    /// browser) and adds it as a capture source once its window appears. Returns null — logging the
    /// reason — if no Chromium browser is installed, the launch fails, or the window never shows up
    /// within a few seconds.</summary>
    public async Task<SourceDefinition?> AddBrowserCaptureSource(string url)
    {
        var browserExePath = BrowserLauncher.FindBrowserExecutable();
        if (browserExePath is null)
        {
            Log.Write(LogCategory.Error, "MasterHost", "Could not add browser source: no Chrome or Edge installation found.");
            return null;
        }

        var profileDirectory = Path.Combine(AppPaths.DataRoot("Master"), "BrowserProfiles", Guid.NewGuid().ToString("n"));
        Process? process;
        try
        {
            process = BrowserLauncher.LaunchCaptureFriendlyBrowser(browserExePath, url, profileDirectory);
        }
        catch (Exception ex)
        {
            Log.Write(LogCategory.Error, "MasterHost", $"Failed to launch browser for {url}", ex);
            return null;
        }

        if (process is null)
        {
            Log.Write(LogCategory.Error, "MasterHost", $"Failed to launch browser for {url}");
            return null;
        }

        CapturableWindow? window = null;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (window is null && DateTime.UtcNow < deadline)
        {
            window = WindowEnumerator.FindWindowForProcess(process.Id);
            if (window is null)
            {
                await Task.Delay(200);
            }
        }

        if (window is null)
        {
            Log.Write(LogCategory.Error, "MasterHost", $"Browser launched for {url} but its window never appeared within 8s.");
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best-effort — nothing more useful to do if this fails too
            }

            return null;
        }

        var name = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        return AddCaptureSourceForWindow(window, name, url);
    }

    private SourceDefinition AddCaptureSourceForWindow(CapturableWindow? window, string name, string? browserUrl)
    {
        const int defaultFps = 12;
        const int defaultMaxDimension = 3840;
        const int defaultQuality = 100;
        const bool defaultIncludeAudio = true;

        var config = JsonSerializer.SerializeToElement(
            new { live = true, windowTitle = window?.Title, fps = defaultFps, maxDimension = defaultMaxDimension, quality = defaultQuality, audio = defaultIncludeAudio, browserUrl },
            ProtocolSerializer.Options);
        var source = Project.AddSource(SourceType.DisplayCapture, name, config,
            new Transform2D { X = 200, Y = 120, Width = 640, Height = 360 });

        StartCapture(source.Id, window?.Handle, defaultFps, defaultMaxDimension, defaultQuality);
        if (defaultIncludeAudio)
        {
            StartAudioCapture(source.Id);
        }

        lock (_adaptiveLock)
        {
            _adaptiveControllers[source.Id] = new AdaptiveEncodingController(defaultQuality, defaultMaxDimension, DateTimeOffset.UtcNow);
        }

        Log.Write(LogCategory.Media, "MasterHost", $"Started capture '{name}' for source {source.Id}");
        return source;
    }

    /// <summary>Restarts an existing capture at new FPS/quality/audio settings without changing
    /// which window it targets, and updates the source's Config so the new settings survive a
    /// resync (e.g. a node reconnecting) and show correctly if the Properties panel is reopened.</summary>
    public void UpdateCaptureSettings(string sourceId, int fps, int maxDimension, int quality, bool includeAudio)
    {
        // The Properties panel binds these to raw TextBoxes — clamp here so a typo ("120" fps,
        // "99999" px) can't drive the capture pipeline into absurd territory.
        fps = Math.Clamp(fps, 1, 60);
        maxDimension = Math.Clamp(maxDimension, 320, 3840);
        quality = Math.Clamp(quality, 1, 100);

        var currentSource = Project.ActiveSceneSources.FirstOrDefault(s => s.Id == sourceId);
        string? windowTitle = currentSource is { Config.ValueKind: JsonValueKind.Object } &&
            currentSource.Config.TryGetProperty("windowTitle", out var titleProp) && titleProp.ValueKind == JsonValueKind.String
                ? titleProp.GetString()
                : null;

        lock (_capturesLock)
        {
            if (!_activeCaptures.TryGetValue(sourceId, out var existing))
            {
                return;
            }

            var targetWindow = existing.TargetWindow;
            existing.Stop();
            existing.Dispose();
            StartCaptureCore(sourceId, targetWindow, fps, maxDimension, quality);

            StopAudioCapture(sourceId);
            if (includeAudio)
            {
                StartAudioCapture(sourceId);
            }
        }

        var config = JsonSerializer.SerializeToElement(
            new { live = true, windowTitle, fps, maxDimension, quality, audio = includeAudio },
            ProtocolSerializer.Options);
        Project.UpdateConfig(sourceId, config);

        // An explicit user edit always wins over any in-progress auto-throttling.
        lock (_adaptiveLock)
        {
            if (_adaptiveControllers.TryGetValue(sourceId, out var controller))
            {
                controller.SetTarget(quality, maxDimension, DateTimeOffset.UtcNow);
            }
            else
            {
                _adaptiveControllers[sourceId] = new AdaptiveEncodingController(quality, maxDimension, DateTimeOffset.UtcNow);
            }
        }

        Log.Write(LogCategory.Media, "MasterHost", $"Restarted capture {sourceId} at {fps} FPS / {maxDimension}px / quality={quality} / audio={includeAudio}");
    }

    private void StartCapture(string sourceId, IntPtr? targetWindow, int fps, int maxDimension, int quality)
    {
        // Reentrant under _capturesLock when called from the restart paths (C# locks are
        // reentrant on the same thread); direct callers acquire it here.
        lock (_capturesLock)
        {
            StartCaptureCore(sourceId, targetWindow, fps, maxDimension, quality);
        }
    }

    private void StartCaptureCore(string sourceId, IntPtr? targetWindow, int fps, int maxDimension, int quality)
    {
        StopEncoder(sourceId);

        var capture = new ScreenCaptureService(fps, maxDimension, targetWindow, quality);
        var ffmpegPath = _ffmpegBroken ? null : FfmpegLocator.Find();

        if (ffmpegPath is not null)
        {
            // H.264 mode (the quality fix): raw frames → ffmpeg → compressed stream chunks.
            // Roughly an order of magnitude better quality per byte than the JPEG-per-frame
            // fallback below, because H.264 only spends bits on what changed between frames.
            var encoder = new H264VideoEncoder(ffmpegPath, fps, MapQualityToCrf(quality));
            encoder.EncodedDataAvailable += chunk =>
                _ = MediaHub.BroadcastFrameAsync(sourceId, chunk, CancellationToken.None);
            encoder.StreamRestarted += () =>
                _ = MediaHub.BroadcastFrameAsync(sourceId, H264StreamProtocol.RestartMarker, CancellationToken.None, neverDrop: true);
            encoder.Failed += () =>
            {
                Log.Write(LogCategory.Error, "MasterHost",
                    $"H.264 encoder for {sourceId} failed repeatedly — falling back to JPEG for all captures");
                _ffmpegBroken = true;
                // Restart from outside the capture loop's own frame callback.
                _ = Task.Run(() => RestartCaptureWithCurrentSettings(sourceId));
            };
            _activeEncoders[sourceId] = encoder;

            // A (re)connecting stream is always a fresh sequence — tell existing subscribers to
            // reset their decoders (no-op when there are none, e.g. a brand-new source).
            _ = MediaHub.BroadcastFrameAsync(sourceId, H264StreamProtocol.RestartMarker, CancellationToken.None, neverDrop: true);

            var previewInterval = Math.Max(1, fps);
            var frameCounter = 0;
            capture.RawFrameCaptured += async (bgra, width, height) =>
            {
                // Self-preview at ~1fps: there's no JPEG anywhere in this pipeline to reuse, and
                // each BMP wrap is a full uncompressed frame copy (~33MB at 4K) — at several per
                // second that's hundreds of MB/s of pure allocation churn for what is only a
                // monitoring thumbnail on the Master's own canvas. Remote nodes get full-rate
                // H.264 regardless; this only throttles the local preview.
                if (LocalCaptureFrame is not null && frameCounter++ % previewInterval == 0)
                {
                    LocalCaptureFrame.Invoke(sourceId, BgraBmp.Encode(bgra, width, height));
                }

                await encoder.WriteFrameAsync(bgra, width, height, CancellationToken.None);
            };
        }
        else
        {
            capture.FrameCaptured += bytes =>
            {
                // Master already has these bytes in-process — no need to round-trip through its
                // own MediaHub/WebSocket to preview what it's sending, unlike a remote node.
                LocalCaptureFrame?.Invoke(sourceId, bytes);
                return MediaHub.BroadcastFrameAsync(sourceId, bytes, CancellationToken.None);
            };
        }

        _activeCaptures[sourceId] = capture;
        capture.Start();
    }

    /// <summary>Maps the user-facing 1-100 quality setting onto x264's CRF scale (lower = better):
    /// 100 → 16 (near-visually-lossless), 70 → 24, 40 → 32. The same setting the adaptive
    /// controller steps under network pressure keeps working in H.264 mode — it just moves CRF
    /// instead of JPEG quantization now.</summary>
    private static int MapQualityToCrf(int quality) =>
        Math.Clamp(16 + (100 - quality) * 16 / 60, 16, 36);

    private void RestartCaptureWithCurrentSettings(string sourceId)
    {
        lock (_capturesLock)
        {
            if (!_activeCaptures.TryGetValue(sourceId, out var existing))
            {
                return;
            }

            var targetWindow = existing.TargetWindow;
            var fps = existing.TargetFps;
            var maxDimension = existing.MaxDimension;
            var quality = existing.Quality;
            existing.Stop();
            existing.Dispose();
            StartCaptureCore(sourceId, targetWindow, fps, maxDimension, quality);
        }
    }

    private void StopEncoder(string sourceId)
    {
        lock (_capturesLock)
        {
            if (_activeEncoders.Remove(sourceId, out var encoder))
            {
                encoder.Dispose();
            }
        }
    }

    private void StartAudioCapture(string sourceId)
    {
        lock (_capturesLock)
        {
            _audioEnabledSources.Add(sourceId);
            if (_sharedAudioCapture is not null)
            {
                return; // already running for another source — every source hears the same desktop audio
            }

            try
            {
                var audioCapture = new AudioCaptureService();
                audioCapture.ChunkCaptured += (bytes, timestampMs) =>
                    AudioHub.BroadcastFrameAsync(SharedAudioSourceId, AudioChunkFraming.Encode(timestampMs, bytes), CancellationToken.None);
                audioCapture.Start();
                _sharedAudioCapture = audioCapture;
            }
            catch (Exception ex)
            {
                // WASAPI loopback throws when this PC has no default audio output device
                // (headless box, RDP without audio, everything unplugged). That must degrade to
                // silent video, not crash the whole broadcast on an "Add Capture Source" click.
                Log.Write(LogCategory.Error, "MasterHost", "No audio output device available — capture continues without audio", ex);
            }
        }
    }

    private void StopAudioCapture(string sourceId)
    {
        lock (_capturesLock)
        {
            if (!_audioEnabledSources.Remove(sourceId) || _audioEnabledSources.Count > 0)
            {
                return; // other sources still want audio — keep the shared capture running
            }

            _sharedAudioCapture?.Stop();
            _sharedAudioCapture?.Dispose();
            _sharedAudioCapture = null;
        }
    }

    /// <summary>Removes a source, stopping any live capture (video and audio) it owns first.</summary>
    public void RemoveSource(string sourceId)
    {
        lock (_capturesLock)
        {
            if (_activeCaptures.Remove(sourceId, out var capture))
            {
                capture.Stop();
                capture.Dispose();
            }

            StopEncoder(sourceId);
            StopAudioCapture(sourceId);
        }

        lock (_adaptiveLock)
        {
            _adaptiveControllers.Remove(sourceId);
        }

        Log.Write(LogCategory.Media, "MasterHost", $"Stopped capture (if any) and removed source {sourceId}");
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
        // Stop the restart triggers before tearing captures down, so nothing races the cleanup
        // below into starting a fresh capture/encoder mid-shutdown.
        MediaHub.FrameDropped -= OnVideoFrameDropped;
        await _broadcastTimer.DisposeAsync();

        lock (_capturesLock)
        {
            foreach (var capture in _activeCaptures.Values)
            {
                capture.Stop();
                capture.Dispose();
            }

            _activeCaptures.Clear();

            foreach (var encoder in _activeEncoders.Values)
            {
                encoder.Dispose();
            }

            _activeEncoders.Clear();

            _sharedAudioCapture?.Stop();
            _sharedAudioCapture?.Dispose();
            _sharedAudioCapture = null;
            _audioEnabledSources.Clear();
        }

        foreach (var simulator in _simulators)
        {
            await simulator.DisposeAsync();
        }

        await Server.DisposeAsync();
    }
}
