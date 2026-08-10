using System.Diagnostics;
using Microsoft.Win32;

namespace TradeFix.Sources.Capture;

/// <summary>
/// Launches a Chrome/Edge window pre-configured to never freeze when covered by another window —
/// the real fix for a genuine, well-documented Chromium behavior (window-occlusion-based
/// background throttling): a covered or minimized Chromium window deliberately stops rendering
/// new frames to save resources, so <see cref="ScreenCaptureService"/>'s <c>PrintWindow</c>-based
/// capture — which can only ever show whatever the app itself last actually drew — ends up showing
/// a stale, frozen frame. That's not a capture bug; the app genuinely stopped producing new
/// content. This is the same "browser source freezes when covered/minimized" problem the
/// OBS/streaming community has documented for years, with the same documented fix: launch
/// Chromium with flags that disable that throttling. Since we can't retroactively apply flags to a
/// browser instance the user already has open, this launches a dedicated, correctly-flagged one.
/// </summary>
public static class BrowserLauncher
{
    private static readonly string[] AppPathsExeNames = ["chrome.exe", "msedge.exe"];

    /// <summary>Prefers Chrome, falls back to Edge — both accept the same Chromium flags. Uses
    /// the "App Paths" registry (the standard, install-location-independent way Windows itself
    /// resolves a browser's exe) rather than assuming a fixed Program Files path.</summary>
    public static string? FindBrowserExecutable()
    {
        foreach (var exeName in AppPathsExeNames)
        {
            var path = ResolveViaAppPaths(exeName);
            if (path is not null)
            {
                return path;
            }
        }

        return null;
    }

    private static string? ResolveViaAppPaths(string exeName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
        var path = key?.GetValue(null) as string;
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <param name="browserExePath">From <see cref="FindBrowserExecutable"/>.</param>
    /// <param name="url">Loaded directly in "app mode" (<c>--app=</c>) — a clean, chrome-less
    /// window with no tabs/address bar, better suited to being captured as a source.</param>
    /// <param name="profileDirectory">A dedicated, persistent user-data-dir for this specific
    /// capture source. Persistent so logins/sessions survive between launches (e.g. staying signed
    /// into a trading dashboard); dedicated so this launch is fully separate from any browser
    /// instance the user already has open — critical, since Chromium only honors most of these
    /// flags for a genuinely new process/profile, not a new window tacked onto an existing one.</param>
    public static Process? LaunchCaptureFriendlyBrowser(string browserExePath, string url, string profileDirectory)
    {
        Directory.CreateDirectory(profileDirectory);

        var startInfo = new ProcessStartInfo(browserExePath) { UseShellExecute = false };
        startInfo.ArgumentList.Add($"--app={url}");
        startInfo.ArgumentList.Add($"--user-data-dir={profileDirectory}");
        startInfo.ArgumentList.Add("--disable-backgrounding-occluded-windows");
        startInfo.ArgumentList.Add("--disable-renderer-backgrounding");
        startInfo.ArgumentList.Add("--disable-background-timer-throttling");
        // The three flags above stop Chromium from pausing JS timers/the renderer process for an
        // occluded window — enough for general page content (proven by BrowserSourceTests' color-
        // cycling page, which uses a JS timer). Video playback specifically goes through a SEPARATE
        // compositor frame-submission path that's suppressed based on Chromium's own native window-
        // occlusion detection, independent of the flags above — this is why a playing YouTube video
        // kept its audio (decode isn't gated by this) but stopped visually updating once occluded.
        // This flag disables that native occlusion detection entirely, so Chromium never even
        // learns the window is occluded and none of the downstream throttling — including the
        // video compositor path — kicks in. See BrowserOcclusionVideoTests for the regression
        // coverage this specific flag exists for.
        startInfo.ArgumentList.Add("--disable-features=CalculateNativeWinOcclusion");
        startInfo.ArgumentList.Add("--new-window");

        return Process.Start(startInfo);
    }
}
