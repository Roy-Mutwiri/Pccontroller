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
    /// naturally back-pressures the capture rate instead of frames piling up in memory.</summary>
    public event Func<byte[], Task>? FrameCaptured;

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

        while (!cancellationToken.IsCancellationRequested)
        {
            var frameStart = DateTime.UtcNow;

            try
            {
                var jpegBytes = _targetWindow is { } hwnd ? CaptureWindowAsJpeg(hwnd) : CaptureScreenAsJpeg();
                if (jpegBytes is not null && FrameCaptured is not null)
                {
                    await FrameCaptured.Invoke(jpegBytes);
                }
            }
            catch
            {
                // A single bad frame shouldn't kill the capture loop; next tick tries again.
            }

            var elapsed = DateTime.UtcNow - frameStart;
            var remaining = interval - elapsed;
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
        }
    }

    private byte[]? CaptureScreenAsJpeg()
    {
        var screenWidth = GetSystemMetrics(SmCxScreen);
        var screenHeight = GetSystemMetrics(SmCyScreen);
        if (screenWidth <= 0 || screenHeight <= 0)
        {
            return null;
        }

        using var bitmap = new Bitmap(screenWidth, screenHeight, PixelFormat.Format32bppArgb);
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

        using var scaled = ScaleDownIfNeeded(bitmap);
        return EncodeJpeg(scaled);
    }

    private byte[]? CaptureWindowAsJpeg(IntPtr hwnd)
    {
        if (!IsWindow(hwnd) || !GetWindowRect(hwnd, out var rect))
        {
            return null; // window closed since it was picked — capture loop just skips this tick
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var destHdc = graphics.GetHdc();
            try
            {
                PrintWindow(hwnd, destHdc, PwRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(destHdc);
            }

            DrawCursor(graphics, offsetX: -rect.Left, offsetY: -rect.Top, clampWidth: width, clampHeight: height);
        }

        using var scaled = ScaleDownIfNeeded(bitmap);
        return EncodeJpeg(scaled);
    }

    private byte[] EncodeJpeg(Bitmap bitmap)
    {
        var jpegEncoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, _quality);
        using var stream = new MemoryStream();
        bitmap.Save(stream, jpegEncoder, encoderParams);
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

    private Bitmap ScaleDownIfNeeded(Bitmap source)
    {
        if (source.Width <= _maxDimension && source.Height <= _maxDimension)
        {
            return new Bitmap(source);
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
}
