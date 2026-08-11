using TradeFix.Network.Media;

namespace TradeFix.Network.Tests;

/// <summary>
/// Pure unit coverage for the audio wire framing — the contract between Master and every Agent.
/// Previously exercised only incidentally inside AudioCaptureEndToEndTests, which needs a real
/// audio device and genuinely playing sound; these pin the byte-level contract with no hardware.
/// </summary>
public sealed class AudioChunkFramingTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(123_456_789L)]
    [InlineData(long.MaxValue)]
    public void EncodeThenDecode_PreservesTimestampAndExactPcmBytes(long timestamp)
    {
        var pcm = new byte[] { 1, 2, 3, 250, 251, 252 };

        var framed = AudioChunkFraming.Encode(timestamp, pcm);

        Assert.Equal(AudioChunkFraming.HeaderBytes + pcm.Length, framed.Length);
        Assert.True(AudioChunkFraming.TryDecode(framed, out var decodedTimestamp, out var decodedPcm));
        Assert.Equal(timestamp, decodedTimestamp);
        Assert.Equal(pcm, decodedPcm.ToArray());
    }

    [Fact]
    public void EmptyPcm_RoundTrips_AsAnEmptySegment()
    {
        var framed = AudioChunkFraming.Encode(42, []);

        Assert.Equal(AudioChunkFraming.HeaderBytes, framed.Length);
        Assert.True(AudioChunkFraming.TryDecode(framed, out var timestamp, out var pcm));
        Assert.Equal(42, timestamp);
        Assert.Empty(pcm);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void TruncatedMessages_AreRejected_NotMisread(int length)
    {
        // One byte short of a header must be rejected — the boundary where a partial network
        // message would otherwise decode a garbage timestamp.
        Assert.False(AudioChunkFraming.TryDecode(new byte[length], out _, out _));
    }
}
