using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TradeFix.Setup;

/// <summary>
/// Core install/uninstall logic for the compiled TradeFix.Setup.exe — a straight C# port of what
/// was previously a PowerShell script (installer/Install-TradeFixBroadcast.ps1), kept because the
/// user specifically wants one double-clickable .exe rather than a script + .bat wrapper. The
/// PowerShell version is still shipped alongside as a documented fallback for a machine where this
/// exe itself gets blocked by something like Windows Defender Application Control — PowerShell
/// scripts run through the trusted, signed powershell.exe host and can't hit that particular
/// problem the way a compiled exe theoretically could (see KNOWN_LIMITATIONS.md's WDAC section).
///
/// Mutating operations are real (real filesystem, real registry) but structured so path resolution
/// and validation are pure/injectable — testable against real temp directories rather than mocks,
/// matching this project's established testing style.
/// </summary>
public static class Installer
{
    public const string DisplayName = "TradeFix Broadcast Control Center";
    public const string UninstallRegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TradeFixBroadcast";

    /// <summary>(destination folder name under the install root, source publish-profile folder
    /// name, exe file name). Destination names must match what
    /// TradeFix.Launcher.Services.AppProcessSupervisor.ResolveExePath expects.</summary>
    public static readonly (string DestFolder, string SourcePublishFolder, string ExeName)[] Apps =
    [
        ("Master", "TradeFix.Master-win-x64", "TradeFix.Master.exe"),
        ("Agent", "TradeFix.Agent-win-x64", "TradeFix.Agent.exe"),
        ("Launcher", "TradeFix.Launcher-win-x64", "TradeFix.Launcher.exe"),
    ];

    public sealed record InstallOutcome(bool Success, string Message);

    public static string ResolveInstallRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TradeFix Broadcast");

    /// <param name="baseDirectory">Defaults to this exe's own directory — overridable for tests.
    /// Expects a "publish" folder as a direct child (the layout Build-Distributable.ps1's final
    /// packaging step produces: TradeFix.Setup.exe sitting next to publish\TradeFix.*-win-x64\...).</param>
    public static string ResolvePublishRoot(string? baseDirectory = null) =>
        Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "publish");

    /// <summary>Pure — no side effects. Returns the "sourceFolder\exeName" of every app not found
    /// under <paramref name="publishRoot"/>; empty if everything needed is present.</summary>
    public static IReadOnlyList<string> FindMissingApps(string publishRoot)
    {
        var missing = new List<string>();
        foreach (var (_, sourceFolder, exeName) in Apps)
        {
            if (!File.Exists(Path.Combine(publishRoot, sourceFolder, exeName)))
            {
                missing.Add($"{sourceFolder}\\{exeName}");
            }
        }

        return missing;
    }

    public static bool IsTailscaleInstalled()
    {
        if (FindOnPath("tailscale.exe") is not null)
        {
            return true;
        }

        var programFilesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        if (File.Exists(programFilesPath))
        {
            return true;
        }

        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Tailscale IPN");
        return key is not null;
    }

    private static string? FindOnPath(string exeName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // malformed PATH entry — skip
            }
        }

        return null;
    }

    public static void StopRunningApps()
    {
        foreach (var name in new[] { "TradeFix.Master", "TradeFix.Agent", "TradeFix.Launcher" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
                catch
                {
                    // best-effort
                }
            }
        }
    }

    public static void CopyAppFiles(string publishRoot, string installRoot)
    {
        foreach (var (destFolder, sourceFolder, _) in Apps)
        {
            CopyDirectory(Path.Combine(publishRoot, sourceFolder), Path.Combine(installRoot, destFolder));
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
        }
    }

    /// <summary>Creating a shortcut goes through the IShellLinkW COM object, which — like most
    /// shell COM interfaces — requires an STA thread. This is called from <see cref="Install"/>,
    /// which callers commonly run via <c>Task.Run</c> (a thread-pool/MTA thread) to keep the UI
    /// responsive during file copying — so this specific step is marshaled onto a dedicated STA
    /// thread regardless of which thread <see cref="CreateShortcuts"/> itself was called from.
    /// Skipping this was a real bug found during end-to-end testing: it crashed the whole process
    /// with an unhandled COM exception the very first time this ran outside a test host (xUnit's
    /// test runner happened to use an STA thread for this project's test collection, masking the
    /// bug in <c>InstallerTests</c> — a good reminder that "the unit test passed" isn't the same
    /// guarantee as "the real app doesn't crash" when thread apartment state is involved).</summary>
    /// <param name="startMenuProgramsDir">Defaults to the real Start Menu Programs folder —
    /// overridable so tests can verify this without touching the real user's Start Menu.</param>
    /// <param name="desktopDir">Defaults to the real Desktop — same reasoning.</param>
    public static void CreateShortcuts(string installRoot, string? startMenuProgramsDir = null, string? desktopDir = null)
    {
        RunOnStaThread(() =>
        {
            var launcherExe = Path.Combine(installRoot, "Launcher", "TradeFix.Launcher.exe");
            var workingDir = Path.GetDirectoryName(launcherExe)!;

            var startMenu = startMenuProgramsDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs");
            Directory.CreateDirectory(startMenu);
            ShortcutCreator.Create(Path.Combine(startMenu, "TradeFix Broadcast.lnk"), launcherExe, workingDir, DisplayName);

            var desktop = desktopDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            Directory.CreateDirectory(desktop);
            ShortcutCreator.Create(Path.Combine(desktop, "TradeFix Broadcast.lnk"), launcherExe, workingDir, DisplayName);
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
        {
            throw new InvalidOperationException("Shortcut creation failed.", error);
        }
    }

    public static void RemoveShortcuts()
    {
        TryDelete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\TradeFix Broadcast.lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TradeFix Broadcast.lnk"));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort
        }
    }

    public static void RegisterUninstall(string installRoot, string setupExePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistrySubKey);
        key.SetValue("DisplayName", DisplayName);
        key.SetValue("UninstallString", $"\"{setupExePath}\" --uninstall");
        key.SetValue("Publisher", "TradeFix");
        key.SetValue("InstallLocation", installRoot);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    public static void RemoveUninstallRegistration()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistrySubKey, throwOnMissingSubKey: false);
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>Schedules deletion of <paramref name="installRoot"/> a few seconds out via a
    /// detached cmd.exe, since a running process can't delete its own containing folder
    /// synchronously. Moves this process's own working directory out of the folder first — the
    /// exact bug that silently broke the PowerShell installer's uninstaller on its first real
    /// end-to-end test: Windows won't delete a directory that's any live process's current
    /// directory, and this process (running from inside that folder) would otherwise still hold
    /// that lock for the few seconds before the detached cmd fires.</summary>
    public static void ScheduleSelfDelete(string installRoot)
    {
        Directory.SetCurrentDirectory(Path.GetTempPath());

        var cleanupCommand = $"timeout /t 3 /nobreak >nul & rmdir /s /q \"{installRoot}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c {cleanupCommand}")
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false
        });
    }

    public static InstallOutcome Install(Action<string> log)
    {
        var publishRoot = ResolvePublishRoot();
        var missing = FindMissingApps(publishRoot);
        if (missing.Count > 0)
        {
            return new InstallOutcome(false,
                $"This setup package is incomplete — missing {string.Join(", ", missing)}. Re-download a complete package.");
        }

        log("Stopping any already-running copies...");
        StopRunningApps();

        var installRoot = ResolveInstallRoot();
        log($"Installing to {installRoot} ...");
        CopyAppFiles(publishRoot, installRoot);

        var setupDestPath = Path.Combine(installRoot, "TradeFix.Setup.exe");
        try
        {
            if (Environment.ProcessPath is { } currentExePath)
            {
                File.Copy(currentExePath, setupDestPath, overwrite: true);
            }
        }
        catch
        {
            // best-effort — if this fails, uninstall via Apps & Features just won't have a target;
            // the install itself still succeeded
        }

        log("Creating shortcuts...");
        CreateShortcuts(installRoot);

        log("Registering in Apps & Features...");
        RegisterUninstall(installRoot, setupDestPath);

        if (IsTailscaleInstalled())
        {
            log("Tailscale detected — connecting nodes on other networks will work.");
        }
        else
        {
            log("Tailscale wasn't detected. Only needed for nodes on a different network than the " +
                "Master (same-LAN setups work without it) — opening the download page; installing it is optional.");
            TryOpenUrl("https://tailscale.com/download/windows");
        }

        log("Launching TradeFix Broadcast...");
        var launcherExe = Path.Combine(installRoot, "Launcher", "TradeFix.Launcher.exe");
        try
        {
            Process.Start(new ProcessStartInfo(launcherExe) { WorkingDirectory = Path.GetDirectoryName(launcherExe), UseShellExecute = true });
        }
        catch (Exception ex)
        {
            return new InstallOutcome(true, $"Installed, but couldn't auto-launch it ({ex.Message}) — start it from the Start Menu or Desktop shortcut.");
        }

        return new InstallOutcome(true, "Setup complete.");
    }

    public static InstallOutcome Uninstall(Action<string> log)
    {
        log("Stopping running apps...");
        StopRunningApps();

        log("Removing shortcuts...");
        RemoveShortcuts();

        log("Removing Apps & Features entry...");
        RemoveUninstallRegistration();

        log("Cleaning up program files...");
        ScheduleSelfDelete(ResolveInstallRoot());

        return new InstallOutcome(true,
            "Uninstalled. Your settings, logs, and paired-node history under %LocalAppData%\\TradeFixBroadcast\\ were kept.");
    }

    private static void TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // best-effort — not opening a browser tab isn't worth failing setup over
        }
    }
}
