using System.Windows;
using TradeFix.Agent.Services;
using TradeFix.Agent.ViewModels;

namespace TradeFix.Agent;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(AgentHost host)
    {
        InitializeComponent();
        WindowChromeHelper.ApplyDarkTitleBar(this);
        var viewModel = new MainViewModel(host, Dispatcher);
        DataContext = viewModel;

        // Keep the log panel pinned to the newest entry (same rationale as Master's MainWindow).
        // The scroll is DEFERRED via the dispatcher rather than run inline: ScrollIntoView inside
        // a CollectionChanged handler forces container generation before the ListBox has
        // processed that same collection change, which desyncs the ItemContainerGenerator and
        // crashes with "An ItemsControl is inconsistent with its items source" — confirmed from
        // a real render node's crash log when a burst of connection-failure entries landed
        // together. Background priority guarantees the list consumes the change first.
        viewModel.RecentLogLines.CollectionChanged += (_, _) =>
            Dispatcher.InvokeAsync(() =>
            {
                if (LogList.Items.Count > 0)
                {
                    LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
    }
}
