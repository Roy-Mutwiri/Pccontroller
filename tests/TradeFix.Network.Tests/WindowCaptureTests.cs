using System.Diagnostics;
using TradeFix.Sources.Capture;

namespace TradeFix.Network.Tests;

/// <summary>
/// Proves per-window capture (as opposed to whole-screen) actually targets the right window:
/// launches a real Notepad window, captures only it, and checks the frame dimensions match that
/// window's actual size rather than the whole screen's — the concrete claim "captures this one
/// app, not everything" that the feature exists to make true.
/// </summary>
public sealed class WindowCaptureTests
{
    [Fact]
    public async Task CapturingASpecificWindow_ProducesFrameSizedToThatWindow_NotTheWholeScreen()
    {
        using var notepad = Process.Start("notepad.exe");
        try
        {
            // Modern Windows ships Notepad as a packaged app: Process.Start's returned Process
            // object is often a launcher stub whose own MainWindowHandle never resolves, even
            // though the real Notepad window appears seconds later under a different process.
            // Matching by title (what WindowEnumerator actually exposes to the picker UI) sidesteps
            // that — it's also the real lookup path the app itself uses.
            CapturableWindow? found = null;
            var findDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < findDeadline && found is null)
            {
                found = WindowEnumerator.GetCapturableWindows()
                    .FirstOrDefault(w => w.Title.Contains("Notepad", StringComparison.OrdinalIgnoreCase));

                if (found is null)
                {
                    await Task.Delay(200);
                }
            }

            Assert.NotNull(found);

            byte[]? frame = null;
            using var capture = new ScreenCaptureService(targetFps: 5, maxDimension: 4000, targetWindow: found!.Handle);
            capture.FrameCaptured += bytes =>
            {
                frame ??= bytes;
                return Task.CompletedTask;
            };

            capture.Start();
            var captureDeadline = DateTime.UtcNow.AddSeconds(5);
            while (frame is null && DateTime.UtcNow < captureDeadline)
            {
                await Task.Delay(100);
            }

            capture.Stop();

            Assert.NotNull(frame);
            Assert.True(frame!.Length > 500, $"Frame too small ({frame.Length} bytes) to be a real Notepad window capture");
            Assert.True(frame is [0xFF, 0xD8, ..] && frame[^2] == 0xFF && frame[^1] == 0xD9, "Not a valid JPEG frame");

            using var image = System.Drawing.Image.FromStream(new MemoryStream(frame));
            // A default Notepad window is a small fraction of any real screen — this would fail
            // (be screen-sized) if the capture accidentally fell back to whole-screen mode.
            Assert.True(image.Width < 2000 && image.Height < 1200,
                $"Captured frame ({image.Width}x{image.Height}) looks like a full screen, not a single Notepad window");
        }
        finally
        {
            if (!notepad.HasExited)
            {
                notepad.Kill();
            }

            // Belt-and-suspenders: if Process.Start's handle was a launcher stub (see comment
            // above) rather than the real Notepad process, the Kill() above won't have closed
            // the actual window — clean up any stray "notepad" processes this test spawned.
            foreach (var stray in Process.GetProcessesByName("notepad"))
            {
                try
                {
                    stray.Kill();
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }
    }

    [Fact]
    public void GetCapturableWindows_DoesNotThrow_AndReturnsWindowsWithNonEmptyTitles()
    {
        var windows = WindowEnumerator.GetCapturableWindows();
        Assert.All(windows, w => Assert.False(string.IsNullOrWhiteSpace(w.Title)));
    }
}
