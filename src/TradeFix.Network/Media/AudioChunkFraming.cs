namespace TradeFix.Network.Media;

/// <summary>
/// Wire framing for audio chunks sent over <c>/audio/{sourceId}</c>: an 8-byte little-endian
/// timestamp (the sender's cumulative captured-audio-timeline position, in milliseconds, at which
/// this chunk begins — see AudioCaptureService.ChunkCaptured) followed by the raw PCM payload.
/// The timestamp lets the receiver detect gaps left by dropped chunks (MediaHub's per-subscriber
/// queue is drop-oldest under backpressure) and fill them with silence instead of playing audio
/// back-to-back and silently drifting ahead of the video it should accompany.
/// </summary>
public static class AudioChunkFraming
{
    public const int HeaderBytes = 8;

    public static byte[] Encode(long timestampMs, byte[] pcm)
    {
        var framed = new byte[HeaderBytes + pcm.Length];
        BitConverter.TryWriteBytes(framed.AsSpan(0, HeaderBytes), timestampMs);
        Array.Copy(pcm, 0, framed, HeaderBytes, pcm.Length);
        return framed;
    }

    /// <summary>Returns false if <paramref name="framed"/> is too short to contain a header
    /// (malformed/truncated message) — caller should discard it.</summary>
    public static bool TryDecode(byte[] framed, out long timestampMs, out ArraySegment<byte> pcm)
    {
        if (framed.Length < HeaderBytes)
        {
            timestampMs = 0;
            pcm = default;
            return false;
        }

        timestampMs = BitConverter.ToInt64(framed, 0);
        pcm = new ArraySegment<byte>(framed, HeaderBytes, framed.Length - HeaderBytes);
        return true;
    }
}
