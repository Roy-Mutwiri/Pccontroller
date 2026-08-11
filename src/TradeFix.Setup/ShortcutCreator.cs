using System.Diagnostics;
using System.IO;

namespace TradeFix.Setup;

/// <summary>
/// Creates a real Windows .lnk shortcut by shelling out to PowerShell's WScript.Shell COM object,
/// rather than using classic in-process [ComImport]/Type.GetTypeFromCLSID COM interop.
///
/// The in-process approach was the original implementation and worked fine under `dotnet build`/
/// `dotnet run`, but crashed the published self-contained single-file exe on startup — before any
/// window even appeared, and without a catchable managed exception (a native
/// STATUS_FATAL_USER_CALLBACK_EXCEPTION). Confirmed via isolation testing: removing the COM
/// interop types from the assembly entirely made the crash disappear, even though that code path
/// was never executed before the crash — the mere presence of classic COM interop types was
/// enough. This matches a documented, real .NET limitation: built-in COM interop relies on
/// generating an IL stub at runtime, which doesn't reliably work in every self-contained
/// single-file publish configuration (the fully-correct fix is source-generated COM interop via
/// [GeneratedComInterface]/ComWrappers, but that's substantially more code for one shortcut call).
/// Shelling out to powershell.exe sidesteps the whole problem: it's a separate, already-COM-capable
/// process, and this is the exact mechanism the project's earlier PowerShell-based installer
/// (installer/Install-TradeFixBroadcast.ps1) already used successfully.
/// </summary>
public static class ShortcutCreator
{
    public static void Create(string shortcutPath, string targetPath, string workingDirectory, string description)
    {
        var script =
            $"$shell = New-Object -ComObject WScript.Shell; " +
            $"$shortcut = $shell.CreateShortcut('{Escape(shortcutPath)}'); " +
            $"$shortcut.TargetPath = '{Escape(targetPath)}'; " +
            $"$shortcut.WorkingDirectory = '{Escape(workingDirectory)}'; " +
            $"$shortcut.Description = '{Escape(description)}'; " +
            $"$shortcut.Save()";

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start powershell.exe to create a shortcut.");
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15000))
        {
            process.Kill();
            throw new TimeoutException($"Creating shortcut '{shortcutPath}' via PowerShell timed out.");
        }

        if (process.ExitCode != 0 || !File.Exists(shortcutPath))
        {
            throw new InvalidOperationException($"Failed to create shortcut '{shortcutPath}': {stderr}");
        }
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
