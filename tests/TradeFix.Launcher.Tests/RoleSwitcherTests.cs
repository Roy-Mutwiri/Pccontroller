using System.IO;
using TradeFix.Common;
using TradeFix.Launcher.Services;

namespace TradeFix.Launcher.Tests;

/// <summary>
/// Pins <see cref="RoleSwitcher"/> (used by the Master/Agent in-app role-switch buttons, lives in
/// TradeFix.Common) to <see cref="LauncherSettingsStore"/> (the Launcher's own reader): the two
/// implementations serialize/deserialize the SAME file, and nothing but these tests enforces
/// that they agree. If the Launcher's settings schema ever changes, this fails loudly instead of
/// role switches silently writing a file the Launcher misreads.
///
/// The real settings file is saved and restored around each test — it holds this dev machine's
/// actual role choice.
/// </summary>
public sealed class RoleSwitcherTests : IDisposable
{
    private readonly string _settingsPath = RoleSwitcher.LauncherSettingsPath;
    private readonly string? _originalContent;

    public RoleSwitcherTests()
    {
        _originalContent = File.Exists(_settingsPath) ? File.ReadAllText(_settingsPath) : null;
    }

    public void Dispose()
    {
        if (_originalContent is not null)
        {
            File.WriteAllText(_settingsPath, _originalContent);
        }
        else if (File.Exists(_settingsPath))
        {
            File.Delete(_settingsPath);
        }
    }

    [Fact]
    public void SaveRoleMaster_IsReadBackByTheLauncherAsMaster()
    {
        RoleSwitcher.SaveRole(master: true);
        Assert.Equal(LauncherRole.Master, LauncherSettingsStore.Load().Role);
    }

    [Fact]
    public void SaveRoleRenderNode_IsReadBackByTheLauncherAsRenderNode()
    {
        RoleSwitcher.SaveRole(master: false);
        Assert.Equal(LauncherRole.RenderNode, LauncherSettingsStore.Load().Role);
    }

    [Fact]
    public void ResolveCounterpartPath_FindsSiblingInInstalledLayout_AndNullOtherwise()
    {
        var root = Directory.CreateTempSubdirectory("tfx-roleswitch-test");
        try
        {
            var agentDir = Directory.CreateDirectory(Path.Combine(root.FullName, "Agent"));
            var masterDir = Directory.CreateDirectory(Path.Combine(root.FullName, "Master"));
            var masterExe = Path.Combine(masterDir.FullName, "TradeFix.Master.exe");
            File.WriteAllText(masterExe, "stub");

            var resolved = RoleSwitcher.ResolveCounterpartPath("Master", "TradeFix.Master.exe", agentDir.FullName);
            Assert.Equal(masterExe, resolved);

            Assert.Null(RoleSwitcher.ResolveCounterpartPath("Agent", "TradeFix.Agent.exe", masterDir.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
