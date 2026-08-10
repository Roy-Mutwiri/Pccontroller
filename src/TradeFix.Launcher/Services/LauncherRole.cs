namespace TradeFix.Launcher.Services;

/// <summary>Which sub-app this PC should run. Persisted so the Launcher knows what to start on
/// every subsequent launch without asking again — see <see cref="LauncherSettingsStore"/>.</summary>
public enum LauncherRole
{
    Unset,
    Master,
    RenderNode
}
