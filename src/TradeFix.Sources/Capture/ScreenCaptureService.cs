using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TradeFix.Sources.Capture;

/// <summary>
/// Captures either the primary monitor or one specific window (via <paramref name="targetWindow"/>
/// in the constructor) at a fixed rate and encodes each frame as JPEG.
///
/// Uses GDI (<c>BitBlt</c>/<c>PrintWindow</c> + manual cursor compositing via <c>DrawIconEx</c>)
/// rather than the newer Windows.Graphics.Capture API. WGC is GPU-accelerated and composites the
/// cursor automatically — genuinely better — but requires WinRT/COM interop (custom
/// QueryInterface for <c>IGraphicsCaptureItemInterop</c>, manual Direct3D11 device bridging via
/// <c>CreateDirect3D11DeviceFromDXGIDevice</c>) that could not be verified by compiling/running
/// against the real API in this environment. GDI capture is decades-old, needs only plain
/// P/Invoke, and is far more likely to work correctly on the first real test — the right
/// tradeoff for a first working version. See docs/KNOWN_LIMITATIONS.md.
///
/// Per-window capture uses <c>PrintWindow</c> with <c>PW_RENDERFULLCONTENT</c>, which (Windows
/// 8.1+) works for most modern GPU-composited windows (browsers, Electron apps), unlike plain
/// <c>BitBlt</c> of a window's DC which often renders black for those. Some windows using
/// exclusive-fullscreen DirectX may still not capture correctly — a known GDI limitation.
/// </summary>
public sealed class ScreenCaptureService : IDisposable
{
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint DiNormal = 0x0003;
    private const uint PwRenderFullContent = 0x00000002;

    private readonly int _targetFps;
    private readonly int _maxDimension;
    private readonly IntPtr? _targetWindow;
    private readonly long _quality;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public IntPtr? TargetWindow => _targetWindow;
    public int TargetFps => _targetFps;
    public int MaxDimension => _maxDimension;
    public int Quality => (int)_quality;

    /// <summary>Fires once per captured frame with JPEG-encoded bytes. The handler is awaited
    /// before the next frame is captured, so a slow subscriber (e.g. a slow network broadcast)
    /// naturally back-pressures the capture rate instead of frames piling up in memory.
    /// Not fired when <see cref="RawFrameCaptured"/> has a subscriber — raw mode replaces the
    /// JPEG encode entirely.</summary>
    public event Func<byte[], Task>? FrameCaptured;

    /// <summary>Raw-mode alternative to <see cref="FrameCaptured"/> for the H.264 pipeline:
    /// fires with (tightly-packed top-down BGRA pixels, width, height), skipping the JPEG encode
    /// completely — the video encoder wants raw pixels, and JPEG-ing them first would waste CPU
    /// and quality. The pixel buffer is reused between frames (the handler is awaited before the
    /// next capture), so handlers must consume it before returning, not store it.</summary>
    public event Func<byte[], int, int, Task>? RawFrameCaptured;

    private byte[]? _rawBuffer;

    /// <param name="targetWindow">If set, captures only this window (see <see cref="WindowEnumerator"/>
    /// for picking one). If null, captures the whole primary monitor.</param>
    /// <param name="maxDimension">Defaults to 3840 (4K) — high enough that virtually no real
    /// monitor or window gets downscaled at all; lower it only to trade quality for bandwidth on a
    /// constrained link.</param>
    /// <param name="quality">JPEG quality 1-100. Defaults to 100 (GDI+'s maximum) — the user
    /// explicitly asked for the highest quality possible on every node, over bandwidth.</param>
    public ScreenCaptureService(int targetFps = 12, int maxDimension = 3840, IntPtr? targetWindow = null, int quality = 100)
    {
        _targetFps = targetFps;
        _maxDimension = maxDimension;
        _targetWindow = targetWindow;
        _quality = Math.Clamp(quality, 1, 100);
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => CaptureLoopAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(1.0 / _targetFps);

        // Absolute schedule, not per-iteration delays: "capture, then sleep the remainder"
        // accumulates timer quantization every tick and measurably under-delivers the configured
        // FPS by ~10-16% — while the H.264 encoder is told the nominal rate. Tracking the next
        // tick's absolute due-time makes small overshoots self-correct instead of compounding.
        var nextTick = DateTime.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            nextTick += interval;

            try
            {
                if (RawFrameCaptured is { } rawHandler)
                {
                    using var frame = _targetWindow is { } hwnd ? CaptureWindowBitmap(hwnd) : CaptureScreenBitmap();
                    if (frame is not null)
                    {
                        using var scaled = ScaleDownIfNeeded(frame);
                        var (buffer, width, height) = ExtractBgra(scaled);
                        await rawHandler.Invoke(buffer, width, height);
                    }
                }
                else
                {
                    var jpegBytes = _targetWindow is { } hwnd ? CaptureWindowAsJpeg(hwnd) : CaptureScreenAsJpeg();
                    if (jpegBytes is not null && FrameCaptured is not null)
                    {
                        await FrameCaptured.Invoke(jpegBytes);
                    }
                }
            }
            catch
            {
                // A single bad frame shouldn't kill the capture loop; next tick tries again.
            }

            var now = DateTime.UtcNow;
            var remaining = nextTick - now;
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(remaining, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            else if (remaining < -interval)
            {
                // Fell more than a full frame behind (slow capture/encode) — resynchronize the
                // schedule instead of trying to burst-catch-up with back-to-back captures.
                nextTick = now;
            }
        }
    }

    private byte[]? CaptureScreenAsJpeg()
    {
        using var bitmap = CaptureScreenBitmap();
        if (bitmap is null)
        {
            return null;
        }

        using var scaled = ScaleDownIfNeeded(bitmap);
        return EncodeJpeg(scaled);
    }

    private byte[]? CaptureWindowAsJpeg(IntPtr hwnd)
    {
        using var bitmap = CaptureWindowBitmap(hwnd);
        if (bitmap is null)
        {
            return null;
        }

        using var scaled = ScaleDownIfNeeded(bitmap);
        return EncodeJpeg(scaled);
    }

    /// <summary>Copies a bitmap's pixels into a reused, tightly-packed top-down BGRA buffer.
    /// GDI+'s Format32bppArgb is BGRA in memory on little-endian Windows — exactly what the
    /// H.264 encoder's <c>-pix_fmt bgra</c> input expects, so no per-pixel conversion happens
    /// here, only row copies (LockBits stride may exceed width*4).</summary>
    private (byte[] Buffer, int Width, int Height) ExtractBgra(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var needed = width * 4 * height;
        if (_rawBuffer is null || _rawBuffer.Length < needed)
        {
            _rawBuffer = new byte[needed];
        }

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, _rawBuffer, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return (_rawBuffer, width, height);
    }

    private Bitmap? CaptureScreenBitmap()
    {
        var screenWidth = GetSystemMetrics(SmCxScreen);
        var screenHeight = GetSystemMetrics(SmCyScreen);
        if (screenWidth <= 0 || screenHeight <= 0)
        {
            return null;
        }

        var bitmap = new Bitmap(screenWidth, screenHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var destHdc = graphics.GetHdc();
            try
            {
                var srcHdc = GetDC(IntPtr.Zero);
                try
                {
                    BitBlt(destHdc, 0, 0, screenWidth, screenHeight, srcHdc, 0, 0, SrcCopy | CaptureBlt);
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, srcHdc);
                }
            }
            finally
            {
                graphics.ReleaseHdc(destHdc);
            }

            DrawCursor(graphics, offsetX: 0, offsetY: 0, clampWidth: screenWidth, clampHeight: screenHeight);
        }

        return bitmap;
    }

    private Bitmap? CaptureWindowBitmap(IntPtr hwnd)
    {
        if (!IsWindow(hwnd) || !GetWindowRect(hwnd, out var rect))
        {
            return null; // window closed since it was picked — capture loop just skips this tick
        }

        if (IsIconic(hwnd))
        {
            // Minimized windows have no valid backing surface for PrintWindow to render from —
            // Windows itself can't produce real content here, only garbage/blank. Skipping the
            // tick (rather than encoding and sending that blank frame) means nodes keep showing
            // the last real frame instead of flashing to black the moment a window is minimized.
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var destHdc = graphics.GetHdc();
            bool printed;
            try
            {
                printed = PrintWindow(hwnd, destHdc, PwRenderFullContent);
                if (!printed)
                {
                    // PW_RENDERFULLCONTENT (the flag that's supposed to work correctly even when
                    // the window is occluded/in the background) isn't supported by every window —
                    // some older/non-composited windows only honor plain PrintWindow. Falling back
                    // rather than silently sending whatever half-drawn bitmap PrintWindow left
                    // behind (previously encoded and sent as-is — the bug behind "shows nothing
                    // when another window is on top").
                    printed = PrintWindow(hwnd, destHdc, 0);
                }
            }
            finally
            {
                graphics.ReleaseHdc(destHdc);
            }

            if (!printed)
            {
                bitmap.Dispose();
                return null; // both attempts failed — skip this tick rather than send a blank frame
            }

            DrawCursor(graphics, offsetX: -rect.Left, offsetY: -rect.Top, clampWidth: width, clampHeight: height);
        }

        return bitmap;
    }

    private static readonly ImageCodecInfo JpegEncoder =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private byte[] EncodeJpeg(Bitmap bitmap)
    {
        using var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, _quality);
        using var stream = new MemoryStream();
        bitmap.Save(stream, JpegEncoder, encoderParams);
        return stream.ToArray();
    }

    /// <summary>Draws the system cursor onto the captured frame at (screen position + offset),
    /// skipped if that falls outside the frame bounds (e.g. cursor isn't over the captured window).</summary>
    private static void DrawCursor(Graphics graphics, int offsetX, int offsetY, int clampWidth, int clampHeight)
    {
        var cursorInfo = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursorInfo) || cursorInfo.flags == 0 || cursorInfo.hCursor == IntPtr.Zero)
        {
            return; // cursor hidden or unavailable — leave the frame as-is rather than throwing
        }

        var x = cursorInfo.ptScreenPosX + offsetX;
        var y = cursorInfo.ptScreenPosY + offsetY;
        if (x < 0 || y < 0 || x >= clampWidth || y >= clampHeight)
        {
            return; // cursor isn't over the captured region
        }

        var destHdc = graphics.GetHdc();
        try
        {
            DrawIconEx(destHdc, x, y, cursorInfo.hCursor, 0, 0, 0, IntPtr.Zero, DiNormal);
        }
        finally
        {
            graphics.ReleaseHdc(destHdc);
        }
    }

    /// <summary>Returns <paramref name="source"/> itself (no copy) when it's already within
    /// bounds — a full-frame pixel copy on every single tick was pure waste, and at the new 4K
    /// default it was a real, measurable chunk of per-frame CPU cost on top of the JPEG encode
    /// itself. Callers already wrap both the original bitmap and this return value in their own
    /// <c>using</c>, so the same instance gets disposed twice in the no-scaling case — safe:
    /// System.Drawing.Common's <c>Image.Dispose()</c> is a no-op after the first call.</summary>
    private Bitmap ScaleDownIfNeeded(Bitmap source)
    {
        if (source.Width <= _maxDimension && source.Height <= _maxDimension)
        {
            return source;
        }

        var scale = (double)_maxDimension / Math.Max(source.Width, source.Height);
        var width = Math.Max(1, (int)(source.Width * scale));
        var height = Math.Max(1, (int)(source.Height * scale));

        var scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        graphics.DrawImage(source, 0, 0, width, height);
        return scaled;
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public int ptScreenPosX;
        public int ptScreenPosY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, int rasterOp);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int width, int height, int frame, IntPtr hbrFlickerFree, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
}
