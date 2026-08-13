using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradeFix.Common.Logging;
using TradeFix.Master.Services;
using TradeFix.Network;
using TradeFix.Network.Auth;
using TradeFix.Protocol;
using TradeFix.Shared.Enums;
using TradeFix.Shared.Models;

namespace TradeFix.Master.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly MasterHost _host;
    private readonly Dispatcher _dispatcher;
    private int _simulatedNodeCount;
    private int _sceneCount;

    /// <summary>One <see cref="LiveFramePump"/> per capture source, so decoding each frame for
    /// Master's own live preview happens off the UI thread instead of blocking the capture loop
    /// (see that class's remarks). Guarded by <see cref="_framePumpsLock"/>: each active capture
    /// runs its own background capture-loop thread, so this dictionary can be touched concurrently
    /// from several threads at once, plus <see cref="RebuildFromProject"/> on the UI thread.</summary>
    private readonly Dictionary<string, LiveFramePump> _framePumps = new();
    private readonly object _framePumpsLock = new();

    public ObservableCollection<NodeViewModel> Nodes { get; } = [];
    public ObservableCollection<string> RecentLogLines { get; } = [];
    public ObservableCollection<SceneItemViewModel> Scenes { get; } = [];
    public ObservableCollection<SourceItemViewModel> ActiveSceneSources { get; } = [];

    /// <summary>Transient node online/offline notifications, rendered as a toast stack in the
    /// window's bottom-right corner. See <see cref="NotifyNodePresenceChange"/>.</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    /// <summary>Last known connected-ness per node, so only genuine online/offline *transitions*
    /// toast (not every heartbeat state refresh). UI-thread only.</summary>
    private readonly Dictionary<string, bool> _lastKnownConnected = new();

    [ObservableProperty] private string? _activePairingCode;
    [ObservableProperty] private string? _startupWarning;
    [ObservableProperty] private string _serverSummary = string.Empty;
    [ObservableProperty] private SourceItemViewModel? _selectedSource;

    /// <summary>Label of the warning banner's action button; null hides the button (warnings with
    /// no clickable remedy, e.g. a port conflict). Set alongside <see cref="StartupWarning"/> by
    /// the network health check.</summary>
    [ObservableProperty] private string? _networkFixLabel;
    [ObservableProperty] private bool _isFixingNetwork;

    private enum NetworkIssue { None, WindowsBlocking, TailscaleSignedOut, TailscaleUnreachable, PortConflict }
    private NetworkIssue _networkIssue;
    private bool _healthCheckRunning;

    public MainViewModel(MasterHost host, Dispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;

        ServerSummary = $"{host.Settings.ServerName}  ·  port {host.Server.Port}";

        // Self-healing network setup: detect what's blocking nodes from reaching this Master
        // (Windows URL ACL/firewall, Tailscale off or signed out, no internet), fix what's
        // fixable without a human, and reduce the rest to one button. The startup pass auto-runs
        // the elevated fix so a fresh Master PC works after a single UAC "Yes"; the timer keeps
        // watching so e.g. someone quitting Tailscale mid-show surfaces (and heals) within seconds.
        _ = RunNetworkHealthCheckAsync(autoFixWindows: true);
        var healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        healthTimer.Tick += (_, _) => _ = RunNetworkHealthCheckAsync(autoFixWindows: false);
        healthTimer.Start();

        var selfInfo = new NodeInfo
        {
            NodeId = "self",
            Name = host.Settings.ServerName,
            Role = NodeRole.Master,
            AppVersion = MasterHost.AppVersion,
            ProtocolVersion = ProtocolVersion.Current,
            ConnectionState = NodeConnectionState.Online,
            LastHeartbeatAt = DateTimeOffset.UtcNow
        };
        host.Registry.Upsert(selfInfo);

        foreach (var node in host.Registry.All)
        {
            Nodes.Add(new NodeViewModel(node));
        }

        host.Registry.NodeChanged += OnNodeChanged;
        host.Registry.NodeRemoved += OnNodeRemoved;
        host.LogSink.EntryAdded += OnLogEntryAdded;
        host.Project.Changed += () => _ = _dispatcher.InvokeAsync(RebuildFromProject);
        host.LocalCaptureFrame += (sourceId, jpegBytes) =>
        {
            LiveFramePump pump;
            lock (_framePumpsLock)
            {
                if (!_framePumps.TryGetValue(sourceId, out pump!))
                {
                    pump = new LiveFramePump(_dispatcher, SourceItemViewModel.DecodeJpeg, decoded =>
                    {
                        var item = ActiveSceneSources.FirstOrDefault(s => s.Id == sourceId);
                        item?.ApplyLiveFrame(decoded);
                    });
                    _framePumps[sourceId] = pump;
                }
            }

            pump.Post(jpegBytes);
        };

        host.AdaptiveSettingsChanged += (sourceId, isThrottled, quality, maxDimension) => _ = _dispatcher.InvokeAsync(() =>
        {
            var item = ActiveSceneSources.FirstOrDefault(s => s.Id == sourceId);
            if (item is null)
            {
                return;
            }

            item.AdaptiveStatusText = isThrottled
                ? $"Auto-reduced to quality {quality}, {maxDimension}px — a subscriber's connection can't keep up at your configured settings"
                : null;
        });

        // Zero-typing pairing: an unpaired node that discovered this Master sends a code-less
        // join request; the operator just clicks Allow here. Codes stay as the manual fallback.
        host.Server.JoinApprover = (nodeName, remoteAddress) =>
        {
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = _dispatcher.InvokeAsync(() =>
            {
                var allow = System.Windows.MessageBox.Show(
                    $"\"{nodeName}\" wants to join this broadcast as a render node.\n\nAllow it to connect?",
                    "TradeFix Broadcast — New Node", System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;
                decision.TrySetResult(allow);
            });
            return decision.Task;
        };

        foreach (var entry in host.LogSink.Snapshot().TakeLast(50))
        {
            RecentLogLines.Add(Format(entry));
        }

        _sceneCount = host.Project.Scenes.Count;
        RebuildFromProject();
    }

    /// <summary>One pass of the network self-check. Order matters: a blocked listener (nodes can't
    /// connect at all) outranks Tailscale problems (only cross-network nodes affected). A stopped
    /// Tailscale is reconnected silently here — no banner, no clicks — because <c>tailscale up</c>
    /// needs no elevation; only sign-in genuinely requires the operator.</summary>
    private async Task RunNetworkHealthCheckAsync(bool autoFixWindows)
    {
        if (_healthCheckRunning || IsFixingNetwork)
        {
            return;
        }

        _healthCheckRunning = true;
        try
        {
            if (_host.StartupWarning is { } hostWarning && !_host.IsLocalhostOnly)
            {
                // Port conflict etc. — informational; no button can fix a second running Master.
                _networkIssue = NetworkIssue.PortConflict;
                StartupWarning = hostWarning;
                NetworkFixLabel = null;
                return;
            }

            if (_host.IsLocalhostOnly)
            {
                _networkIssue = NetworkIssue.WindowsBlocking;
                StartupWarning = _host.StartupWarning;
                NetworkFixLabel = "Fix connections";
                if (autoFixWindows)
                {
                    _healthCheckRunning = false;
                    await FixNetworkAsync();
                }

                return;
            }

            var tailscale = await TailscaleHealth.GetStatusAsync();
            if (tailscale.State == TailscaleBackendState.Stopped)
            {
                if (await TailscaleHealth.TryStartAsync())
                {
                    tailscale = await TailscaleHealth.GetStatusAsync();
                    if (tailscale.State == TailscaleBackendState.Running)
                    {
                        ShowToast("Reconnected Tailscale — nodes on other networks can reach this Master again", positive: true);
                    }
                }
            }

            switch (tailscale.State)
            {
                case TailscaleBackendState.NeedsLogin:
                    _networkIssue = NetworkIssue.TailscaleSignedOut;
                    StartupWarning =
                        "Tailscale (the service that connects PCs on different networks) is signed out on this PC — " +
                        "render nodes elsewhere can't reach this Master until it's signed in.";
                    NetworkFixLabel = "Sign in to Tailscale";
                    break;
                case TailscaleBackendState.Stopped:
                    _networkIssue = NetworkIssue.TailscaleUnreachable;
                    StartupWarning =
                        "Tailscale is turned off on this PC and couldn't be started — render nodes on other " +
                        "networks can't reach this Master.";
                    NetworkFixLabel = "Try again";
                    break;
                case TailscaleBackendState.Running when !tailscale.SelfOnline:
                    _networkIssue = NetworkIssue.TailscaleUnreachable;
                    StartupWarning =
                        "This PC doesn't seem to be connected to the internet — render nodes on other networks " +
                        "can't reach it. Check the Wi-Fi/network connection.";
                    NetworkFixLabel = "Check again";
                    break;
                default:
                    // Running and online, or Tailscale simply not installed (fine for same-LAN
                    // setups; the installer already points cross-network users at it).
                    if (_networkIssue != NetworkIssue.None)
                    {
                        ShowToast("Network is healthy — nodes can reach this Master", positive: true);
                    }

                    _networkIssue = NetworkIssue.None;
                    StartupWarning = null;
                    NetworkFixLabel = null;
                    break;
            }
        }
        catch
        {
            // the health check must never take the app down — worst case the banner is stale 20s
        }
        finally
        {
            _healthCheckRunning = false;
        }
    }

    [RelayCommand]
    private async Task FixNetworkAsync()
    {
        if (IsFixingNetwork)
        {
            return;
        }

        IsFixingNetwork = true;
        try
        {
            switch (_networkIssue)
            {
                case NetworkIssue.WindowsBlocking:
                    var fixApplied = await MasterNetworkSetup.RunElevatedAsync(
                        _host.Settings.ControlPort, TradeFix.Network.Discovery.DiscoveryProtocol.Port);
                    if (fixApplied && _host.TryUpgradeToPublicListener())
                    {
                        _networkIssue = NetworkIssue.None;
                        StartupWarning = null;
                        NetworkFixLabel = null;
                        ShowToast("Connections enabled — render nodes can now find this Master", positive: true);
                    }
                    else if (!fixApplied)
                    {
                        StartupWarning =
                            "Windows permission wasn't granted, so other PCs still can't connect. " +
                            "Press Fix connections and choose Yes when Windows asks.";
                    }

                    break;
                case NetworkIssue.TailscaleSignedOut:
                    TailscaleHealth.OpenLoginFlow();
                    ShowToast("Sign in to Tailscale in the browser window that just opened", positive: true);
                    break;
            }
        }
        finally
        {
            IsFixingNetwork = false;
        }

        if (_networkIssue is NetworkIssue.TailscaleUnreachable or NetworkIssue.None)
        {
            await RunNetworkHealthCheckAsync(autoFixWindows: false);
        }
    }

    private void RebuildFromProject()
    {
        Scenes.Clear();
        foreach (var scene in _host.Project.Scenes)
        {
            Scenes.Add(new SceneItemViewModel(scene.Id, scene.Name) { IsActive = scene.Id == _host.Project.ActiveSceneId });
        }

        var selectedId = SelectedSource?.Id;
        ActiveSceneSources.Clear();
        SelectedSource = null;
        foreach (var source in _host.Project.ActiveSceneSources)
        {
            var item = new SourceItemViewModel(source);
            if (item.IsImage && item.AssetHash is not null)
            {
                item.ImagePath = _host.Assets.GetFilePath(item.AssetHash);
            }

            ActiveSceneSources.Add(item);
            if (item.Id == selectedId)
            {
                item.IsSelected = true;
                SelectedSource = item;
            }
        }

        var liveIds = ActiveSceneSources.Select(s => s.Id).ToHashSet();
        lock (_framePumpsLock)
        {
            foreach (var staleId in _framePumps.Keys.Where(id => !liveIds.Contains(id)).ToList())
            {
                _framePumps[staleId].Dispose();
                _framePumps.Remove(staleId);
            }
        }
    }

    /// <summary>Called continuously (many times per drag/resize gesture) from MainWindow's mouse
    /// handlers. Cheap local update; MasterHost's timer handles rate-limited network broadcast.</summary>
    public void UpdateSourceTransform(SourceItemViewModel item, double x, double y, double width, double height)
    {
        item.X = x;
        item.Y = y;
        item.Width = width;
        item.Height = height;

        // Preserve crop (and anything else CurrentTransform carries) — this used to rebuild a
        // bare Transform2D from scratch, silently resetting crop back to zero on every drag.
        _host.Project.UpdateTransform(item.Id, item.CurrentTransform());
    }

    /// <summary>Called continuously while dragging a crop handle (see MainWindow's
    /// CropHandle_MouseMove) — trim-style cropping moves the box AND the crop fractions together
    /// so the content underneath stays anchored; one transform broadcast carries both.</summary>
    public void UpdateSourceCropBox(SourceItemViewModel item, double x, double y, double width, double height,
        double left, double top, double right, double bottom)
    {
        item.X = x;
        item.Y = y;
        item.Width = width;
        item.Height = height;
        item.CropLeft = left;
        item.CropTop = top;
        item.CropRight = right;
        item.CropBottom = bottom;

        _host.Project.UpdateTransform(item.Id, item.CurrentTransform());
    }

    public void SelectSource(SourceItemViewModel? item)
    {
        if (SelectedSource is not null)
        {
            SelectedSource.IsSelected = false;
        }

        SelectedSource = item;
        if (item is not null)
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand]
    private void AddScene()
    {
        _sceneCount++;
        _host.Project.AddScene($"Scene {_sceneCount}");
    }

    [RelayCommand]
    private void SwitchScene(SceneItemViewModel scene) => _host.Project.SwitchScene(scene.Id);

    [RelayCommand]
    private void AddColorSource()
    {
        var config = JsonSerializer.SerializeToElement(new { color = "#3E8EF7" }, ProtocolSerializer.Options);
        _host.Project.AddSource(SourceType.Background, "Color Box", config);
    }

    [RelayCommand]
    private void AddTextSource()
    {
        var config = JsonSerializer.SerializeToElement(new { text = "New Text", color = "#FFFFFF" }, ProtocolSerializer.Options);
        _host.Project.AddSource(SourceType.Text, "Text", config, new Transform2D { X = 300, Y = 200, Width = 300, Height = 80 });
    }

    [RelayCommand]
    private void AddImageSource()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add Image Source",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|All files|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string hash;
        try
        {
            hash = _host.Assets.SaveFile(dialog.FileName);
        }
        catch (Exception ex)
        {
            // Locked file, vanished network share, unreadable media — must not crash the UI thread.
            System.Windows.MessageBox.Show(
                $"Couldn't read that file:\n{ex.Message}",
                "Add Image Source", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        var fileName = System.IO.Path.GetFileName(dialog.FileName);
        var config = JsonSerializer.SerializeToElement(new { assetHash = hash, fileName }, ProtocolSerializer.Options);
        _host.Project.AddSource(SourceType.Image, fileName, config, new Transform2D { X = 300, Y = 200, Width = 320, Height = 240 });
    }

    /// <summary>Live app mirroring (video only — audio is a separate follow-up). Lets the
    /// operator pick a specific window to capture; every connected node shows it live, cursor
    /// included. Each call is an independent capture, so multiple different apps can run side by
    /// side.</summary>
    [RelayCommand]
    private void AddCaptureSource()
    {
        var dialog = new WindowPickerDialog { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Selected is null)
        {
            return;
        }

        _host.AddCaptureSource(dialog.Selected);
    }

    /// <summary>Whole-screen fallback if the operator doesn't want to pick a specific app.</summary>
    [RelayCommand]
    private void AddFullScreenCaptureSource() => _host.AddCaptureSource(window: null);

    /// <summary>Launches a dedicated, capture-friendly browser window for a URL instead of
    /// requiring the operator to already have one open to pick — see MasterHost.AddBrowserCaptureSource
    /// and BrowserLauncher for why: a regular already-open browser window pauses its own rendering
    /// once covered by another window, which is what makes a captured browser look "frozen."</summary>
    [RelayCommand]
    private async Task AddBrowserSource()
    {
        var dialog = new AddBrowserSourceDialog { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Url is null)
        {
            return;
        }

        var source = await _host.AddBrowserCaptureSource(dialog.Url);
        if (source is null)
        {
            System.Windows.MessageBox.Show(
                $"Couldn't add a browser source for {dialog.Url}. Check the Logs panel for the reason " +
                "(no Chrome/Edge found, or the window never appeared).",
                "Add Browser Source", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void RemoveSelectedSource()
    {
        if (SelectedSource is not null)
        {
            _host.RemoveSource(SelectedSource.Id);
        }
    }

    /// <summary>Direct delete — the "✕" on each box — independent of whether that box is
    /// currently selected. Requested explicitly: selecting-then-using-the-properties-panel wasn't
    /// obvious enough as the only way to remove a source.</summary>
    [RelayCommand]
    private void RemoveSource(SourceItemViewModel item) => _host.RemoveSource(item.Id);

    /// <summary>Pushes the selected source's currently-edited color/text fields to every node.
    /// Deliberately explicit (a button) rather than live-on-keystroke, so typing a sentence
    /// doesn't send a network message per character.</summary>
    [RelayCommand]
    private void ApplySelectedSourceProperties()
    {
        if (SelectedSource is null)
        {
            return;
        }

        // Only source types whose config genuinely IS these fields may be overwritten here. The
        // old check ("anything but capture") let Apply replace an Image source's
        // { assetHash, fileName } config with { color } — permanently destroying the image on
        // every node with one click.
        if (SelectedSource.Type is SourceType.Text or SourceType.Background)
        {
            var config = SelectedSource.Type == SourceType.Text
                ? JsonSerializer.SerializeToElement(new { text = SelectedSource.TextContent, color = SelectedSource.ColorHex }, ProtocolSerializer.Options)
                : JsonSerializer.SerializeToElement(new { color = SelectedSource.ColorHex }, ProtocolSerializer.Options);

            _host.Project.UpdateConfig(SelectedSource.Id, config);
        }
    }

    /// <summary>Applies the crop percentages currently entered in the Properties panel. Separate
    /// from <see cref="ApplySelectedSourceProperties"/> since crop is part of the source's
    /// Transform (broadcast via UPDATE_SOURCE), not its type-specific Config (broadcast via a
    /// full LOAD_SCENE) — different network paths, matching how drag/resize already works.</summary>
    [RelayCommand]
    private void ApplySelectedSourceCrop()
    {
        if (SelectedSource is null)
        {
            return;
        }

        _host.Project.UpdateTransform(SelectedSource.Id, SelectedSource.CurrentTransform());
    }

    /// <summary>Restarts the selected capture at newly-entered FPS/quality settings — a live
    /// setting change, not something that requires deleting and re-adding the source.</summary>
    [RelayCommand]
    private void ApplyCaptureSettings()
    {
        if (SelectedSource is not { Type: SourceType.DisplayCapture })
        {
            return;
        }

        _host.UpdateCaptureSettings(SelectedSource.Id, SelectedSource.CaptureFps, SelectedSource.CaptureMaxDimension, SelectedSource.CaptureQuality, SelectedSource.CaptureIncludeAudio);
    }

    [RelayCommand]
    private void PairNewNode()
    {
        var code = _host.Pairing.IssueCode();
        var address = TradeFix.Network.NetworkAddressHelper.GetBestAdvertisableAddress();
        ActivePairingCode = ConnectCode.Format(code, address, _host.Server.Port);
    }

    [RelayCommand]
    private void CopyConnectCode()
    {
        if (string.IsNullOrEmpty(ActivePairingCode))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(ActivePairingCode);
        }
        catch (Exception)
        {
            // Clipboard.SetText throws CLIPBRD_E_CANT_OPEN whenever another process (clipboard
            // managers, RDP) momentarily holds the clipboard — a classic WPF crash. One retry
            // covers the common transient case; beyond that the code is still on screen to copy
            // by hand.
            try
            {
                System.Windows.Clipboard.SetText(ActivePairingCode);
            }
            catch (Exception)
            {
                // still held — the visible code remains selectable manually
            }
        }
    }

    [RelayCommand]
    private void AddSimulatedNode()
    {
        _simulatedNodeCount++;
        _host.AddSimulatedNode($"Simulated PC{_simulatedNodeCount + 1}");
    }

    // These three handlers run inline inside network receive loops / the log bus on background
    // threads. They use fire-and-forget InvokeAsync, never blocking Invoke: a blocking Invoke
    // here would stall every node's session receive loop (and, in one measured chain, mutually
    // deadlock with an encoder teardown for the full 3s timeout) whenever the UI thread is busy.
    private void OnNodeChanged(NodeInfo node)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            var existing = Nodes.FirstOrDefault(n => n.NodeId == node.NodeId);
            if (existing is null)
            {
                Nodes.Add(new NodeViewModel(node));
            }
            else
            {
                existing.Apply(node);
            }

            NotifyNodePresenceChange(node);
        });
    }

    /// <summary>Toasts a node's online/offline transitions ("self" excluded — the Master
    /// announcing its own presence to its own operator would be noise). Only actual crossings of
    /// the online/offline boundary toast; heartbeat-driven state churn between the various
    /// connected states (Online/Syncing/Synced) stays silent.</summary>
    private void NotifyNodePresenceChange(NodeInfo node)
    {
        if (node.NodeId == "self")
        {
            return;
        }

        var isConnected = node.ConnectionState is NodeConnectionState.Online
            or NodeConnectionState.Syncing or NodeConnectionState.Synced;
        var wasConnected = _lastKnownConnected.TryGetValue(node.NodeId, out var previous) && previous;
        _lastKnownConnected[node.NodeId] = isConnected;

        if (isConnected && !wasConnected)
        {
            ShowToast($"{node.Name} is online", positive: true);
        }
        else if (!isConnected && wasConnected && node.ConnectionState == NodeConnectionState.Offline)
        {
            ShowToast($"{node.Name} went offline", positive: false);
        }
    }

    private void ShowToast(string message, bool positive)
    {
        var toast = new ToastViewModel { Message = message, IsPositive = positive };
        Toasts.Add(toast);
        while (Toasts.Count > 4)
        {
            Toasts.RemoveAt(0);
        }

        var dismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        dismiss.Tick += (_, _) =>
        {
            dismiss.Stop();
            toast.IsClosing = true;

            var remove = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            remove.Tick += (_, _) =>
            {
                remove.Stop();
                Toasts.Remove(toast);
            };
            remove.Start();
        };
        dismiss.Start();
    }

    private void OnNodeRemoved(string nodeId)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            var existing = Nodes.FirstOrDefault(n => n.NodeId == nodeId);
            if (existing is not null)
            {
                Nodes.Remove(existing);
                ShowToast($"{existing.Name} was removed", positive: false);
            }

            _lastKnownConnected.Remove(nodeId);
        });
    }

    private void OnLogEntryAdded(LogEntry entry)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            RecentLogLines.Add(Format(entry));
            while (RecentLogLines.Count > 200)
            {
                RecentLogLines.RemoveAt(0);
            }
        });
    }

    private static string Format(LogEntry entry) =>
        $"[{entry.Timestamp.LocalDateTime:HH:mm:ss}] [{entry.Category}] {entry.Message}";
}
