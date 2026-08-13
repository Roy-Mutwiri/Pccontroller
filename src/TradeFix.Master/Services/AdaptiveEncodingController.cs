namespace TradeFix.Master.Services;

/// <summary>
/// Per-capture-source state machine that automatically trades quality/resolution for smoothness
/// when a subscriber's link can't keep up, and restores them when it can — the user explicitly
/// chose this over a fixed lower default after real measurement showed local capture/encode was
/// never the bottleneck (30+ FPS achievable on this hardware regardless of settings) but quality
/// 100 near-native-resolution needs ~58 Mbps sustained, more than a typical home upload link can
/// carry (see PROGRESS.md for the numbers). <see cref="TradeFix.Network.Media.MediaHub.FrameDropped"/>
/// is the signal: MediaHub drops a frame exactly when a subscriber hasn't finished sending the
/// previous one, i.e. it's falling behind at the current data rate.
///
/// Deliberately driven entirely by explicit timestamps passed in (<see cref="RecordDrop"/>,
/// <see cref="Tick"/>) rather than reading the clock internally — makes the whole state machine a
/// pure, fast, fully deterministic unit to test without real timers or real network flakiness.
///
/// Step-down reduces quality first, then resolution — measurement showed quality barely affects
/// local CPU cost but changes data volume by ~4x, so it's the cheapest lever to pull first.
/// Step-up restores resolution first, then quality — the reverse order, since resolution loss is
/// usually the more visually obvious tradeoff and it's the last thing that should still be missing
/// once a link recovers.
/// </summary>
public sealed class AdaptiveEncodingController
{
    public const int QualityFloor = 40;
    public const int MaxDimensionFloor = 640;
    public const int FpsFloor = 8;
    private const int QualityStep = 15;
    private const double MaxDimensionStepFactor = 0.75;
    private const double FpsStepFactor = 2.0 / 3.0;
    private static readonly TimeSpan DropWindow = TimeSpan.FromSeconds(3);
    private const int DropsToStepDown = 3;
    private static readonly TimeSpan HealthyPeriodToStepUp = TimeSpan.FromSeconds(12);

    private readonly Queue<DateTimeOffset> _recentDrops = new();
    private DateTimeOffset _lastChangeAt;

    public int TargetQuality { get; private set; }
    public int TargetMaxDimension { get; private set; }
    public int TargetFps { get; private set; }
    public int CurrentQuality { get; private set; }
    public int CurrentMaxDimension { get; private set; }
    public int CurrentFps { get; private set; }

    /// <summary>True once the controller has stepped below the user's configured target at least
    /// once and hasn't fully recovered yet — lets the UI honestly show "auto-reduced" instead of
    /// silently diverging from what the Properties panel says.</summary>
    public bool IsThrottled => CurrentQuality < TargetQuality || CurrentMaxDimension < TargetMaxDimension
        || CurrentFps < TargetFps;

    public AdaptiveEncodingController(int targetQuality, int targetMaxDimension, DateTimeOffset now, int targetFps = 30)
    {
        SetTarget(targetQuality, targetMaxDimension, now, targetFps);
    }

    /// <summary>Call when the user explicitly changes settings (Properties panel Apply) — an
    /// explicit choice should immediately win over any in-progress throttling, not be treated as
    /// something to cautiously step back up to.</summary>
    public void SetTarget(int quality, int maxDimension, DateTimeOffset now, int fps = 30)
    {
        TargetQuality = quality;
        TargetMaxDimension = maxDimension;
        TargetFps = fps;
        CurrentQuality = quality;
        CurrentMaxDimension = maxDimension;
        CurrentFps = fps;
        _recentDrops.Clear();
        _lastChangeAt = now;
    }

    /// <summary>Call every time <see cref="TradeFix.Network.Media.MediaHub.FrameDropped"/> fires
    /// for this source. Returns true if this drop pushed the controller over the threshold and it
    /// stepped down — the caller should restart capture with the new Current* values.</summary>
    public bool RecordDrop(DateTimeOffset now)
    {
        _recentDrops.Enqueue(now);
        while (_recentDrops.Count > 0 && now - _recentDrops.Peek() > DropWindow)
        {
            _recentDrops.Dequeue();
        }

        if (_recentDrops.Count < DropsToStepDown)
        {
            return false;
        }

        _recentDrops.Clear();
        return StepDown(now);
    }

    /// <summary>Call periodically (e.g. once a second) regardless of drop activity. Returns true
    /// if a sustained healthy period let the controller step back up toward target — the caller
    /// should restart capture with the new Current* values.</summary>
    public bool Tick(DateTimeOffset now)
    {
        if (!IsThrottled)
        {
            return false;
        }

        if (now - _lastChangeAt < HealthyPeriodToStepUp)
        {
            return false;
        }

        var changed = StepUp();
        if (changed)
        {
            _lastChangeAt = now;
        }

        return changed;
    }

    private bool StepDown(DateTimeOffset now)
    {
        bool changed;
        if (CurrentQuality > QualityFloor)
        {
            CurrentQuality = Math.Max(QualityFloor, CurrentQuality - QualityStep);
            changed = true;
        }
        else if (CurrentMaxDimension > MaxDimensionFloor)
        {
            CurrentMaxDimension = Math.Max(MaxDimensionFloor, (int)(CurrentMaxDimension * MaxDimensionStepFactor));
            changed = true;
        }
        else if (CurrentFps > FpsFloor)
        {
            // Frame rate is the last lever — a choppier picture beats a late one, and on links
            // that can't even carry quality-floor 640px (e.g. a relayed path sharing a thin
            // uplink), halving-ish the frame rate is the only remaining way to stay live.
            CurrentFps = Math.Max(FpsFloor, (int)(CurrentFps * FpsStepFactor));
            changed = true;
        }
        else
        {
            changed = false; // already at all three floors — nothing left to give
        }

        if (changed)
        {
            _lastChangeAt = now;
        }

        return changed;
    }

    private bool StepUp()
    {
        // Reverse of step-down: the last thing sacrificed is the first thing restored.
        if (CurrentFps < TargetFps)
        {
            CurrentFps = Math.Min(TargetFps, (int)Math.Ceiling(CurrentFps / FpsStepFactor));
            return true;
        }

        if (CurrentMaxDimension < TargetMaxDimension)
        {
            CurrentMaxDimension = Math.Min(TargetMaxDimension, (int)(CurrentMaxDimension / MaxDimensionStepFactor));
            return true;
        }

        if (CurrentQuality < TargetQuality)
        {
            CurrentQuality = Math.Min(TargetQuality, CurrentQuality + QualityStep);
            return true;
        }

        return false;
    }
}
