using System.IO;
using System.Windows;
using System.Windows.Threading;
using TradeFix.Agent.Services;
using TradeFix.Common.Logging;

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

        // Previously nothing caught unhandled exceptions here — an exception anywhere on the UI
        // thread (a bad frame during decode, a rendering failure, etc.) took the whole process
        // down silently, which is indistinguishable from "opens and closes itself immediately"
        // (the exact symptom reported from a real render node). This doesn't prevent the crash —
        // an exception that reaches this far genuinely means the app can't safely continue — but
        // it guarantees a trace survives it, in both this app's own log and a TEMP fallback for
        // cases where Host/its logger isn't up yet. Mirrors the same pattern already used in
        // TradeFix.Setup's App.xaml.cs.
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) => LogCrash("DispatcherUnhandledException", args.Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => LogCrash("UnobservedTaskException", args.Exception);

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

    protected override void OnExit(ExitEventArgs e)
    {
        // Deliberately synchronous — see Master's App.xaml.cs: an async void OnExit races
        // process shutdown against its own teardown.
        try
        {
            Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // best-effort teardown on the way out
        }

        base.OnExit(e);
    }

    private void LogCrash(string source, Exception? ex)
    {
        try
        {
            Host?.Log.Write(LogCategory.Error, "App", $"Unhandled exception ({source})", ex);
        }
        catch
        {
            // logging itself failed (e.g. Host not constructed yet) — fall through to the TEMP file
        }

        try
        {
            var path = Path.Combine(Path.GetTempPath(), "tfagent-crash.txt");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{source}] {ex}\n\n");
        }
        catch
        {
            // if we can't even log the crash, there's nothing more to do
        }
    }
}
