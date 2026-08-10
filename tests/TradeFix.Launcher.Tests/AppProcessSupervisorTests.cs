using System.IO;
using TradeFix.Launcher.Services;

namespace TradeFix.Launcher.Tests;

/// <summary>
/// Real filesystem checks (temp directories, not mocked paths) for the path-resolution logic that
/// decides which sibling app to launch — this is the one piece of the Launcher that must exactly
/// match whatever folder layout the installer script actually produces, so it's worth pinning down
/// precisely rather than trusting it by inspection.
/// </summary>
public sealed class AppProcessSupervisorTests : IDisposable
{
    // A dedicated, exclusively-owned parent directory — everything this test creates (including
    // the "Master"/"Agent" sibling folders one level up from "baseDirectory") lives under here, so
    // Dispose() can safely delete exactly this and nothing else. (An earlier version of this test
    // computed baseDirectory's own parent for cleanup, which resolves to the *system temp root* —
    // caught before ever running it, since that would have recursively deleted it.)
    private readonly string _testParent = Path.Combine(Path.GetTempPath(), $"tflauncher-test-{Guid.NewGuid():n}");
    private readonly string _baseDirectory;

    public AppProcessSupervisorTests()
    {
        _baseDirectory = Path.Combine(_testParent, "Launcher");
        Directory.CreateDirectory(_baseDirectory);
    }

    [Fact]
    public void ResolveExePath_ForUnsetRole_ReturnsNull()
    {
        Assert.Null(AppProcessSupervisor.ResolveExePath(LauncherRole.Unset, _baseDirectory));
    }

    [Fact]
    public void ResolveExePath_WhenMasterExeDoesNotExist_ReturnsNull()
    {
        Assert.Null(AppProcessSupervisor.ResolveExePath(LauncherRole.Master, _baseDirectory));
    }

    [Fact]
    public void ResolveExePath_WhenAgentExeDoesNotExist_ReturnsNull()
    {
        Assert.Null(AppProcessSupervisor.ResolveExePath(LauncherRole.RenderNode, _baseDirectory));
    }

    [Fact]
    public void ResolveExePath_ForMaster_FindsItInTheSiblingMasterFolder()
    {
        // Mirrors the installed layout: <install>\Launcher\ (this exe's own directory, passed as
        // baseDirectory) sits next to <install>\Master\TradeFix.Master.exe.
        var masterDir = Path.Combine(_testParent, "Master");
        Directory.CreateDirectory(masterDir);
        var exePath = Path.Combine(masterDir, "TradeFix.Master.exe");
        File.WriteAllText(exePath, "stub");

        var resolved = AppProcessSupervisor.ResolveExePath(LauncherRole.Master, _baseDirectory);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(exePath), resolved);
    }

    [Fact]
    public void ResolveExePath_ForRenderNode_FindsItInTheSiblingAgentFolder()
    {
        var agentDir = Path.Combine(_testParent, "Agent");
        Directory.CreateDirectory(agentDir);
        var exePath = Path.Combine(agentDir, "TradeFix.Agent.exe");
        File.WriteAllText(exePath, "stub");

        var resolved = AppProcessSupervisor.ResolveExePath(LauncherRole.RenderNode, _baseDirectory);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(exePath), resolved);
    }

    [Fact]
    public void ResolveExePath_ForMaster_DoesNotMatchTheAgentFolder()
    {
        // Guards against a sloppy implementation that just globs for "any exe nearby" instead of
        // the specific per-role folder/name pair.
        var agentDir = Path.Combine(_testParent, "Agent");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "TradeFix.Agent.exe"), "stub");

        Assert.Null(AppProcessSupervisor.ResolveExePath(LauncherRole.Master, _baseDirectory));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testParent))
            {
                Directory.Delete(_testParent, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }
}
