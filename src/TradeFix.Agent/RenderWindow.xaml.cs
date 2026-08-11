using System.Windows;
using TradeFix.Agent.Services;
using TradeFix.Agent.ViewModels;

namespace TradeFix.Agent;

/// <summary>
/// Interaction logic for RenderWindow.xaml
/// </summary>
public partial class RenderWindow : Window
{
    /// <summary>One <see cref="LiveFramePump"/> per live-capture source — see that class for why
    /// frames are decoded off the UI thread instead of inline in a blocking Dispatcher.Invoke.
    /// Guarded by <see cref="_pumpsLock"/>: each capture source's network receive loop runs on its
    /// own background thread, so this dictionary can be touched concurrently from several threads
    /// at once, plus the scene-reconciliation cleanup below.</summary>
    private readonly Dictionary<string, LiveFramePump> _framePumps = new();
    private readonly object _pumpsLock = new();

    public RenderWindow(AgentHost host)
    {
        InitializeComponent();
        WindowChromeHelper.ApplyDarkTitleBar(this);

        var viewModel = new RenderViewModel();
        DataContext = viewModel;

        host.SceneLoaded += scene =>
        {
            Dispatcher.Invoke(() => viewModel.ApplyScene(scene));

            var liveIds = scene.Sources.Select(s => s.Id).ToHashSet();
            lock (_pumpsLock)
            {
                foreach (var staleId in _framePumps.Keys.Where(id => !liveIds.Contains(id)).ToList())
                {
                    _framePumps[staleId].Dispose();
                    _framePumps.Remove(staleId);
                }
            }
        };

        host.SourceUpdated += source => Dispatcher.Invoke(() => viewModel.ApplySourceUpdate(source));
        host.AssetReady += (sourceId, path) => Dispatcher.Invoke(() => viewModel.ApplyAssetPath(sourceId, path));

        host.LiveFrameReceived += (sourceId, jpegBytes) =>
        {
            LiveFramePump pump;
            lock (_pumpsLock)
            {
                if (!_framePumps.TryGetValue(sourceId, out pump!))
                {
                    pump = new LiveFramePump(Dispatcher, RenderSourceItemViewModel.DecodeJpeg, decoded => viewModel.ApplyLiveFrame(sourceId, decoded));
                    _framePumps[sourceId] = pump;
                }
            }

            pump.Post(jpegBytes);
        };

        Closed += (_, _) =>
        {
            lock (_pumpsLock)
            {
                foreach (var pump in _framePumps.Values)
                {
                    pump.Dispose();
                }

                _framePumps.Clear();
            }
        };
    }
}
