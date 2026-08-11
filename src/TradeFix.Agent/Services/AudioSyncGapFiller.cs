namespace TradeFix.Agent.Services;

/// <summary>
/// Keeps received audio anchored to the sender's own audio-timeline instead of letting it drift
/// ahead of the video it accompanies. MediaHub's per-subscriber queue drops the oldest queued
/// frame under backpressure (see MediaHub remarks) — for video that's harmless, a stale frame just
/// stays on screen a moment longer. For audio it's not harmless: a dropped chunk is audio time that
/// never gets played, so every chunk after it plays back exactly one chunk-duration earlier than it
/// should, and that compounds every time it happens. Left alone this makes audio arrive audibly
/// ahead of the video frame it should line up with — the reported symptom ("his voice and the video
/// are not going the same").
///
/// Each audio chunk on the wire (see AudioChunkFraming) carries the sender's cumulative
/// captured-audio-duration in milliseconds as of the start of that chunk. Comparing consecutive
/// timestamps reveals exactly how much audio time was skipped, and inserting that much silence
/// before the chunk keeps total played duration matching total captured duration — the same
/// "freeze and wait" behavior a stale video frame already exhibits, restoring symmetry between the
/// two channels instead of "fixing" only one side.
/// </summary>
public sealed class AudioSyncGapFiller
{
    private readonly double _bytesPerMillisecond;
    private readonly int _maxGapToFillMs;
    private long? _expectedNextMs;

    /// <param name="averageBytesPerSecond">The playback format's bytes/sec (e.g.
    /// <c>WaveFormat.AverageBytesPerSecond</c>) — used to convert byte counts to durations.</param>
    /// <param name="maxGapToFillMs">Gaps larger than this are treated as a real pause/reconnect
    /// rather than a few dropped chunks, and are not filled with silence (that would mean playing
    /// several seconds of dead air) — playback simply resyncs to the new position.</param>
    public AudioSyncGapFiller(int averageBytesPerSecond, int maxGapToFillMs = 3000)
    {
        _bytesPerMillisecond = Math.Max(1, averageBytesPerSecond) / 1000.0;
        _maxGapToFillMs = maxGapToFillMs;
    }

    /// <summary>Call once per received chunk, in arrival order, before feeding the chunk itself to
    /// playback. Returns the number of bytes of silence to feed to playback immediately before the
    /// chunk (zero if there's no gap to fill).</summary>
    public int SilenceBytesBefore(long chunkTimestampMs, int chunkByteLength)
    {
        var chunkDurationMs = (long)(chunkByteLength / _bytesPerMillisecond);

        if (_expectedNextMs is not { } expected)
        {
            _expectedNextMs = chunkTimestampMs + chunkDurationMs;
            return 0;
        }

        var gapMs = chunkTimestampMs - expected;
        var silenceBytes = gapMs > 0 && gapMs <= _maxGapToFillMs
            ? (int)(gapMs * _bytesPerMillisecond)
            : 0;

        _expectedNextMs = Math.Max(expected, chunkTimestampMs) + chunkDurationMs;
        return silenceBytes;
    }

    /// <summary>Discards learned timeline state — call when a subscription reconnects, since the
    /// sender's timestamp is only meaningful relative to its own previous value.</summary>
    public void Reset() => _expectedNextMs = null;
}
