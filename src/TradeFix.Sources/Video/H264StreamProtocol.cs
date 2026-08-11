using System.Text;

namespace TradeFix.Sources.Video;

/// <summary>
/// The tiny bit of shared convention layered on top of the media WebSocket for H.264 mode.
///
/// Frames-vs-streams: JPEG mode sends one complete image per WebSocket message, so the receiver
/// can treat each message independently. H.264 mode sends arbitrary byte ranges of one continuous
/// compressed stream — and a long-lived ffmpeg decode process locks onto the stream's *first*
/// sequence parameters: when Master restarts the encoder (window resized, quality stepped by the
/// adaptive controller, capture settings edited), ffmpeg silently scales the new sequence to the
/// old dimensions instead of following it (measured for real — see PROGRESS.md). So Master
/// broadcasts <see cref="RestartMarker"/> as its own message right before a new encoder's first
/// bytes, telling every subscriber to throw away its decoder and start a fresh one.
///
/// Receivers distinguish the three message kinds cheaply and unambiguously:
/// a marker message equals <see cref="RestartMarker"/> exactly; a JPEG message starts FF D8 and
/// ends FF D9 (mandatory JPEG SOI/EOI bytes); anything else is H.264 stream data.
/// </summary>
public static class H264StreamProtocol
{
    public static readonly byte[] RestartMarker = Encoding.ASCII.GetBytes("TFX-H264-RESTART");

    public static bool IsRestartMarker(ReadOnlySpan<byte> message) =>
        message.SequenceEqual(RestartMarker);

    public static bool IsCompleteJpeg(ReadOnlySpan<byte> message) =>
        message.Length >= 4 &&
        message[0] == 0xFF && message[1] == 0xD8 &&
        message[^2] == 0xFF && message[^1] == 0xD9;
}
