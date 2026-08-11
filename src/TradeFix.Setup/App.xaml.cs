using System.Windows;

namespace TradeFix.Setup;

/// <summary>
/// Interaction logic for App.xaml — routes to install or uninstall mode based on whether
/// "--uninstall" was passed (the Apps &amp; Features UninstallString invokes this exe with that
/// flag; a normal double-click has no args and means "install").
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Belt-and-suspenders: if something ever throws in a way MainWindow's own try/catch can't
        // reach (e.g. during window construction itself, before that try/catch even exists yet),
        // this at least leaves a trace at %TEMP%\tfsetup-crash.txt an operator could send along
        // when reporting a setup failure, instead of the process just silently vanishing.
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) => LogCrash("DispatcherUnhandledException", args.Exception);

        base.OnStartup(e);

        var isUninstall = e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(isUninstall);
        MainWindow = window;
        window.Show();
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tfsetup-crash.txt");
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{source}] {ex}\n\n");
        }
        catch
        {
            // if we can't even log the crash, there's nothing more to do
        }
    }
}
