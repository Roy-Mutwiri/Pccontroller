using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradeFix.Agent.Services;
using TradeFix.Common.Logging;
using TradeFix.Network;
using TradeFix.Network.Auth;
using TradeFix.Network.Discovery;
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

    /// <summary>Live "what is this node actually receiving/showing" readout ("14 fps · 3.2 Mbps"),
    /// refreshed once a second from AgentHost's real counters — null (hidden) while no video is
    /// flowing. The direct answer to "is it lagging?": if fps is steady and bitrate nonzero, the
    /// pipeline is healthy end to end on this node.</summary>
    [ObservableProperty] private string? _videoStats;

    private long _lastVideoBytes;
    private long _lastVideoFrames;

    // ---- Zero-typing pairing (see TradeFix.Network.Discovery) -------------------------------
    // Operators should never read or type an IP: unpaired nodes search for Masters by name and
    // join with one click; the Master's operator clicks Allow. Connect codes stay available
    // behind "enter a connect code instead" for edge cases discovery can't reach.

    public ObservableCollection<DiscoveredMaster> FoundMasters { get; } = [];

    [ObservableProperty] private bool _isDiscovering;
    [ObservableProperty] private bool _searchCameUpEmpty;
    [ObservableProperty] private bool _showCodeEntry;

    /// <summary>Context line shown in the Pairing-state panel — "waiting for approval on X"
    /// for one-click joins vs the enter-a-code guidance for the manual flow.</summary>
    [ObservableProperty] private string? _pairingHint;

    /// <summary>Auto-heal: when this node's saved Master is unreachable but discovery finds a
    /// live Master elsewhere on the network, this drives a "connect to that one instead?"
    /// banner — the fix for a node stranded on a Master that moved to another PC.</summary>
    [ObservableProperty] private DiscoveredMaster? _suggestedMaster;

    private int _ticksSinceHealProbe;
    private bool _healProbeRunning;

    public ObservableCollection<string> RecentLogLines { get; } = [];

    /// <summary>Whether stored credentials existed at launch / whether this session has reached
    /// Online at least once — together the evidence that a pairing genuinely WORKS, which is what
    /// gates hiding the connect-code panel. Judging by "an address was entered" (the old rule)
    /// meant one bad/expired code hid the first-run UI with nothing to show for it.</summary>
    private bool _hasProvenPairing;

    /// <summary>True once this Agent has a Master pairing that has actually succeeded (stored
    /// credentials from a past session, or a successful connect this session) — after that, it
    /// reconnects on its own and the "paste a connect code" UI stays hidden.</summary>
    public bool KnowsMaster => !string.IsNullOrEmpty(_host.Settings.MasterHost) && _hasProvenPairing;

    public bool NeedsPairingCode => State == NodeConnectionState.Pairing;

    /// <summary>The status dot pulses only while the connection is alive or actively trying —
    /// a dead Offline/Error indicator holds still (a breathing red dot reads as "working").</summary>
    public bool StatePulses => State is not (NodeConnectionState.Offline or NodeConnectionState.Error);

    public MainViewModel(AgentHost host, Dispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        _nodeName = host.Settings.NodeName;
        _hasProvenPairing = CredentialStore.Load() is not null;

        host.LogSink.EntryAdded += OnLogEntryAdded;

        var statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        statsTimer.Tick += (_, _) =>
        {
            RefreshVideoStats();
            TickAutoHealProbe();
        };
        statsTimer.Start();

        // A node whose Tailscale got turned off/signed out just shows Offline forever with no
        // clue why — quietly reconnect it here (needs no admin), and say so when only a human
        // sign-in can fix it. Same self-heal the Master runs on its side.
        _ = EnsureTailscaleHealthyAsync();

        if (KnowsMaster)
        {
            // Already paired in a previous session — reconnect automatically, no user action needed.
            BeginConnect(host.Settings, autoSubmitCode: null);
        }
        else
        {
            // First launch / after log-out: immediately look for Masters so the operator lands
            // on a "click your Master" list instead of an empty code box.
            _ = DiscoverMastersAsync();
        }
    }

    private async Task EnsureTailscaleHealthyAsync()
    {
        try
        {
            var tailscale = await TailscaleHealth.GetStatusAsync();
            if (tailscale.State == TailscaleBackendState.Stopped)
            {
                await TailscaleHealth.TryStartAsync();
            }
            else if (tailscale.State == TailscaleBackendState.NeedsLogin)
            {
                _ = _dispatcher.InvokeAsync(() =>
                    PairingHint = "Tailscale is signed out on this PC — open Tailscale and sign in, then this node can reach the Master.");
            }
        }
        catch
        {
            // opportunistic — a failed check must never disturb the agent
        }
    }

    [RelayCommand]
    private async Task DiscoverMastersAsync()
    {
        if (IsDiscovering)
        {
            return;
        }

        IsDiscovering = true;
        SearchCameUpEmpty = false;
        try
        {
            var found = await MasterDiscovery.FindAsync(_host.Settings.MasterPort > 0 ? _host.Settings.MasterPort : 8791);
            FoundMasters.Clear();
            foreach (var master in found)
            {
                FoundMasters.Add(master);
            }

            SearchCameUpEmpty = FoundMasters.Count == 0;
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    /// <summary>One-click join: dial the chosen Master and send a code-less join request — the
    /// Master's operator sees an Allow/Deny prompt, and nobody types anything anywhere.</summary>
    [RelayCommand]
    private void JoinMaster(DiscoveredMaster master)
    {
        PairingHint = $"Asking \"{master.Name}\" for permission — on the Master PC, click Allow.";
        var settings = new AgentSettings { MasterHost = master.Host, MasterPort = master.Port, NodeName = NodeName };
        BeginConnect(settings, autoSubmitCode: string.Empty);
    }

    /// <summary>The auto-heal banner's action: abandon the unreachable saved Master and join the
    /// discovered one (fresh pairing, operator approval on the new Master).</summary>
    [RelayCommand]
    private async Task ConnectToSuggestedAsync()
    {
        if (SuggestedMaster is not { } target)
        {
            return;
        }

        SuggestedMaster = null;
        await _host.LogoutAsync();
        _hasProvenPairing = false;
        OnPropertyChanged(nameof(KnowsMaster));
        JoinMaster(target);
    }

    /// <summary>Every ~45s while paired-but-offline, quietly look around: if the saved Master is
    /// gone but a live one answers elsewhere (the "Master moved to a different PC" scenario),
    /// offer it — instead of silently reconnect-looping at a dead address forever.</summary>
    private void TickAutoHealProbe()
    {
        if (!KnowsMaster || State != NodeConnectionState.Offline)
        {
            _ticksSinceHealProbe = 0;
            if (State is NodeConnectionState.Online or NodeConnectionState.Synced)
            {
                SuggestedMaster = null;
            }

            return;
        }

        if (++_ticksSinceHealProbe < 45 || _healProbeRunning)
        {
            return;
        }

        _ticksSinceHealProbe = 0;
        _healProbeRunning = true;
        _ = Task.Run(async () =>
        {
            try
            {
                // While stranded offline, first rule out this node's own Tailscale being the
                // problem (auto-restarts it when merely stopped) before hunting for a moved Master.
                await EnsureTailscaleHealthyAsync();
                var found = await MasterDiscovery.FindAsync(_host.Settings.MasterPort > 0 ? _host.Settings.MasterPort : 8791);
                var alternative = found.FirstOrDefault(m => m.Host != _host.Settings.MasterHost);
                _ = _dispatcher.InvokeAsync(() => SuggestedMaster = alternative);
            }
            catch
            {
                // probing is opportunistic — a failure just means no suggestion this round
            }
            finally
            {
                _healProbeRunning = false;
            }
        });
    }

    private void RefreshVideoStats()
    {
        var bytes = _host.VideoBytesReceived;
        var frames = _host.VideoFramesDisplayed;
        var bytesPerSecond = bytes - _lastVideoBytes;
        var framesPerSecond = frames - _lastVideoFrames;
        _lastVideoBytes = bytes;
        _lastVideoFrames = frames;

        VideoStats = bytesPerSecond > 0 || framesPerSecond > 0
            ? $"Video: {framesPerSecond} fps  ·  {bytesPerSecond * 8 / 1_000_000.0:0.0} Mbps"
            : null;
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
        _hasProvenPairing = false;
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

        connection.StateChanged += state => _ = _dispatcher.InvokeAsync(() =>
        {
            State = state;
            NodeId = connection.Credentials?.NodeId;
            ConnectedMasterSummary = $"{settings.MasterHost}:{settings.MasterPort}";
            if (state == NodeConnectionState.Online)
            {
                _hasProvenPairing = true;
            }

            OnPropertyChanged(nameof(NeedsPairingCode));
            OnPropertyChanged(nameof(KnowsMaster));
            OnPropertyChanged(nameof(StatePulses));

            if (state == NodeConnectionState.Pairing && _pendingParsedPairingCode is not null)
            {
                connection.SubmitPairingCode(_pendingParsedPairingCode);
                _pendingParsedPairingCode = null; // one-shot; a manual retry uses PairingCodeInput instead
            }
        });
    }

    private void OnLogEntryAdded(LogEntry entry)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            RecentLogLines.Add($"[{entry.Timestamp.LocalDateTime:HH:mm:ss}] [{entry.Category}] {entry.Message}");
            while (RecentLogLines.Count > 200)
            {
                RecentLogLines.RemoveAt(0);
            }
        });
    }
}
