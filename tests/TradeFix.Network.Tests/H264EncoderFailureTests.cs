using TradeFix.Sources.Video;

namespace TradeFix.Network.Tests;

/// <summary>
/// Coverage for the encoder's give-up-and-fall-back contract — the path a machine hits when
/// something (AV, App Control, corporate policy) blocks or breaks ffmpeg mid-run. This is not
/// hypothetical: this project's own dev sandbox blocks ffmpeg.exe by path (see FfmpegLocator's
/// remarks). If <c>Failed</c> regressed, an affected user would get a permanently black source
/// instead of degraded-but-working JPEG video.
///
/// The "broken ffmpeg" is a path that doesn't exist: Process.Start throws on every restart
/// attempt — the same failure shape App Control produces when it blocks the exe (observed for
/// real on this dev sandbox: Start() throws "Application Control policy has blocked this file").
/// </summary>
public sealed class H264EncoderFailureTests
{
    [Fact]
    public async Task RepeatedProcessFailures_FireFailedExactlyOnce_ThenGoQuiet()
    {
        var brokenFfmpeg = Path.Combine(Path.GetTempPath(), $"nonexistent-ffmpeg-{Guid.NewGuid():n}.exe");
        Assert.False(File.Exists(brokenFfmpeg));

        using var encoder = new H264VideoEncoder(brokenFfmpeg, fps: 10, crf: 23);

        var failedCount = 0;
        encoder.Failed += () => Interlocked.Increment(ref failedCount);
        var producedOutput = false;
        encoder.EncodedDataAvailable += _ => producedOutput = true;

        // 4x4 BGRA frame. Every write fails to (re)start the missing exe — the third consecutive
        // failure must trip the give-up threshold.
        var frame = new byte[4 * 4 * 4];
        for (var attempt = 0; attempt < 15 && failedCount == 0; attempt++)
        {
            await encoder.WriteFrameAsync(frame, 4, 4, CancellationToken.None);
        }

        Assert.Equal(1, failedCount);

        // Once given up, further writes must be inert: no new processes, no second Failed.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await encoder.WriteFrameAsync(frame, 4, 4, CancellationToken.None);
        }

        Assert.Equal(1, failedCount);
        Assert.False(producedOutput, "A broken encoder must never deliver encoded bytes");
    }
}
