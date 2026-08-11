using CommunityToolkit.Mvvm.ComponentModel;
using TradeFix.Shared.Enums;
using TradeFix.Shared.Models;

namespace TradeFix.Master.ViewModels;

public sealed partial class NodeViewModel : ObservableObject
{
    public NodeViewModel(NodeInfo info) => Apply(info);

    [ObservableProperty] private string _nodeId = string.Empty;
    [ObservableProperty] private string _name = string.Empty;

    // The NotifyPropertyChangedFor attributes are load-bearing: RoleLabel/StatusLabel/
    // StatusBrushKey are computed getters over these fields, and without explicit notification
    // the node cards would render their INITIAL status forever — a node could disconnect and its
    // card would keep glowing green.
    [NotifyPropertyChangedFor(nameof(RoleLabel))]
    [ObservableProperty] private NodeRole _role;

    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(StatusBrushKey))]
    [ObservableProperty] private NodeConnectionState _connectionState;
    [ObservableProperty] private string? _ipAddress;
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _gpuPercent;
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private double _fps;
    [ObservableProperty] private double _latencyMs;
    [ObservableProperty] private string? _currentSceneId;

    public string RoleLabel => Role == NodeRole.Master ? "MASTER" : "RENDER NODE";
    public string StatusLabel => ConnectionState.ToString().ToUpperInvariant();

    public string StatusBrushKey => ConnectionState switch
    {
        NodeConnectionState.Online or NodeConnectionState.Synced => "Brush.Success",
        NodeConnectionState.Connecting or NodeConnectionState.Pairing or NodeConnectionState.Syncing => "Brush.Warning",
        NodeConnectionState.Warning => "Brush.Warning",
        NodeConnectionState.Error or NodeConnectionState.Offline => "Brush.Danger",
        _ => "Brush.TextSecondary"
    };

    public void Apply(NodeInfo info)
    {
        NodeId = info.NodeId;
        Name = info.Name;
        Role = info.Role;
        ConnectionState = info.ConnectionState;
        IpAddress = info.IpAddress;
        CpuPercent = info.Metrics.CpuPercent;
        GpuPercent = info.Metrics.GpuPercent;
        RamPercent = info.Metrics.RamPercent;
        Fps = info.Metrics.Fps;
        LatencyMs = info.Metrics.LatencyMs;
        CurrentSceneId = info.CurrentSceneId;
    }
}
