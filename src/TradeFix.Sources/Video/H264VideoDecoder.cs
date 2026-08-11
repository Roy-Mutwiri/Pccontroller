using System.Diagnostics;

namespace TradeFix.Sources.Video;

/// <summary>
/// Live H.264 decoder: compressed Annex-B byte-stream chunks in, complete BMP-encoded frames out,
/// via an ffmpeg child process (<c>-f h264</c> stdin → <c>image2pipe -c:v bmp</c> stdout).
///
/// BMP was chosen as the output frame format deliberately: each frame is fully self-describing
/// (dimensions live in its own header, so mid-stream resolution changes need no side channel),
/// encoding it is nearly free for ffmpeg (BMP is raw pixels plus a 54-byte header), and WPF's
/// <c>BitmapImage</c> auto-detects it — meaning decoded frames flow through the *exact same*
/// display path (<c>LiveFramePump</c> → <c>DecodeJpeg</c>, which despite the name is a
/// general stream decoder) that JPEG frames always used. The renderer needs zero changes.
///
/// Input chunks may be split at arbitrary byte positions (they come off a WebSocket fed by
/// <see cref="H264VideoEncoder"/>'s equally arbitrary stdout chunks) — ffmpeg's H.264 parser
/// reassembles the stream itself. A decoder that starts mid-stream (a node subscribing to an
/// in-progress broadcast) shows its first frame at the next keyframe, at most a second in.
/// </summary>
public sealed class H264VideoDecoder : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    /// <summary>Fires from a background thread with each complete decoded frame as a
    /// self-contained BMP file image.</summary>
    public event Action<byte[]>? FrameDecoded;

    public H264VideoDecoder(string ffmpegPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Every flag here was validated against real measured behavior (see PROGRESS.md's H.264
        // pipeline entry for the experiments):
        // - Without -probesize 32 -analyzeduration 0, ffmpeg's input prober buffers the stream
        //   (default probesize is 5MB) and emits NOTHING while stdin stays open — fine for files,
        //   fatal for live (measured: zero frames out with the defaults, 29/30 within the live
        //   window with these).
        // - -fps_mode passthrough: raw Annex-B packets carry no timestamps (pts=N/A on every
        //   packet, confirmed with ffprobe), and ffmpeg's default output sync silently dropped
        //   2/3 of the frames because of it. Passthrough emits every decoded frame, which is
        //   exactly right for a display-on-arrival live feed.
        // - -fflags nobuffer must NOT be added, despite being the standard advice for live
        //   streams: measured against this exact stream shape it made ffmpeg emit zero frames.
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-probesize", "32", "-analyzeduration", "0", "-flags", "low_delay",
            "-f", "h264", "-i", "pipe:0",
            "-an",
            "-fps_mode", "passthrough",
            "-flush_packets", "1",
            "-f", "image2pipe", "-c:v", "bmp", "pipe:1"
        })
        {
            startInfo.ArgumentList.Add(arg);
        }

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffmpeg failed to start");
        _ = _process.StandardError.ReadToEndAsync();
        _ = Task.Run(PumpFramesAsync);
    }

    /// <summary>Feeds a chunk of the compressed stream. Chunk boundaries are irrelevant — bytes
    /// are interpreted as a continuous stream. Throws if the decoder process has died (callers
    /// should treat that as "video unavailable" and stop feeding).</summary>
    public async Task WriteAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        var stdin = _process.StandardInput.BaseStream;
        await stdin.WriteAsync(chunk, cancellationToken);
        await stdin.FlushAsync(cancellationToken);
    }

    /// <summary>Splits ffmpeg's concatenated-BMP stdout back into individual frames using each
    /// BMP's own header: bytes 0-1 are the "BM" magic, bytes 2-5 the little-endian total file
    /// size — everything needed to know exactly where one frame ends and the next begins.</summary>
    private async Task PumpFramesAsync()
    {
        try
        {
            var stdout = _process.StandardOutput.BaseStream;
            var header = new byte[6];
            while (true)
            {
                if (!await ReadExactlyAsync(stdout, header, 0, header.Length))
                {
                    return; // clean end of stream — process exited
                }

                if (header[0] != (byte)'B' || header[1] != (byte)'M')
                {
                    return; // lost sync with the BMP stream — unrecoverable, stop decoding
                }

                var fileSize = BitConverter.ToInt32(header, 2);
                if (fileSize <= header.Length || fileSize > 256 * 1024 * 1024)
                {
                    return; // implausible size — treat as lost sync
                }

                var frame = new byte[fileSize];
                Array.Copy(header, frame, header.Length);
                if (!await ReadExactlyAsync(stdout, frame, header.Length, fileSize - header.Length))
                {
                    return;
                }

                FrameDecoded?.Invoke(frame);
            }
        }
        catch
        {
            // process torn down — nothing left to pump
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset + total, count - total));
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(500))
            {
                _process.Kill();
            }
        }
        catch
        {
            // best-effort teardown
        }
        finally
        {
            _process.Dispose();
        }
    }
}
