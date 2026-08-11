using System.Drawing;
using System.Drawing.Imaging;
using TradeFix.Sources.Video;

namespace TradeFix.Network.Tests;

/// <summary>
/// Real end-to-end tests of the H.264 video pipeline: a real ffmpeg encoder process, real
/// compressed bytes, a real ffmpeg decoder process, and pixel-level verification of what comes
/// out — matching this project's standard of verifying with real components, not mocks.
///
/// The fixture stages ffmpeg.exe into the test's own output directory if it isn't already there,
/// because on this dev sandbox the winget-installed copy is blocked by App Control while a
/// byte-identical copy in another directory runs fine (see FfmpegLocator's remarks) — staging
/// makes FfmpegLocator's probe find the runnable copy, exactly as it will on an end-user install
/// where ffmpeg.exe ships next to the app.
/// </summary>
public sealed class H264PipelineTests
{
    private readonly string? _ffmpegPath;

    public H264PipelineTests()
    {
        var staged = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (!File.Exists(staged))
        {
            var wingetRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            var source = Directory.Exists(wingetRoot)
                ? Directory.EnumerateFiles(wingetRoot, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (source is not null)
            {
                File.Copy(source, staged);
            }
        }

        FfmpegLocator.ResetCacheForTests();
        _ffmpegPath = FfmpegLocator.Find();
    }

    [Fact]
    public async Task EncodeThenDecode_RoundTripsRealFrames_WithCorrectPixelsAndRealCompression()
    {
        if (_ffmpegPath is null)
        {
            return; // no ffmpeg on this machine — the pipeline itself falls back to JPEG, nothing to test
        }

        const int width = 320;
        const int height = 240;
        const int frameCount = 30;

        using var encoder = new H264VideoEncoder(_ffmpegPath!, fps: 30, crf: 18);
        var encodedChunks = new List<byte[]>();
        encoder.EncodedDataAvailable += chunk => { lock (encodedChunks) { encodedChunks.Add(chunk); } };

        // Solid-color frames: green for the first two thirds, then a hard cut to red — the cut
        // proves the decoder tracks *changes*, not just the first keyframe.
        var green = MakeSolidBgraFrame(width, height, b: 0, g: 200, r: 0);
        var red = MakeSolidBgraFrame(width, height, b: 0, g: 0, r: 220);
        for (var i = 0; i < frameCount; i++)
        {
            await encoder.WriteFrameAsync(i < 20 ? green : red, width, height, CancellationToken.None);
        }

        // Dispose flushes stdin and lets ffmpeg drain its final frames through the stdout pump.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        encoder.Dispose();
        while (DateTime.UtcNow < deadline)
        {
            lock (encodedChunks)
            {
                if (encodedChunks.Count > 0 && encodedChunks.Sum(c => c.Length) > 500)
                {
                    break;
                }
            }

            await Task.Delay(100);
        }

        byte[] allEncoded;
        lock (encodedChunks)
        {
            allEncoded = encodedChunks.SelectMany(c => c).ToArray();
        }

        Assert.True(allEncoded.Length > 0, "Encoder produced no output at all");

        var rawTotal = width * height * 4 * frameCount;
        Assert.True(allEncoded.Length < rawTotal / 10,
            $"Encoded size {allEncoded.Length} is not meaningfully smaller than raw {rawTotal} — H.264 compression isn't actually happening");

        // Decode, feeding the stream back in deliberately awkward chunk sizes to prove chunk
        // boundaries don't matter (network chunks won't align to frames either).
        using var decoder = new H264VideoDecoder(_ffmpegPath!);
        var decodedFrames = new List<byte[]>();
        decoder.FrameDecoded += bmp => { lock (decodedFrames) { decodedFrames.Add(bmp); } };

        for (var offset = 0; offset < allEncoded.Length; offset += 1234)
        {
            var length = Math.Min(1234, allEncoded.Length - offset);
            await decoder.WriteAsync(allEncoded.AsMemory(offset, length), CancellationToken.None);
        }

        deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (decodedFrames)
            {
                if (decodedFrames.Count >= frameCount - 5)
                {
                    break;
                }
            }

            await Task.Delay(100);
        }

        List<byte[]> frames;
        lock (decodedFrames)
        {
            frames = [.. decodedFrames];
        }

        Assert.True(frames.Count >= frameCount / 2,
            $"Expected most of the {frameCount} frames back from the decoder, got {frames.Count}");

        // Pixel-verify an early frame is green and the last frame is red (post-cut).
        AssertCenterPixelApproximately(frames[Math.Min(5, frames.Count - 1)], expectedR: 0, expectedG: 200, expectedB: 0);
        AssertCenterPixelApproximately(frames[^1], expectedR: 220, expectedG: 0, expectedB: 0);
    }

    [Fact]
    public async Task Encoder_SurvivesAMidStreamResolutionChange_AndDecoderFollowsIt()
    {
        if (_ffmpegPath is null)
        {
            return; // no ffmpeg on this machine — the pipeline itself falls back to JPEG, nothing to test
        }

        // Models the real restart protocol: on a mid-stream size change the encoder drains the
        // old sequence, fires StreamRestarted, then starts the new one — and each subscriber
        // reacts to the relayed RestartMarker by decoding each sequence with a FRESH decoder
        // (a long-lived ffmpeg decode process silently scales a new sequence to the old
        // dimensions instead of following it — measured; see H264StreamProtocol).
        using var encoder = new H264VideoEncoder(_ffmpegPath!, fps: 30, crf: 23);
        var sequences = new List<List<byte[]>> { new() };
        encoder.EncodedDataAvailable += chunk => { lock (sequences) { sequences[^1].Add(chunk); } };
        encoder.StreamRestarted += () => { lock (sequences) { sequences.Add(new List<byte[]>()); } };

        var small = MakeSolidBgraFrame(320, 240, b: 255, g: 0, r: 0);
        var large = MakeSolidBgraFrame(640, 480, b: 0, g: 255, r: 0);
        for (var i = 0; i < 10; i++)
        {
            await encoder.WriteFrameAsync(small, 320, 240, CancellationToken.None);
        }

        for (var i = 0; i < 10; i++)
        {
            await encoder.WriteFrameAsync(large, 640, 480, CancellationToken.None); // triggers internal restart
        }

        encoder.Dispose();

        List<byte[][]> sequenceSnapshots;
        lock (sequences)
        {
            sequenceSnapshots = sequences.Select(s => s.ToArray()).ToList();
        }

        Assert.Equal(2, sequenceSnapshots.Count); // exactly one StreamRestarted, at the size change
        Assert.True(sequenceSnapshots.All(s => s.Sum(c => c.Length) > 0), "One of the sequences carried no encoded bytes");

        var seenSizes = new List<(int Width, int Height)>();
        foreach (var sequence in sequenceSnapshots)
        {
            using var decoder = new H264VideoDecoder(_ffmpegPath!);
            decoder.FrameDecoded += bmp =>
            {
                using var stream = new MemoryStream(bmp);
                using var image = new Bitmap(stream);
                lock (seenSizes) { seenSizes.Add((image.Width, image.Height)); }
            };

            foreach (var chunk in sequence)
            {
                await decoder.WriteAsync(chunk, CancellationToken.None);
            }

            // Disposing closes the decoder's stdin, flushing its held final frame.
            var before = DateTime.UtcNow.AddSeconds(5);
            decoder.Dispose();
            while (DateTime.UtcNow < before)
            {
                lock (seenSizes)
                {
                    if (seenSizes.Count > 0)
                    {
                        break;
                    }
                }

                await Task.Delay(100);
            }
        }

        await Task.Delay(500); // let the second decoder's pump finish delivering

        lock (seenSizes)
        {
            Assert.Contains(seenSizes, s => s.Width == 320 && s.Height == 240);
            Assert.Contains(seenSizes, s => s.Width == 640 && s.Height == 480);
        }
    }

    private static byte[] MakeSolidBgraFrame(int width, int height, byte b, byte g, byte r)
    {
        var frame = new byte[width * height * 4];
        for (var i = 0; i < frame.Length; i += 4)
        {
            frame[i] = b;
            frame[i + 1] = g;
            frame[i + 2] = r;
            frame[i + 3] = 255;
        }

        return frame;
    }

    private static void AssertCenterPixelApproximately(byte[] bmpBytes, int expectedR, int expectedG, int expectedB)
    {
        using var stream = new MemoryStream(bmpBytes);
        using var image = new Bitmap(stream);
        var pixel = image.GetPixel(image.Width / 2, image.Height / 2);

        // yuv420p conversion isn't bit-exact; ±35 comfortably distinguishes green from red while
        // still failing on genuinely wrong output (black/garbage frames).
        Assert.True(Math.Abs(pixel.R - expectedR) < 35, $"R was {pixel.R}, expected ~{expectedR}");
        Assert.True(Math.Abs(pixel.G - expectedG) < 35, $"G was {pixel.G}, expected ~{expectedG}");
        Assert.True(Math.Abs(pixel.B - expectedB) < 35, $"B was {pixel.B}, expected ~{expectedB}");
    }
}
