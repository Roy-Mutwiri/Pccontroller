using System.Diagnostics;

namespace TradeFix.Sources.Video;

/// <summary>
/// Finds a working ffmpeg.exe for the H.264 video pipeline (see <see cref="H264VideoEncoder"/>).
///
/// "Working" is verified by actually running <c>ffmpeg -version</c>, not just checking the file
/// exists — because on WDAC/App-Control-restricted machines an ffmpeg.exe can be present on disk
/// yet blocked from executing (observed for real on this project's dev sandbox: the winget-installed
/// copy was blocked while a byte-identical copy in a different directory ran fine). A candidate
/// that can't actually execute must be treated as "not available" so the whole video pipeline
/// falls back to the JPEG path instead of failing at first use.
///
/// Probe order: next to the running app (how the installer ships it), then PATH, then the winget
/// install location. The result is cached for the process lifetime — availability doesn't change
/// mid-run, and each probe costs a real process spawn.
/// </summary>
public static class FfmpegLocator
{
    private static readonly object Lock = new();
    private static bool _probed;
    private static string? _path;

    /// <summary>Full path to a verified-working ffmpeg.exe, or null if none is available on this
    /// machine (callers must fall back to the JPEG pipeline).</summary>
    public static string? Find()
    {
        lock (Lock)
        {
            if (_probed)
            {
                return _path;
            }

            _probed = true;
            _path = Probe();
            return _path;
        }
    }

    /// <summary>Test hook: forget the cached probe result so a test can stage/remove an
    /// ffmpeg.exe and re-probe. Not used by production code.</summary>
    public static void ResetCacheForTests()
    {
        lock (Lock)
        {
            _probed = false;
            _path = null;
        }
    }

    private static string? Probe()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate) && CanActuallyRun(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        // 1. Shipped alongside the app itself (what the installer/Build-Distributable produces).
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

        // 2. Anywhere on PATH.
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in pathDirs)
        {
            string full;
            try
            {
                full = Path.Combine(dir.Trim(), "ffmpeg.exe");
            }
            catch
            {
                continue; // malformed PATH entry
            }

            yield return full;
        }

        // 3. winget's package store (Gyan.FFmpeg), which isn't always on PATH for the current
        // process even when installed.
        var wingetRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetRoot))
        {
            IEnumerable<string> found;
            try
            {
                found = Directory.EnumerateFiles(wingetRoot, "ffmpeg.exe", SearchOption.AllDirectories);
            }
            catch
            {
                yield break;
            }

            foreach (var exe in found)
            {
                yield return exe;
            }
        }
    }

    private static bool CanActuallyRun(string exePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                ArgumentList = { "-version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { /* best-effort */ }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            // Start() itself throws when App Control blocks the file — exactly the case this
            // validation exists to catch.
            return false;
        }
    }
}
