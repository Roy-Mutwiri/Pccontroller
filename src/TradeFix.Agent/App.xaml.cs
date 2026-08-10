using System.Windows;
using TradeFix.Agent.Services;

namespace TradeFix.Agent;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public AgentHost Host { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        var settings = AgentSettingsStore.Load();
        Host = new AgentHost(settings);

        var renderWindow = new RenderWindow(Host);
        renderWindow.Show();

        var statusWindow = new MainWindow(Host);
        PositionBesideRenderWindow(statusWindow, renderWindow);
        MainWindow = statusWindow;
        statusWindow.Show();
    }

    /// <summary>
    /// Both windows previously defaulted to CenterScreen and landed exactly on top of each
    /// other, making the render window invisible unless the operator happened to drag the status
    /// window aside. Places the status window to the right of the (already-centered) render
    /// window, falling back to the left if it wouldn't fit on screen.
    /// </summary>
    private static void PositionBesideRenderWindow(Window statusWindow, Window renderWindow)
    {
        var workArea = SystemParameters.WorkArea;
        statusWindow.WindowStartupLocation = WindowStartupLocation.Manual;
        statusWindow.Top = Math.Max(workArea.Top, renderWindow.Top);

        var spaceToTheRight = workArea.Right - (renderWindow.Left + renderWindow.Width);
        if (spaceToTheRight >= statusWindow.Width)
        {
            statusWindow.Left = renderWindow.Left + renderWindow.Width;
        }
        else if (renderWindow.Left - workArea.Left >= statusWindow.Width)
        {
            statusWindow.Left = renderWindow.Left - statusWindow.Width;
        }
        else
        {
            // Neither side has room (small/narrow screen) — stack it below instead of hiding it.
            statusWindow.Left = Math.Max(workArea.Left, renderWindow.Left);
            statusWindow.Top = renderWindow.Top + renderWindow.Height;
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await Host.DisposeAsync();
        base.OnExit(e);
    }
}
