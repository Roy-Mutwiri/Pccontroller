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
        viewModel.RecentLogLines.CollectionChanged += (_, _) =>
        {
            if (LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
            }
        };
    }
}
