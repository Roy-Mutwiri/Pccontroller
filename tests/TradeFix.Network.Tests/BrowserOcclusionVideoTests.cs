using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using TradeFix.Sources.Capture;

namespace TradeFix.Network.Tests;

/// <summary>
/// Regression coverage for the "YouTube video freezes (but audio keeps playing) when I switch to
/// another app" report: <see cref="BrowserSourceTests"/>' color-cycling test uses a JS
/// <c>setInterval</c> timer, which only proves Chromium's *timer* throttling is disabled. Video
/// playback goes through a separate compositor frame-submission path gated by Chromium's own
/// native window-occlusion detection — a different mechanism, only addressed by the
/// <c>--disable-features=CalculateNativeWinOcclusion</c> flag added specifically for this. This
/// test uses <c>requestAnimationFrame</c>-driven canvas rendering instead of a timer — the much
/// closer proxy to how video frames actually get scheduled for compositing — so it actually
/// exercises the mechanism that was broken, rather than re-proving the one that already worked.
/// </summary>
[Collection("Notepad capture tests")]
public sealed class BrowserOcclusionVideoTests : IDisposable
{
    private readonly string _tempHtmlPath = Path.Combine(Path.GetTempPath(), $"tradefix-rafvideo-test-{Guid.NewGuid():n}.html");
    private readonly string _profileDirectory = Path.Combine(Path.GetTempPath(), $"tradefix-rafvideo-profile-{Guid.NewGuid():n}");
    private Process? _browserProcess;
    private Process? _coveringNotepad;

    [Fact]
    public async Task RequestAnimationFrameDrivenContent_KeepsRendering_WhileWindowIsOccluded()
    {
        var browserExePath = BrowserLauncher.FindBrowserExecutable();
        Assert.NotNull(browserExePath);

        // requestAnimationFrame is the scheduling primitive video playback effectively rides on
        // for compositing new frames — unlike setInterval, rAF callbacks are the ones Chromium's
        // native-window-occlusion detection can suppress independently of JS timer throttling.
        File.WriteAllText(_tempHtmlPath, """
            <!DOCTYPE html><html><body style="margin:0;height:100vh">
            <canvas id="c" width="200" height="200" style="width:100%;height:100%"></canvas>
            <script>
            var ctx = document.getElementById('c').getContext('2d');
            var colors = ['#ff0000', '#00ff00', '#0000ff'];
            var i = 0;
            var lastSwitch = 0;
            function frame(ts) {
                if (ts - lastSwitch > 150) { i++; lastSwitch = ts; }
                ctx.fillStyle = colors[i % colors.length];
                ctx.fillRect(0, 0, 200, 200);
                requestAnimationFrame(frame);
            }
            requestAnimationFrame(frame);
            </script>
            </body></html>
            """);

        var url = new Uri(_tempHtmlPath).AbsoluteUri;
        _browserProcess = BrowserLauncher.LaunchCaptureFriendlyBrowser(browserExePath!, url, _profileDirectory);
        Assert.NotNull(_browserProcess);

        CapturableWindow? browserWindow = null;
        var findDeadline = DateTime.UtcNow.AddSeconds(15);
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

        _coveringNotepad = Process.Start("notepad.exe");
        var coveringWindow = await FindWindowByTitleAsync("Notepad", excludeHandle: browserWindow.Handle);
        Assert.NotNull(coveringWindow);

        SetWindowPos(coveringWindow!.Handle, IntPtr.Zero, browserRect.Left, browserRect.Top,
            browserRect.Right - browserRect.Left, browserRect.Bottom - browserRect.Top, SwpNoZOrder | SwpNoActivate);
        SetForegroundWindow(coveringWindow.Handle);
        await Task.Delay(500);

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
                // a torn/partial frame — skip it
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
            $"All {sampledColors.Count} requestAnimationFrame-driven samples while covered showed the same color " +
            $"({string.Join(",", distinctColors)}) — this is exactly the 'video keeps playing but stops visually " +
            "updating' symptom reported. --disable-features=CalculateNativeWinOcclusion should prevent it.");
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
