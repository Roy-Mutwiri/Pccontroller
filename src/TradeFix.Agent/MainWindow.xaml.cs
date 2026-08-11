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
        DataContext = new MainViewModel(host, Dispatcher);
    }
}
