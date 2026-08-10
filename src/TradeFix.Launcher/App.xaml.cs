using System.Windows;
using System.Windows.Forms;
using TradeFix.Launcher.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace TradeFix.Launcher;

/// <summary>
/// Interaction logic for App.xaml — the Launcher has no persistent main window; it lives as a
/// system tray icon that starts/stops the sibling Master or Agent app based on the saved role.
/// </summary>
public partial class App : Application
{
    private readonly AppProcessSupervisor _supervisor = new();
    private NotifyIcon? _trayIcon;
    private LauncherSettings _settings = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _settings = LauncherSettingsStore.Load();

        if (_settings.Role == LauncherRole.Unset)
        {
            var picker = new RoleSelectionWindow();
            var picked = picker.ShowDialog();
            if (picked != true)
            {
                // Closed without choosing — nothing to run without a role.
                Shutdown();
                return;
            }

            _settings.Role = picker.SelectedRole;
            LauncherSettingsStore.Save(_settings);
        }

        CreateTrayIcon();
        StartRole(_settings.Role, showErrorIfMissing: true);

        _supervisor.ProcessExitedUnexpectedly += () => Dispatcher.Invoke(RefreshTrayMenu);
    }

    private void StartRole(LauncherRole role, bool showErrorIfMissing)
    {
        var started = _supervisor.Start(role);
        if (!started && showErrorIfMissing)
        {
            var roleName = role == LauncherRole.Master ? "Master" : "Agent";
            MessageBox.Show(
                $"Could not find {roleName}\\TradeFix.{roleName}.exe next to the Launcher.\n\n" +
                "This usually means the install is incomplete — reinstall TradeFix Broadcast.",
                "TradeFix Broadcast", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        RefreshTrayMenu();
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "TradeFix Broadcast"
        };

        _trayIcon.DoubleClick += (_, _) => BringRunningAppToFront();
        RefreshTrayMenu();
    }

    private void RefreshTrayMenu()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var roleLabel = _settings.Role == LauncherRole.Master ? "Master PC" : "Render Node";
        var runningLabel = _supervisor.RunningRole is null ? "stopped" : "running";
        _trayIcon.Text = $"TradeFix Broadcast — {roleLabel} ({runningLabel})";

        var menu = new ContextMenuStrip();

        menu.Items.Add(_supervisor.RunningRole is null ? $"Start {roleLabel}" : $"Restart {roleLabel}",
            null, (_, _) => StartRole(_settings.Role, showErrorIfMissing: true));

        menu.Items.Add(new ToolStripSeparator());

        var switchToMaster = new ToolStripMenuItem("Switch this PC to Master", null, (_, _) => SwitchRole(LauncherRole.Master))
        {
            Enabled = _settings.Role != LauncherRole.Master
        };
        var switchToRenderNode = new ToolStripMenuItem("Switch this PC to Render Node", null, (_, _) => SwitchRole(LauncherRole.RenderNode))
        {
            Enabled = _settings.Role != LauncherRole.RenderNode
        };
        menu.Items.Add(switchToMaster);
        menu.Items.Add(switchToRenderNode);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Launcher (keeps the app running)", null, (_, _) => ExitLauncherOnly());

        _trayIcon.ContextMenuStrip = menu;
    }

    /// <summary>Switching roles stops whichever app is currently running (it doesn't make sense
    /// for a PC to be both) and starts the other — a real, disruptive action worth confirming
    /// since it's stopping a live broadcast app, not just a background helper.</summary>
    private void SwitchRole(LauncherRole newRole)
    {
        var newRoleLabel = newRole == LauncherRole.Master ? "Master PC" : "Render Node";
        var result = MessageBox.Show(
            $"Switch this PC to {newRoleLabel}? The currently running app will be closed first.",
            "TradeFix Broadcast", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _settings.Role = newRole;
        LauncherSettingsStore.Save(_settings);
        StartRole(newRole, showErrorIfMissing: true);
    }

    private void BringRunningAppToFront()
    {
        // Best-effort: the running app manages its own window(s); the Launcher doesn't track
        // window handles across process boundaries. Restarting is the reliable fallback if the
        // operator wants to get back to it and can't find the window (e.g. it's minimized).
        if (_supervisor.RunningRole is null)
        {
            StartRole(_settings.Role, showErrorIfMissing: true);
        }
    }

    /// <summary>Closes only the tray helper — the already-running Master/Agent app is a genuinely
    /// independent process and must keep running; this is a supervisor exiting, not a shutdown
    /// command for the actual broadcast software.</summary>
    private void ExitLauncherOnly()
    {
        _trayIcon!.Visible = false;
        _trayIcon.Dispose();
        _supervisor.Dispose();
        Shutdown();
    }
}
