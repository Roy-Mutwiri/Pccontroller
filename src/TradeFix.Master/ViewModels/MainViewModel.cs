using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradeFix.Common.Logging;
using TradeFix.Master.Services;
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

    [ObservableProperty] private string? _activePairingCode;
    [ObservableProperty] private string? _startupWarning;
    [ObservableProperty] private string _serverSummary = string.Empty;
    [ObservableProperty] private SourceItemViewModel? _selectedSource;

    public MainViewModel(MasterHost host, Dispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;

        StartupWarning = host.StartupWarning;
        ServerSummary = $"{host.Settings.ServerName}  ·  port {host.Server.Port}";

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
        host.Project.Changed += () => _dispatcher.Invoke(RebuildFromProject);
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

        foreach (var entry in host.LogSink.Snapshot().TakeLast(50))
        {
            RecentLogLines.Add(Format(entry));
        }

        _sceneCount = host.Project.Scenes.Count;
        RebuildFromProject();
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
    /// CropHandle_MouseMove) — same live-update-then-network-broadcast pattern as
    /// <see cref="UpdateSourceTransform"/>, just for the four crop fractions instead of X/Y/W/H.</summary>
    public void UpdateSourceCrop(SourceItemViewModel item, double left, double top, double right, double bottom)
    {
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

        var hash = _host.Assets.SaveFile(dialog.FileName);
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
        var dialog = new WindowPickerDialog();
        if (dialog.ShowDialog() != true || dialog.Selected is null)
        {
            return;
        }

        _host.AddCaptureSource(dialog.Selected);
    }

    /// <summary>Whole-screen fallback if the operator doesn't want to pick a specific app.</summary>
    [RelayCommand]
    private void AddFullScreenCaptureSource() => _host.AddCaptureSource(window: null);

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

        if (SelectedSource.Type != SourceType.DisplayCapture)
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
        if (!string.IsNullOrEmpty(ActivePairingCode))
        {
            System.Windows.Clipboard.SetText(ActivePairingCode);
        }
    }

    [RelayCommand]
    private void AddSimulatedNode()
    {
        _simulatedNodeCount++;
        _host.AddSimulatedNode($"Simulated PC{_simulatedNodeCount + 1}");
    }

    private void OnNodeChanged(NodeInfo node)
    {
        _dispatcher.Invoke(() =>
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
        });
    }

    private void OnNodeRemoved(string nodeId)
    {
        _dispatcher.Invoke(() =>
        {
            var existing = Nodes.FirstOrDefault(n => n.NodeId == nodeId);
            if (existing is not null)
            {
                Nodes.Remove(existing);
            }
        });
    }

    private void OnLogEntryAdded(LogEntry entry)
    {
        _dispatcher.Invoke(() =>
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
