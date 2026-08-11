using System.IO;
using System.Windows;
using TradeFix.Common.Logging;
using TradeFix.Master.Services;

namespace TradeFix.Master;

public partial class App : Application
{
    public MasterHost Host { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // See TradeFix.Agent's App.xaml.cs for why this exists: previously nothing caught
        // unhandled exceptions here, so any UI-thread exception took the whole process down with
        // no trace — indistinguishable from the app just vanishing.
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) => LogCrash("DispatcherUnhandledException", args.Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => LogCrash("UnobservedTaskException", args.Exception);

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
            var path = Path.Combine(Path.GetTempPath(), "tfmaster-crash.txt");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{source}] {ex}\n\n");
        }
        catch
        {
            // if we can't even log the crash, there's nothing more to do
        }
    }
}
