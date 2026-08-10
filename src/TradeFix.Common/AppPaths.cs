namespace TradeFix.Common;

/// <summary>Resolves per-app data directories under %LocalAppData%\TradeFixBroadcast. Never
/// hardcoded elsewhere (spec section 34).</summary>
public static class AppPaths
{
    public static string DataRoot(string appName)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TradeFixBroadcast",
            appName);
        Directory.CreateDirectory(root);
        return root;
    }

    public static string LogsDirectory(string appName)
    {
        var path = Path.Combine(DataRoot(appName), "logs");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string DatabasePath(string appName, string fileName) =>
        Path.Combine(DataRoot(appName), fileName);

    public static string ProjectsDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TradeFixProjects");
}
