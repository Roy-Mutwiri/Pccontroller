using TradeFix.Agent.Services;

namespace TradeFix.Agent.Tests;

/// <summary>
/// Pure arithmetic tests for the audio/video sync fix: given a sequence of sender-timeline
/// timestamps (as would arrive after MediaHub drops an audio chunk under backpressure), the filler
/// must report exactly enough silence to keep played-back duration matching captured duration,
/// without over- or under-filling, and without filling implausibly large gaps (a real
/// pause/reconnect, not a few dropped chunks).
/// </summary>
public sealed class AudioSyncGapFillerTests
{
    // 24kHz, 16-bit, mono == 48000 bytes/sec == 48 bytes/ms, matching AgentHost's AudioFormat.
    private const int AverageBytesPerSecond = 48000;
    private const int BytesPerMs = 48;
    private const int ChunkBytes = BytesPerMs * 100; // a normal ~100ms chunk

    [Fact]
    public void FirstChunk_NeverInsertsSilence_RegardlessOfItsTimestamp()
    {
        var filler = new AudioSyncGapFiller(AverageBytesPerSecond);

        var silence = filler.SilenceBytesBefore(chunkTimestampMs: 12_345, ChunkBytes);

        Assert.Equal(0, silence);
    }

    [Fact]
    public void ConsecutiveChunksWithNoGap_InsertNoSilence()
    {
        var filler = new AudioSyncGapFiller(AverageBytesPerSecond);

        filler.SilenceBytesBefore(0, ChunkBytes);
        var silence = filler.SilenceBytesBefore(100, ChunkBytes); // starts exactly where the last one ended

        Assert.Equal(0, silence);
    }

    [Fact]
    public void OneDroppedChunk_InsertsExactlyOneChunkDurationOfSilence()
    {
        var filler = new AudioSyncGapFiller(AverageBytesPerSecond);

        filler.SilenceBytesBefore(0, ChunkBytes);   // establishes expected-next = 100ms
        // the chunk that should have started at 100ms was dropped by MediaHub; the next one
        // to actually arrive starts at 200ms
        var silence = filler.SilenceBytesBefore(200, ChunkBytes);

        Assert.Equal(100 * BytesPerMs, silence);
    }

    [Fact]
    public void MultipleDroppedChunks_InsertsSilenceCoveringTheWholeGap()
    {
        var filler = new AudioSyncGapFiller(AverageBytesPerSecond);

        filler.SilenceBytesBefore(0, ChunkBytes);      // expected-next = 100ms
        var silence = filler.SilenceBytesBefore(500, ChunkBytes); // 4 chunks' worth were dropped

        Assert.Equal(400 * BytesPerMs, silence);
    }

    [Fact]
    public void RunningTotalOfPlayedAudio_StaysAnchoredToSenderTimeline_AcrossRepeatedDrops()
    {
        var filler = new AudioSyncGapFiller(AverageBytesPerSecond);
        long totalPlayedMs = 0;

        long[] arrivalTimestampsMs = [0, 100, 300, 400, 700]; // gaps after the 2nd and 4th chunks
        foreach (var ts in arrivalTimestampsMs)
        {
            var silenceBytes = filler.SilenceBytesBefore(ts, ChunkBytes);
            totalPlayedMs += silenceBytes / BytesPerMs;
            totalPlayedMs += ChunkBytes / BytesPerMs;
        }

        // last chunk starts at 700ms and is 100ms long — a correctly-anchored player has played
        // exactly up to 800ms of timeline, matching what the sender actually captured, instead of
        // drifting ahead by however much audio got silently dropped along the way.
        Assert.Equal(800, totalPlayedMs);
    }

    [Fact]
    public void ImplausiblyLargeGap_IsTreatedAsAReconnectNotADrop_AndIsNotFilledWithSilence()
    {
        var filler = new AudioSyncGapFiller(AverageBytesPerSecond, maxGapToFillMs: 3000);

        filler.SilenceBytesBefore(0, ChunkBytes); // expected-next = 100ms
        var silence = filler.SilenceBytesBefore(10_000, ChunkBytes); // ~10s pause/reconnect

        Assert.Equal(0, silence);
    }

    [Fact]
    public void Reset_ForgetsPriorTimeline_SoNextChunkIsTreatedAsFirst()
    {
        var filler = new AudioSyncGapFiller(AverageBytesPerSecond);
        filler.SilenceBytesBefore(0, ChunkBytes);
        filler.SilenceBytesBefore(100, ChunkBytes);

        filler.Reset();
        var silence = filler.SilenceBytesBefore(999_999, ChunkBytes);

        Assert.Equal(0, silence);
    }
}
