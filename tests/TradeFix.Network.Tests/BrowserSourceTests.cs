using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using TradeFix.Sources.Capture;

namespace TradeFix.Network.Tests;

/// <summary>
/// Proves the actual claim behind the Browser source feature — not just "launches something" but
/// specifically that the launched browser keeps rendering fresh content while its window is fully
/// covered by another one, which a normal already-open Chromium window does NOT do (see
/// BrowserLauncher's remarks for why: Chromium deliberately pauses rendering for occluded windows
/// to save resources, which is exactly what makes a captured browser look "frozen").
///
/// Real end-to-end: a real local HTML page whose background color visibly cycles every 150ms, a
/// real browser process launched via BrowserLauncher, a real second window covering it, real
/// ScreenCaptureService frames decoded and pixel-sampled. If the anti-throttling flags didn't
/// work, every sampled frame while covered would show the same (frozen) color.
/// </summary>
[Collection("Notepad capture tests")]
public sealed class BrowserSourceTests : IDisposable
{
    private readonly string _tempHtmlPath = Path.Combine(Path.GetTempPath(), $"tradefix-browser-test-{Guid.NewGuid():n}.html");
    private readonly string _profileDirectory = Path.Combine(Path.GetTempPath(), $"tradefix-browser-profile-{Guid.NewGuid():n}");
    private Process? _browserProcess;
    private Process? _coveringNotepad;

    [Fact]
    public void FindBrowserExecutable_FindsARealInstalledChromiumBrowser()
    {
        var path = BrowserLauncher.FindBrowserExecutable();

        Assert.NotNull(path);
        Assert.True(File.Exists(path), $"Resolved path '{path}' does not actually exist");
    }

    [Fact]
    public async Task LaunchedBrowser_KeepsRenderingFreshContent_EvenWhileFullyCoveredByAnotherWindow()
    {
        var browserExePath = BrowserLauncher.FindBrowserExecutable();
        Assert.NotNull(browserExePath); // if this fails, the environment has no Chrome/Edge — the feature genuinely can't work here

        File.WriteAllText(_tempHtmlPath, """
            <!DOCTYPE html><html><body style="margin:0;height:100vh"><script>
            var colors = ['#ff0000', '#00ff00', '#0000ff'];
            var i = 0;
            setInterval(function () {
                document.body.style.backgroundColor = colors[i % colors.length];
                i++;
            }, 150);
            </script></body></html>
            """);

        var url = new Uri(_tempHtmlPath).AbsoluteUri;
        _browserProcess = BrowserLauncher.LaunchCaptureFriendlyBrowser(browserExePath!, url, _profileDirectory);
        Assert.NotNull(_browserProcess);

        CapturableWindow? browserWindow = null;
        var findDeadline = DateTime.UtcNow.AddSeconds(15); // fresh profile dirs can take a few seconds to spin up
        while (browserWindow is null && DateTime.UtcNow < findDeadline)
        {
            browserWindow = WindowEnumerator.FindWindowForProcess(_browserProcess.Id);
            if (browserWindow is null)
            {
                await Task.Delay(300);
            }
        }

        Assert.NotNull(browserWindow);
        Assert.True(GetWindowRect(browserWindow!.Handle, out var browserRect), "Could not read the launched browser window's rect");

        // Cover it completely with a real second window.
        _coveringNotepad = Process.Start("notepad.exe");
        var coveringWindow = await FindWindowByTitleAsync("Notepad", excludeHandle: browserWindow.Handle);
        Assert.NotNull(coveringWindow);

        SetWindowPos(coveringWindow!.Handle, IntPtr.Zero, browserRect.Left, browserRect.Top,
            browserRect.Right - browserRect.Left, browserRect.Bottom - browserRect.Top, SwpNoZOrder | SwpNoActivate);
        SetForegroundWindow(coveringWindow.Handle);
        await Task.Delay(500); // let the compositor finish restacking

        // Capture several frames of the now-covered browser window and sample the center pixel of each.
        var sampledColors = new List<string>();
        using var capture = new ScreenCaptureService(targetFps: 6, maxDimension: 1280, targetWindow: browserWindow.Handle);
        capture.FrameCaptured += bytes =>
        {
            try
            {
                using var image = (Bitmap)Image.FromStream(new MemoryStream(bytes));
                var pixel = image.GetPixel(image.Width / 2, image.Height / 2);
                lock (sampledColors)
                {
                    sampledColors.Add(ClassifyColor(pixel));
                }
            }
            catch
            {
                // a torn/partial frame — skip it, plenty of other samples coming
            }

            return Task.CompletedTask;
        };

        capture.Start();
        var sampleDeadline = DateTime.UtcNow.AddSeconds(6);
        while (sampledColors.Count < 8 && DateTime.UtcNow < sampleDeadline)
        {
            await Task.Delay(100);
        }

        capture.Stop();

        Assert.True(sampledColors.Count >= 8, $"Expected at least 8 samples, got {sampledColors.Count}");

        var distinctColors = sampledColors.Distinct().ToList();
        Assert.True(distinctColors.Count >= 2,
            $"All {sampledColors.Count} samples while the browser window was covered showed the same color " +
            $"({string.Join(",", distinctColors)}) — this is exactly the frozen-frame symptom the anti-throttling " +
            "flags are supposed to prevent. The page's background should have kept cycling every 150ms.");
    }

    private static string ClassifyColor(Color pixel)
    {
        if (pixel.R > pixel.G && pixel.R > pixel.B)
        {
            return "red";
        }

        if (pixel.G > pixel.R && pixel.G > pixel.B)
        {
            return "green";
        }

        if (pixel.B > pixel.R && pixel.B > pixel.G)
        {
            return "blue";
        }

        return $"other({pixel.R},{pixel.G},{pixel.B})";
    }

    private static async Task<CapturableWindow?> FindWindowByTitleAsync(string titleContains, IntPtr? excludeHandle = null)
    {
        CapturableWindow? found = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && found is null)
        {
            found = WindowEnumerator.GetCapturableWindows()
                .Where(w => excludeHandle is null || w.Handle != excludeHandle.Value)
                .FirstOrDefault(w => w.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase));

            if (found is null)
            {
                await Task.Delay(200);
            }
        }

        return found;
    }

    public void Dispose()
    {
        try
        {
            if (_browserProcess is { HasExited: false })
            {
                _browserProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort
        }

        if (_coveringNotepad is { HasExited: false })
        {
            _coveringNotepad.Kill();
        }

        foreach (var stray in Process.GetProcessesByName("notepad"))
        {
            try
            {
                stray.Kill();
            }
            catch
            {
                // best-effort
            }
        }

        try
        {
            File.Delete(_tempHtmlPath);
        }
        catch
        {
            // best-effort
        }

        try
        {
            if (Directory.Exists(_profileDirectory))
            {
                Directory.Delete(_profileDirectory, recursive: true);
            }
        }
        catch
        {
            // best-effort — a Chromium profile dir can hold briefly-locked files right after exit
        }
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
