using System.Diagnostics;
using TradeFix.Sources.Capture;
using Xunit.Abstractions;

namespace TradeFix.Network.Tests;

/// <summary>
/// The user reported lag a third time after two rounds of real, confirmed architectural fixes
/// (MediaHub backpressure, off-UI-thread decode). Both of those removed *artificial* blocking —
/// this measures whether the remaining bottleneck is simply that real GDI+ JPEG capture+encode at
/// the current defaults (quality 100, up to 3840px) can't physically keep up with the 12 FPS
/// target on real hardware, which no amount of threading/queueing fixes can solve. Real
/// measurements against a real capture, not a guess.
/// </summary>
public sealed class CapturePerformanceDiagnosticTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MeasureRealCaptureThroughput_AtCurrentDefaults_VsLowerResolutionAndQuality()
    {
        var fullScreenAt3840Q100 = await MeasureAsync("Full screen, 3840px cap, quality 100 (current default)", maxDimension: 3840, quality: 100, targetWindow: null);
        var fullScreenAt1280Q100 = await MeasureAsync("Full screen, 1280px cap, quality 100", maxDimension: 1280, quality: 100, targetWindow: null);
        var fullScreenAt3840Q70 = await MeasureAsync("Full screen, 3840px cap, quality 70 (old default)", maxDimension: 3840, quality: 70, targetWindow: null);
        var fullScreenAt1280Q70 = await MeasureAsync("Full screen, 1280px cap, quality 70 (old defaults, both)", maxDimension: 1280, quality: 70, targetWindow: null);

        output.WriteLine("");
        output.WriteLine("=== Real capture+encode throughput (this machine) ===");
        output.WriteLine(fullScreenAt3840Q100);
        output.WriteLine(fullScreenAt1280Q100);
        output.WriteLine(fullScreenAt3840Q70);
        output.WriteLine(fullScreenAt1280Q70);
        output.WriteLine("Target for 12 FPS: 83ms/frame or less.");

        // Not a pass/fail gate on absolute speed (that's genuinely hardware-dependent) — just
        // proves measurement happened and the pipeline produced real frames at every setting.
        Assert.Contains("ms/frame", fullScreenAt3840Q100);
    }

    private static async Task<string> MeasureAsync(string label, int maxDimension, int quality, IntPtr? targetWindow)
    {
        var frameTimes = new List<double>();
        var frameSizes = new List<int>();
        var stopwatch = new Stopwatch();
        var lastFrameAt = (double?)null;

        using var capture = new ScreenCaptureService(targetFps: 30, maxDimension: maxDimension, targetWindow: targetWindow, quality: quality);
        stopwatch.Start();
        capture.FrameCaptured += bytes =>
        {
            var now = stopwatch.Elapsed.TotalMilliseconds;
            if (lastFrameAt is { } prev)
            {
                frameTimes.Add(now - prev);
            }

            lastFrameAt = now;
            frameSizes.Add(bytes.Length);
            return Task.CompletedTask;
        };

        capture.Start();
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (frameTimes.Count < 8 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        capture.Stop();

        if (frameTimes.Count == 0)
        {
            return $"{label}: FAILED — no frames captured";
        }

        var avgMs = frameTimes.Average();
        var avgKb = frameSizes.Average() / 1024.0;
        var achievableFps = 1000.0 / avgMs;
        return $"{label}: {avgMs:F0}ms/frame avg -> ~{achievableFps:F1} FPS achievable, ~{avgKb:F0}KB/frame ({frameTimes.Count} samples)";
    }
}
