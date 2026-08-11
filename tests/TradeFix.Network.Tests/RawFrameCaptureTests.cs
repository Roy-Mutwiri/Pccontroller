using TradeFix.Sources.Capture;

namespace TradeFix.Network.Tests;

/// <summary>
/// Real-capture coverage for <see cref="ScreenCaptureService"/>'s raw-frame mode (the H.264
/// pipeline's input): captures the actual primary monitor and verifies the raw BGRA output is
/// dimensionally coherent and contains real pixel data — same real-hardware standard as
/// <c>ScreenCaptureEndToEndTests</c> for the JPEG path.
/// </summary>
public sealed class RawFrameCaptureTests
{
    [Fact]
    public async Task RawMode_CapturesRealScreenPixels_WithCoherentDimensions()
    {
        using var capture = new ScreenCaptureService(targetFps: 10, maxDimension: 1280);

        var completion = new TaskCompletionSource<(byte[] Bgra, int Width, int Height)>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.RawFrameCaptured += (bgra, width, height) =>
        {
            // Copy before returning — the service reuses the buffer between frames by contract.
            var snapshot = new byte[width * 4 * height];
            Array.Copy(bgra, snapshot, snapshot.Length);
            completion.TrySetResult((snapshot, width, height));
            return Task.CompletedTask;
        };

        capture.Start();
        try
        {
            var done = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(done == completion.Task, "No raw frame arrived from a real screen capture within 10s");
        }
        finally
        {
            capture.Stop();
        }

        var (frame, w, h) = await completion.Task;
        Assert.True(w is > 0 and <= 1280, $"Width {w} outside expected bounds");
        Assert.True(h is > 0 and <= 1280, $"Height {h} outside expected bounds");
        Assert.Equal(w * 4 * h, frame.Length);

        // A real desktop is never a single flat color — if every pixel matches the first one,
        // we captured garbage (or a genuinely blank surface), not the actual screen.
        var first = BitConverter.ToUInt32(frame, 0);
        var anyDifferent = false;
        for (var i = 4; i < frame.Length; i += 4)
        {
            if (BitConverter.ToUInt32(frame, i) != first)
            {
                anyDifferent = true;
                break;
            }
        }

        Assert.True(anyDifferent, "Every captured pixel is identical — raw capture produced a blank/garbage frame");
    }
}
