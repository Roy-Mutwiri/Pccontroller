using System.Diagnostics;
using System.Runtime.InteropServices;
using TradeFix.Sources.Capture;

namespace TradeFix.Network.Tests;

/// <summary>
/// Verifies the "highest quality, no downscaling" change with real measurements rather than
/// "does it throw" — the same standard applied to the rest of this project's capture/audio tests.
/// Two concrete, previously-false claims are checked here: that the JPEG quality setting actually
/// changes the encoded bytes (not just accepted and ignored), and that the new default max
/// dimension (3840) leaves a real screen capture at the OS's actual native resolution instead of
/// silently scaling it down.
/// </summary>
[Collection("Notepad capture tests")]
public sealed class CaptureQualityTests
{
    [Fact]
    public async Task HigherJpegQuality_ProducesLargerEncodedFrames_ForTheSameStaticWindow()
    {
        using var notepad = Process.Start("notepad.exe");
        try
        {
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

            // Notepad's content is static (no video/animation), so any byte-size difference
            // between captures is attributable to the quality setting, not changing content.
            var highQualityFrames = await CaptureFramesAsync(found!.Handle, quality: 100, count: 3);
            var lowQualityFrames = await CaptureFramesAsync(found.Handle, quality: 15, count: 3);

            var avgHigh = highQualityFrames.Average(f => f.Length);
            var avgLow = lowQualityFrames.Average(f => f.Length);

            Assert.True(avgHigh > avgLow,
                $"Expected quality=100 frames (avg {avgHigh:F0} bytes) to be larger than quality=15 frames (avg {avgLow:F0} bytes) " +
                "of the same static window — the quality setting isn't actually affecting the encoder.");
        }
        finally
        {
            KillNotepad(notepad);
        }
    }

    [Fact]
    public async Task DefaultMaxDimension_LeavesAWholeScreenCapture_AtTheOSsActualNativeResolution()
    {
        var nativeWidth = GetSystemMetrics(SmCxScreen);
        var nativeHeight = GetSystemMetrics(SmCyScreen);
        Assert.True(nativeWidth > 0 && nativeHeight > 0, "Could not read a real native screen resolution to compare against");

        byte[]? frame = null;
        using var capture = new ScreenCaptureService(targetFps: 5); // maxDimension intentionally omitted — proving the default itself
        capture.FrameCaptured += bytes =>
        {
            frame ??= bytes;
            return Task.CompletedTask;
        };

        capture.Start();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (frame is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        capture.Stop();

        Assert.NotNull(frame);
        using var image = System.Drawing.Image.FromStream(new MemoryStream(frame!));

        Assert.True(image.Width == nativeWidth && image.Height == nativeHeight,
            $"Default-settings capture was {image.Width}x{image.Height} but the real screen is " +
            $"{nativeWidth}x{nativeHeight} — the default max dimension is silently downscaling instead of preserving native resolution.");
    }

    private static async Task<List<byte[]>> CaptureFramesAsync(IntPtr targetWindow, int quality, int count)
    {
        var frames = new List<byte[]>();
        using var capture = new ScreenCaptureService(targetFps: 5, maxDimension: 4000, targetWindow: targetWindow, quality: quality);
        capture.FrameCaptured += bytes =>
        {
            lock (frames)
            {
                frames.Add(bytes);
            }
            return Task.CompletedTask;
        };

        capture.Start();
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (frames.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        capture.Stop();
        Assert.True(frames.Count >= count, $"Expected {count} frames at quality={quality}, got {frames.Count}");
        return frames;
    }

    private static void KillNotepad(Process notepad)
    {
        if (!notepad.HasExited)
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

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
