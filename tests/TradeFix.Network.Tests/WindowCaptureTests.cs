using System.Diagnostics;
using System.Runtime.InteropServices;
using TradeFix.Sources.Capture;

namespace TradeFix.Network.Tests;

/// <summary>
/// Proves per-window capture (as opposed to whole-screen) actually targets the right window:
/// launches a real Notepad window, captures only it, and checks the frame dimensions match that
/// window's actual size rather than the whole screen's — the concrete claim "captures this one
/// app, not everything" that the feature exists to make true.
/// </summary>
[Collection("Notepad capture tests")]
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

    /// <summary>
    /// The user reported the captured app "shows nothing" once another window covers it. The
    /// original code never checked PrintWindow's return value — a minimized window has no valid
    /// surface to render from, PrintWindow silently fails, and the old code encoded and sent
    /// whatever was left in the (blank) bitmap anyway. This proves the fix: capturing a minimized
    /// window produces no frame at all (skipped), not a blank one — and capture resumes normally
    /// the moment the window is restored.
    /// </summary>
    [Fact]
    public async Task MinimizedWindow_ProducesNoFrame_InsteadOfABlankOne_AndResumesOnceRestored()
    {
        using var notepad = Process.Start("notepad.exe");
        try
        {
            var found = await FindWindowByTitleAsync("Notepad");
            Assert.NotNull(found);

            ShowWindow(found!.Handle, SwMinimize);
            var minimizeDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!IsIconic(found.Handle) && DateTime.UtcNow < minimizeDeadline)
            {
                await Task.Delay(50);
            }
            Assert.True(IsIconic(found.Handle), "Test setup failed: window never actually minimized");

            byte[]? frameWhileMinimized = null;
            using var capture = new ScreenCaptureService(targetFps: 5, maxDimension: 4000, targetWindow: found.Handle);
            capture.FrameCaptured += bytes =>
            {
                frameWhileMinimized ??= bytes;
                return Task.CompletedTask;
            };

            capture.Start();
            await Task.Delay(1500); // several capture intervals at 5 FPS — plenty of chances to (wrongly) produce a frame
            Assert.Null(frameWhileMinimized); // this is the actual regression check: no blank frame while minimized

            ShowWindow(found.Handle, SwRestore);
            var frameAfterRestore = await WaitForFrameAsync(() => frameWhileMinimized);
            capture.Stop();

            Assert.NotNull(frameAfterRestore);
            AssertIsRealNonBlankJpeg(frameAfterRestore!);
        }
        finally
        {
            KillNotepad(notepad);
        }
    }

    /// <summary>
    /// The other half of the same bug: even when NOT minimized, PrintWindow can fail for a window
    /// that's fully covered by another one on top of it — the original code didn't check for that
    /// either. Covers a real Notepad window with a second one, captures the covered window anyway,
    /// and asserts whatever comes back is genuine, non-blank content — not the solid-black frame
    /// the old bug would have silently sent.
    /// </summary>
    [Fact]
    public async Task OccludedButNotMinimizedWindow_StillCapturesRealContent_NotABlankFrame()
    {
        using var target = Process.Start("notepad.exe");
        using var coveringNotepad = Process.Start("notepad.exe");
        try
        {
            var targetWindow = await FindWindowByTitleAsync("Notepad", excludeHandle: null);
            Assert.NotNull(targetWindow);

            var coveringWindow = await FindWindowByTitleAsync("Notepad", excludeHandle: targetWindow!.Handle);
            Assert.NotNull(coveringWindow);

            Assert.True(GetWindowRect(targetWindow.Handle, out var targetRect), "Could not read target window's rect");

            // Move the covering window exactly on top of the target and bring it to the foreground.
            SetWindowPos(coveringWindow!.Handle, IntPtr.Zero, targetRect.Left, targetRect.Top,
                targetRect.Right - targetRect.Left, targetRect.Bottom - targetRect.Top, SwpNoZOrder | SwpNoActivate);
            SetForegroundWindow(coveringWindow.Handle);
            await Task.Delay(300); // let the compositor actually finish restacking/painting

            byte[]? frame = null;
            using var capture = new ScreenCaptureService(targetFps: 5, maxDimension: 4000, targetWindow: targetWindow.Handle);
            capture.FrameCaptured += bytes =>
            {
                frame ??= bytes;
                return Task.CompletedTask;
            };

            capture.Start();
            var captured = await WaitForFrameAsync(() => frame);
            capture.Stop();

            Assert.NotNull(captured); // fails loudly if PrintWindow can't handle this window while covered
            AssertIsRealNonBlankJpeg(captured!);
        }
        finally
        {
            KillNotepad(target);
            KillNotepad(coveringNotepad);
        }
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

    private static async Task<byte[]?> WaitForFrameAsync(Func<byte[]?> getFrame)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (getFrame() is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        return getFrame();
    }

    private static void AssertIsRealNonBlankJpeg(byte[] frame)
    {
        Assert.True(frame.Length > 500, $"Frame too small ({frame.Length} bytes) to be a real window capture");
        Assert.True(frame is [0xFF, 0xD8, ..] && frame[^2] == 0xFF && frame[^1] == 0xD9, "Not a valid JPEG frame");

        using var image = (System.Drawing.Bitmap)System.Drawing.Image.FromStream(new MemoryStream(frame));
        var sampleCount = 0;
        var nonBlackCount = 0;
        for (var x = 0; x < image.Width; x += Math.Max(1, image.Width / 20))
        {
            for (var y = 0; y < image.Height; y += Math.Max(1, image.Height / 20))
            {
                sampleCount++;
                var pixel = image.GetPixel(x, y);
                if (pixel.R != 0 || pixel.G != 0 || pixel.B != 0)
                {
                    nonBlackCount++;
                }
            }
        }

        Assert.True(nonBlackCount > 0,
            $"All {sampleCount} sampled pixels were pure black — this is exactly the blank-frame bug " +
            "(PrintWindow failing silently and the old code encoding the empty bitmap anyway), not real captured content.");
    }

    private static void KillNotepad(Process? notepad)
    {
        if (notepad is { HasExited: false })
        {
            notepad.Kill();
        }

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

    private const int SwMinimize = 6;
    private const int SwRestore = 9;
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
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
