using System.Diagnostics;
using System.Text.Json;

namespace TradeFix.Common;

/// <summary>
/// Lets the Master and Agent apps themselves change which role this PC runs — previously that
/// lived only in the Launcher's tray menu, which meant a PC felt "stuck" as whatever was picked
/// at install time unless the operator knew about the tray icon ("make the app flexible not
/// stuck in one place").
///
/// The saved role lives in the Launcher's own settings file — this class writes the exact same
/// JSON shape <c>TradeFix.Launcher.Services.LauncherSettingsStore</c> reads (role serialized as
/// its numeric enum value), so the Launcher, the role-picker dialog, and these in-app switches
/// all stay one single source of truth. A round-trip test in TradeFix.Launcher.Tests pins the
/// two implementations together; if the Launcher's schema ever changes, that test fails.
///
/// Process choreography on a switch: save the new role first, then try to start the counterpart
/// app directly (installed layout: <c>..\Master\TradeFix.Master.exe</c> etc., same resolution as
/// <c>AppProcessSupervisor</c>). The Launcher — if it's running and supervising — also reloads
/// the saved role when its supervised app exits and starts the new role <em>only if it isn't
/// already running</em>, so the two starters can't race into duplicate processes.
/// </summary>
public static class RoleSwitcher
{
    // Mirrors TradeFix.Launcher.Services.LauncherRole's numeric values.
    private const int MasterRoleValue = 1;
    private const int RenderNodeRoleValue = 2;

    public static string LauncherSettingsPath => Path.Combine(AppPaths.DataRoot("Launcher"), "settings.json");

    /// <summary>Persists the PC's role choice where the Launcher reads it.</summary>
    public static void SaveRole(bool master)
    {
        var json = JsonSerializer.Serialize(new { Role = master ? MasterRoleValue : RenderNodeRoleValue },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(LauncherSettingsPath, json);
    }

    /// <summary>Switches this PC to the other role: saves the choice, then starts the counterpart
    /// app. Returns null on success (caller should then shut this app down), or a human-readable
    /// error when the counterpart couldn't be started and no Launcher is around to do it.</summary>
    public static string? SwitchThisPc(bool toMaster)
    {
        SaveRole(toMaster);

        var (folder, exe, processName) = toMaster
            ? ("Master", "TradeFix.Master.exe", "TradeFix.Master")
            : ("Agent", "TradeFix.Agent.exe", "TradeFix.Agent");

        if (Process.GetProcessesByName(processName).Length > 0)
        {
            return null; // already running (e.g. dev setup) — nothing to start
        }

        if (TryStartApp(ResolveCounterpartPath(folder, exe)))
        {
            return null;
        }

        if (Process.GetProcessesByName("TradeFix.Launcher").Length > 0)
        {
            // Couldn't start it directly, but the Launcher is supervising — when this app exits,
            // the Launcher notices the saved role changed and starts the new one itself.
            return null;
        }

        return $"The role was saved, but {exe} couldn't be found/started from here. " +
               "Start it via the TradeFix Broadcast shortcut (the Launcher) — it will now run the new role.";
    }

    /// <summary>Installed layout: this exe lives in <c>...\TradeFix Broadcast\{Master|Agent}\</c>
    /// with the counterpart in a sibling folder — the same resolution AppProcessSupervisor uses.
    /// Overridable base directory for tests.</summary>
    public static string? ResolveCounterpartPath(string folder, string exe, string? baseDirectory = null)
    {
        var candidate = Path.GetFullPath(Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "..", folder, exe));
        return File.Exists(candidate) ? candidate : null;
    }

    private static bool TryStartApp(string? exePath)
    {
        if (exePath is null)
        {
            return false;
        }

        try
        {
            // Independent OS process, not a child — same reasoning as AppProcessSupervisor.
            return Process.Start(new ProcessStartInfo(exePath)
            {
                WorkingDirectory = Path.GetDirectoryName(exePath),
                UseShellExecute = true
            }) is not null;
        }
        catch
        {
            return false;
        }
    }
}
