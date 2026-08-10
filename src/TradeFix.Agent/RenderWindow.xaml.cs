using System.Windows;
using TradeFix.Agent.Services;
using TradeFix.Agent.ViewModels;

namespace TradeFix.Agent;

/// <summary>
/// Interaction logic for RenderWindow.xaml
/// </summary>
public partial class RenderWindow : Window
{
    public RenderWindow(AgentHost host)
    {
        InitializeComponent();

        var viewModel = new RenderViewModel();
        DataContext = viewModel;

        host.SceneLoaded += scene => Dispatcher.Invoke(() => viewModel.ApplyScene(scene));
        host.SourceUpdated += source => Dispatcher.Invoke(() => viewModel.ApplySourceUpdate(source));
        host.AssetReady += (sourceId, path) => Dispatcher.Invoke(() => viewModel.ApplyAssetPath(sourceId, path));
        host.LiveFrameReceived += (sourceId, jpegBytes) => Dispatcher.Invoke(() => viewModel.ApplyLiveFrame(sourceId, jpegBytes));
    }
}
