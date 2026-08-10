using System.Windows;
using TradeFix.Master.Services;

namespace TradeFix.Master;

public partial class App : Application
{
    public MasterHost Host { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = SettingsStore.Load();
        Host = new MasterHost(settings);
        Host.Start();

        var window = new MainWindow(Host);
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await Host.DisposeAsync();
        base.OnExit(e);
    }
}
