using System.IO;
using TradeFix.Setup;

namespace TradeFix.Setup.Tests;

/// <summary>
/// Real filesystem/registry checks (temp directories, not mocks) for the compiled installer's
/// core logic — this is the C# port of what was previously PowerShell, so the same standard
/// applies: prove the actual behavior, not just "compiles and doesn't throw."
/// </summary>
public sealed class InstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tfsetup-test-{Guid.NewGuid():n}");

    public InstallerTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ResolvePublishRoot_IsADirectChildNamedPublish_OfTheGivenBaseDirectory()
    {
        var result = Installer.ResolvePublishRoot(_root);

        Assert.Equal(Path.Combine(_root, "publish"), result);
    }

    [Fact]
    public void FindMissingApps_WhenNothingIsPublished_ListsAllThreeApps()
    {
        var missing = Installer.FindMissingApps(_root);

        Assert.Equal(3, missing.Count);
    }

    [Fact]
    public void FindMissingApps_WhenEverythingIsPublished_ReturnsEmpty()
    {
        foreach (var (_, sourceFolder, exeName) in Installer.Apps)
        {
            var dir = Path.Combine(_root, sourceFolder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, exeName), "stub");
        }

        var missing = Installer.FindMissingApps(_root);

        Assert.Empty(missing);
    }

    [Fact]
    public void FindMissingApps_WhenOnlySomeArePublished_ListsOnlyTheMissingOnes()
    {
        var (_, masterSourceFolder, masterExe) = Installer.Apps[0];
        var masterDir = Path.Combine(_root, masterSourceFolder);
        Directory.CreateDirectory(masterDir);
        File.WriteAllText(Path.Combine(masterDir, masterExe), "stub");

        var missing = Installer.FindMissingApps(_root);

        Assert.Equal(2, missing.Count); // Agent and Launcher still missing
        Assert.DoesNotContain(missing, m => m.Contains(masterSourceFolder));
    }

    [Fact]
    public void CopyAppFiles_PlacesEachAppUnderItsShortDestinationName_NotTheSourcePublishProfileName()
    {
        // The destination folder names (Master/Agent/Launcher) must exactly match what
        // TradeFix.Launcher.Services.AppProcessSupervisor.ResolveExePath expects — this is the one
        // thing that has to stay in sync across two different projects, so it's worth pinning down
        // precisely rather than trusting it by inspection.
        var publishRoot = Path.Combine(_root, "publish");
        foreach (var (_, sourceFolder, exeName) in Installer.Apps)
        {
            var dir = Path.Combine(publishRoot, sourceFolder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, exeName), "stub content");
        }

        var installRoot = Path.Combine(_root, "installed");
        Installer.CopyAppFiles(publishRoot, installRoot);

        foreach (var (destFolder, _, exeName) in Installer.Apps)
        {
            var expectedPath = Path.Combine(installRoot, destFolder, exeName);
            Assert.True(File.Exists(expectedPath), $"Expected {expectedPath} to exist after CopyAppFiles");
            Assert.Equal("stub content", File.ReadAllText(expectedPath));
        }
    }

    [Fact]
    public void CopyAppFiles_CopiesNestedSubdirectoriesToo()
    {
        var publishRoot = Path.Combine(_root, "publish");
        var (_, sourceFolder, exeName) = Installer.Apps[0];
        var dir = Path.Combine(publishRoot, sourceFolder);
        var nestedDir = Path.Combine(dir, "runtimes", "win-x64");
        Directory.CreateDirectory(nestedDir);
        File.WriteAllText(Path.Combine(dir, exeName), "stub");
        File.WriteAllText(Path.Combine(nestedDir, "native.dll"), "native stub");

        foreach (var (_, otherSourceFolder, otherExeName) in Installer.Apps.Skip(1))
        {
            var otherDir = Path.Combine(publishRoot, otherSourceFolder);
            Directory.CreateDirectory(otherDir);
            File.WriteAllText(Path.Combine(otherDir, otherExeName), "stub");
        }

        var installRoot = Path.Combine(_root, "installed");
        Installer.CopyAppFiles(publishRoot, installRoot);

        var destFolder = Installer.Apps[0].DestFolder;
        var expectedNestedFile = Path.Combine(installRoot, destFolder, "runtimes", "win-x64", "native.dll");
        Assert.True(File.Exists(expectedNestedFile), $"Expected nested file {expectedNestedFile} to have been copied");
    }

    [Fact]
    public void IsTailscaleInstalled_MatchesRealMachineState()
    {
        // This dev machine has Tailscale installed (confirmed earlier in this project via
        // `tailscale status`) — a real check against real machine state, not a mock, consistent
        // with this project's standard for anything OS/environment-dependent.
        Assert.True(Installer.IsTailscaleInstalled());
    }

    [Fact]
    public async Task CreateShortcuts_DoesNotCrash_WhenCalledFromAThreadPoolContext()
    {
        // Installer.Install() is meant to be called via Task.Run (a thread-pool thread) to keep
        // the setup UI responsive during file copying, so this forces that same context rather
        // than calling CreateShortcuts directly from the test's own thread. Originally written
        // when ShortcutCreator used in-process COM interop (which genuinely did crash the real
        // published exe on startup — see ShortcutCreator's remarks for the full story of why it
        // now shells out to PowerShell instead). Kept as regression coverage even after that
        // rewrite, since "works fine from a background thread" is still a real property worth
        // guaranteeing regardless of the underlying mechanism.
        var launcherDir = Path.Combine(_root, "installed", "Launcher");
        Directory.CreateDirectory(launcherDir);
        File.WriteAllText(Path.Combine(launcherDir, "TradeFix.Launcher.exe"), "stub");

        var installRoot = Path.Combine(_root, "installed");
        var fakeStartMenu = Path.Combine(_root, "fake-start-menu");
        var fakeDesktop = Path.Combine(_root, "fake-desktop");

        var exception = await Record.ExceptionAsync(() =>
            Task.Run(() => Installer.CreateShortcuts(installRoot, fakeStartMenu, fakeDesktop)));

        Assert.Null(exception);
        Assert.True(File.Exists(Path.Combine(fakeStartMenu, "TradeFix Broadcast.lnk")));
        Assert.True(File.Exists(Path.Combine(fakeDesktop, "TradeFix Broadcast.lnk")));
    }

    [Fact]
    public void ShortcutCreator_CreatesARealNonEmptyLnkFile()
    {
        var targetPath = Path.Combine(_root, "dummy-target.txt");
        File.WriteAllText(targetPath, "target");
        var shortcutPath = Path.Combine(_root, "Test Shortcut.lnk");

        ShortcutCreator.Create(shortcutPath, targetPath, _root, "A test shortcut");

        Assert.True(File.Exists(shortcutPath));
        Assert.True(new FileInfo(shortcutPath).Length > 0, "The .lnk file was created but is empty");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }
}
