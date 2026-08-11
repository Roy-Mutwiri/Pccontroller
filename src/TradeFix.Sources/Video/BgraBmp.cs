namespace TradeFix.Sources.Video;

/// <summary>
/// Wraps a raw top-down BGRA pixel buffer in a minimal BMP file image — used for Master's local
/// self-preview in H.264 mode, where there's no JPEG anywhere in the pipeline anymore but the
/// preview UI decodes whatever self-describing image bytes it's handed (WPF auto-detects BMP).
/// Encoding is a header write plus row copies — far cheaper than a JPEG encode, which matters
/// because it runs inside the capture loop.
/// </summary>
public static class BgraBmp
{
    private const int HeaderBytes = 54;

    public static byte[] Encode(byte[] bgra, int width, int height)
    {
        // 32bpp rows are always 4-byte aligned, so no per-row padding is needed.
        var pixelBytes = width * 4 * height;
        var file = new byte[HeaderBytes + pixelBytes];

        // BITMAPFILEHEADER
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.TryWriteBytes(file.AsSpan(2), file.Length);
        BitConverter.TryWriteBytes(file.AsSpan(10), HeaderBytes);

        // BITMAPINFOHEADER — negative height marks a top-down bitmap, letting rows be copied in
        // capture order instead of bottom-up reversed.
        BitConverter.TryWriteBytes(file.AsSpan(14), 40);
        BitConverter.TryWriteBytes(file.AsSpan(18), width);
        BitConverter.TryWriteBytes(file.AsSpan(22), -height);
        BitConverter.TryWriteBytes(file.AsSpan(26), (short)1);   // planes
        BitConverter.TryWriteBytes(file.AsSpan(28), (short)32);  // bits per pixel
        BitConverter.TryWriteBytes(file.AsSpan(34), pixelBytes);

        Array.Copy(bgra, 0, file, HeaderBytes, pixelBytes);
        return file;
    }
}
