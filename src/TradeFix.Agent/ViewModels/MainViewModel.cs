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
