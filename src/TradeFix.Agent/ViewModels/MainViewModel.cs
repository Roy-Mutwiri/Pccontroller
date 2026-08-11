using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradeFix.Agent.Services;
using TradeFix.Common.Logging;
using TradeFix.Network.Auth;
using TradeFix.Shared.Enums;

namespace TradeFix.Agent.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AgentHost _host;
    private readonly Dispatcher _dispatcher;
    private string? _pendingParsedPairingCode;

    [ObservableProperty] private string _connectCodeInput = string.Empty;
    [ObservableProperty] private string _nodeName;
    [ObservableProperty] private NodeConnectionState _state = NodeConnectionState.Offline;
    [ObservableProperty] private string _pairingCodeInput = string.Empty;
    [ObservableProperty] private string? _nodeId;
    [ObservableProperty] private string? _connectCodeError;
    [ObservableProperty] private string? _connectedMasterSummary;

    public ObservableCollection<string> RecentLogLines { get; } = [];

    /// <summary>True once this Agent has ever successfully connected to a Master — after that,
    /// it reconnects on its own at every launch and the "paste a connect code" UI never needs to
    /// be shown again.</summary>
    public bool KnowsMaster => !string.IsNullOrEmpty(_host.Settings.MasterHost);

    public bool NeedsPairingCode => State == NodeConnectionState.Pairing;

    public MainViewModel(AgentHost host, Dispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        _nodeName = host.Settings.NodeName;

        host.LogSink.EntryAdded += OnLogEntryAdded;

        if (KnowsMaster)
        {
            // Already paired in a previous session — reconnect automatically, no user action needed.
            BeginConnect(host.Settings, autoSubmitCode: null);
        }
    }

    [RelayCommand]
    private void ConnectWithCode()
    {
        ConnectCodeError = null;

        if (!ConnectCode.TryParse(ConnectCodeInput, out var pairingCode, out var masterHost, out var masterPort))
        {
            ConnectCodeError = "That doesn't look like a valid connect code. Copy it exactly from the Master's \"Pair New Node\" panel.";
            return;
        }

        var settings = new AgentSettings { MasterHost = masterHost, MasterPort = masterPort, NodeName = NodeName };
        BeginConnect(settings, autoSubmitCode: pairingCode);
    }

    /// <summary>Fallback manual entry — used if auto-submitting the code parsed from the connect
    /// code failed (e.g. it expired between copy and paste) or the Agent is already in the
    /// Pairing state for some other reason.</summary>
    [RelayCommand]
    private void SubmitPairingCode()
    {
        if (!string.IsNullOrWhiteSpace(PairingCodeInput))
        {
            _host.Connection?.SubmitPairingCode(PairingCodeInput.Trim().ToUpperInvariant());
        }
    }

    /// <summary>Forgets this node's pairing (credentials + Master address) and returns to the
    /// first-launch "paste a connect code" state — so a node can move to a different Master
    /// without hand-deleting files.</summary>
    [RelayCommand]
    private async Task LogoutAsync()
    {
        var confirmed = System.Windows.MessageBox.Show(
            "Log out from this Master? The saved pairing is forgotten and this PC goes back to asking for a connect code.",
            "TradeFix Render Agent", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirmed != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        await _host.LogoutAsync();
        State = NodeConnectionState.Offline;
        NodeId = null;
        ConnectedMasterSummary = null;
        ConnectCodeInput = string.Empty;
        PairingCodeInput = string.Empty;
        OnPropertyChanged(nameof(KnowsMaster));
        OnPropertyChanged(nameof(NeedsPairingCode));
    }

    /// <summary>Flips this PC's role to Master: the counterpart app starts (directly, or via the
    /// Launcher when it's the one supervising) and this Agent closes.</summary>
    [RelayCommand]
    private void SwitchToMaster()
    {
        var confirmed = System.Windows.MessageBox.Show(
            "Switch this PC to Master? The render agent will close and the Master control app will start.",
            "TradeFix Render Agent", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirmed != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        var error = TradeFix.Common.RoleSwitcher.SwitchThisPc(toMaster: true);
        if (error is not null)
        {
            System.Windows.MessageBox.Show(error, "TradeFix Render Agent",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        System.Windows.Application.Current.Shutdown();
    }

    private void BeginConnect(AgentSettings settings, string? autoSubmitCode)
    {
        _pendingParsedPairingCode = autoSubmitCode;
        var connection = _host.Connect(settings);

        connection.StateChanged += state => _dispatcher.Invoke(() =>
        {
            State = state;
            NodeId = connection.Credentials?.NodeId;
            ConnectedMasterSummary = $"{settings.MasterHost}:{settings.MasterPort}";
            OnPropertyChanged(nameof(NeedsPairingCode));
            OnPropertyChanged(nameof(KnowsMaster));

            if (state == NodeConnectionState.Pairing && _pendingParsedPairingCode is not null)
            {
                connection.SubmitPairingCode(_pendingParsedPairingCode);
                _pendingParsedPairingCode = null; // one-shot; a manual retry uses PairingCodeInput instead
            }
        });
    }

    private void OnLogEntryAdded(LogEntry entry)
    {
        _dispatcher.Invoke(() =>
        {
            RecentLogLines.Add($"[{entry.Timestamp.LocalDateTime:HH:mm:ss}] [{entry.Category}] {entry.Message}");
            while (RecentLogLines.Count > 200)
            {
                RecentLogLines.RemoveAt(0);
            }
        });
    }
}
