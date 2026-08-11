using System.Diagnostics;

namespace TradeFix.Sources.Video;

/// <summary>
/// Live H.264 encoder: raw BGRA frames in, compressed H.264 (Annex-B byte stream) out, via an
/// ffmpeg child process (<c>rawvideo</c> on stdin, <c>libx264 -tune zerolatency</c> on stdout).
///
/// This is the core of the quality fix (2026-08-11): the original pipeline sent every frame as an
/// independent JPEG, which has no compression *between* frames — the reason
/// <c>AdaptiveEncodingController</c> had to crush quality/resolution so aggressively under real
/// network pressure ("we have very bad quality"). H.264 only spends bits on what changed between
/// frames, giving roughly an order of magnitude better quality at the same bandwidth for typical
/// screen content. ffmpeg was chosen over hand-written Media Foundation COM interop for the same
/// reason GDI beat Windows.Graphics.Capture and NAudio beat raw WASAPI interop earlier in this
/// project: it's verifiable with real end-to-end tests in this environment (see
/// H264PipelineTests), while MF's IMFTransform interop could not be. ffmpeg runs as a separate
/// process, so its GPL license does not extend to this codebase.
///
/// The encoder self-restarts when the incoming frame size changes (window resized) — an H.264
/// stream has a fixed frame size per sequence; a restart begins a fresh sequence (SPS/PPS + IDR)
/// that decoders pick up cleanly. Frame dimensions are rounded down to even numbers because
/// yuv420p chroma subsampling requires them.
///
/// Not thread-safe: exactly one caller (the capture loop, which is single-threaded and awaits
/// each frame's handler) may call <see cref="WriteFrameAsync"/>.
/// </summary>
public sealed class H264VideoEncoder : IDisposable
{
    private readonly string _ffmpegPath;
    private readonly int _fps;
    private readonly int _crf;
    private Process? _process;
    private Task? _stdoutPump;
    private int _currentWidth;
    private int _currentHeight;
    private int _consecutiveFailures;
    private bool _disposed;

    /// <summary>True while executing inside the stdout pump's own call chain (its
    /// EncodedDataAvailable handlers and anything they call). Guards <see cref="Dispose"/>
    /// against waiting on the pump from within the pump — that wait can never succeed and would
    /// burn its full timeout as a video stall. AsyncLocal flows through the pump's awaits and
    /// synchronous event dispatch, exactly the paths a re-entrant Dispose arrives on.</summary>
    private static readonly AsyncLocal<bool> InsidePumpCallback = new();

    /// <summary>Fires from a background thread with each chunk of encoded H.264 bytes as ffmpeg
    /// produces them. Chunks are arbitrary byte ranges of the Annex-B stream, NOT aligned to
    /// frame/NAL boundaries — receivers must feed them to a decoder in order, as a stream.</summary>
    public event Action<byte[]>? EncodedDataAvailable;

    /// <summary>Fires when the encoder has failed repeatedly and given up (e.g. ffmpeg crashes on
    /// every restart) — the signal for the owner to fall back to the JPEG pipeline.</summary>
    public event Action? Failed;

    /// <summary>Fires when an incoming frame-size change forces a new encode sequence (fresh
    /// ffmpeg process), *after* the previous sequence's bytes have fully drained through
    /// <see cref="EncodedDataAvailable"/> and *before* any byte of the new sequence — the owner
    /// must relay <see cref="H264StreamProtocol.RestartMarker"/> to subscribers at exactly this
    /// point so their decoders restart too (see that class for why they can't just follow along).</summary>
    public event Action? StreamRestarted;

    /// <param name="crf">x264 Constant Rate Factor, lower = higher quality/bitrate. 18 is
    /// near-visually-lossless, 23 is the x264 default, 30 is visibly compressed. Mapped from the
    /// user-facing quality setting by the caller.</param>
    public H264VideoEncoder(string ffmpegPath, int fps, int crf = 21)
    {
        _ffmpegPath = ffmpegPath;
        _fps = Math.Max(1, fps);
        _crf = Math.Clamp(crf, 0, 51);
    }

    /// <summary>Feeds one raw BGRA frame (tightly packed, top-down, 4 bytes/pixel) to the encoder.
    /// Blocks (async) while writing to ffmpeg's stdin — if encoding falls behind, this
    /// back-pressures the capture loop naturally instead of buffering frames in memory. The buffer
    /// may be reused by the caller after this returns. Only the top-left even-sized region of an
    /// odd-dimensioned frame is encoded (yuv420p requires even dimensions).</summary>
    public async Task WriteFrameAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken)
    {
        var evenWidth = width & ~1;
        var evenHeight = height & ~1;
        if (_disposed || evenWidth <= 0 || evenHeight <= 0)
        {
            return;
        }

        try
        {
            if (_process is null || _process.HasExited || evenWidth != _currentWidth || evenHeight != _currentHeight)
            {
                RestartProcess(evenWidth, evenHeight);
            }

            var stdin = _process!.StandardInput.BaseStream;
            if (evenWidth == width)
            {
                // No odd column to trim — rows are contiguous; write the frame region in one go.
                await stdin.WriteAsync(bgra.AsMemory(0, evenWidth * 4 * evenHeight), cancellationToken);
            }
            else
            {
                for (var y = 0; y < evenHeight; y++)
                {
                    await stdin.WriteAsync(bgra.AsMemory(y * width * 4, evenWidth * 4), cancellationToken);
                }
            }

            await stdin.FlushAsync(cancellationToken);
            _consecutiveFailures = 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // ffmpeg died mid-write (or refused to start). Drop this frame and let the next call
            // restart it; after several consecutive failures, give up and tell the owner to fall
            // back to JPEG rather than burning a process spawn per frame forever.
            KillProcess();
            if (++_consecutiveFailures >= 3)
            {
                _disposed = true;
                Failed?.Invoke();
            }
        }
    }

    private void RestartProcess(int width, int height)
    {
        KillProcess();
        try
        {
            // Let the previous process's stdout pump finish delivering its flushed tail before
            // the new process starts producing — keeps the outgoing byte stream strictly ordered
            // across a resolution-change restart.
            _stdoutPump?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // previous pump faulted — stream continuity is already broken; decoder will resync
        }

        if (_currentWidth != 0)
        {
            StreamRestarted?.Invoke();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // -g {fps}: a keyframe every second, so a node that joins mid-stream (or loses bytes to
        // backpressure drops) recovers a clean picture within a second.
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{width}x{height}", "-r", _fps.ToString(),
            "-i", "pipe:0",
            "-an",
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
            "-pix_fmt", "yuv420p", "-crf", _crf.ToString(), "-g", _fps.ToString(),
            "-f", "h264", "pipe:1"
        })
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffmpeg failed to start");
        _process = process;
        _currentWidth = width;
        _currentHeight = height;

        // Drain stderr so ffmpeg can't block on a full pipe; content is only interesting on failure.
        _ = process.StandardError.ReadToEndAsync();

        _stdoutPump = Task.Run(async () =>
        {
            InsidePumpCallback.Value = true;
            var buffer = new byte[64 * 1024];
            try
            {
                var stdout = process.StandardOutput.BaseStream;
                int read;
                while ((read = await stdout.ReadAsync(buffer)) > 0)
                {
                    var chunk = new byte[read];
                    Array.Copy(buffer, chunk, read);
                    EncodedDataAvailable?.Invoke(chunk);
                }
            }
            catch
            {
                // process torn down — nothing left to pump
            }
        });
    }

    private void KillProcess()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(500))
            {
                process.Kill();
            }
        }
        catch
        {
            // best-effort teardown
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        KillProcess();

        if (InsidePumpCallback.Value)
        {
            // Dispose reached from within the pump's own call chain (e.g. an
            // EncodedDataAvailable handler triggering a capture restart) — waiting on the pump
            // here would wait on ourselves and always burn the full timeout. The process is
            // already killed above; the pump ends on its own as stdout closes.
            return;
        }

        try
        {
            // Wait for the stdout pump to drain ffmpeg's final flushed frames through
            // EncodedDataAvailable — without this, disposal races the last frames' delivery
            // (observed as truncated streams in H264PipelineTests).
            _stdoutPump?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // pump faulted on teardown — nothing more to drain
        }
    }
}
