namespace TradeFix.Agent.Services;

/// <summary>
/// Decides when audio playback has genuinely fallen behind live and should hard-resync (clear the
/// buffered backlog), as opposed to momentarily holding a burst that will drain by itself.
///
/// Why this needs care: audio chunks arrive at exactly real-time rate, so any backlog that ever
/// accumulates never drains on its own — playback runs permanently that far behind the video. But
/// an instantaneous "buffer > threshold → clear" check (the first attempt at fixing that) is wrong
/// in the other direction: a loaded Master legitimately sends several chunks back-to-back
/// (catch-up bursts), which spike the buffer for a moment and then drain fully between bursts.
/// Clearing on every spike shredded playback into fragments — the field report was "now NO sound".
///
/// The distinguishing signal is the buffer's MINIMUM level across a window of chunks: a burst
/// spikes the maximum but the minimum still touches ~zero between bursts, while true standing lag
/// keeps even the minimum elevated. So: track the minimum buffered duration seen at each chunk
/// arrival over a rolling window (~2s of chunks), and only resync when that minimum stayed above
/// the tolerance the whole window — meaning there is real dead weight that will never drain.
/// </summary>
public sealed class AudioDriftGuard(TimeSpan? maxStandingDelay = null, int windowChunks = 20)
{
    // 250ms standing tolerance: with the Master's audio pump now running at elevated priority
    // (bursts are the exception, not the norm), the buffer floor sits near zero on a healthy
    // link — anything persistently above a quarter second is genuine dead weight, and clearing
    // it keeps the voice within a blink of the video instead of noticeably trailing it.
    private readonly TimeSpan _maxStandingDelay = maxStandingDelay ?? TimeSpan.FromMilliseconds(250);
    private readonly int _windowChunks = Math.Max(1, windowChunks);

    /// <summary>Backstop for the pathological case (PC slept, device stalled for seconds): don't
    /// wait a whole window to react when the buffer is near its hard cap.</summary>
    private static readonly TimeSpan ImmediateClearLevel = TimeSpan.FromMilliseconds(1500);

    private TimeSpan _minBufferedInWindow = TimeSpan.MaxValue;
    private int _chunksInWindow;

    /// <summary>Call once per received chunk with the playback buffer's current level (BEFORE
    /// adding the new chunk). True means playback is genuinely stuck behind live — the caller
    /// should clear the buffered backlog and re-anchor its gap filler.</summary>
    public bool ShouldResync(TimeSpan bufferedBeforeAdd)
    {
        if (bufferedBeforeAdd >= ImmediateClearLevel)
        {
            Reset();
            return true;
        }

        if (bufferedBeforeAdd < _minBufferedInWindow)
        {
            _minBufferedInWindow = bufferedBeforeAdd;
        }

        if (++_chunksInWindow < _windowChunks)
        {
            return false;
        }

        var standingLag = _minBufferedInWindow > _maxStandingDelay;
        Reset();
        return standingLag;
    }

    /// <summary>Start a fresh observation window — call after any clear/reconnect so pre-reset
    /// levels can't influence the next decision.</summary>
    public void Reset()
    {
        _minBufferedInWindow = TimeSpan.MaxValue;
        _chunksInWindow = 0;
    }
}
