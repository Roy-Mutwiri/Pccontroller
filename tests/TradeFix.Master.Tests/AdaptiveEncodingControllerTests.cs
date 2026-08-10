using TradeFix.Master.Services;

namespace TradeFix.Master.Tests;

/// <summary>
/// Pure state-machine tests for the auto quality/resolution throttle added after real measurement
/// showed quality-100/4K needs ~58 Mbps sustained — more than a typical home upload link — while
/// local capture/encode itself was never the bottleneck. Driven entirely by explicit timestamps
/// (no real timers, no real network), so these are fast and cannot flake on timing.
/// </summary>
public sealed class AdaptiveEncodingControllerTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartsAtTarget_NotThrottled()
    {
        var controller = new AdaptiveEncodingController(100, 3840, Start);

        Assert.Equal(100, controller.CurrentQuality);
        Assert.Equal(3840, controller.CurrentMaxDimension);
        Assert.False(controller.IsThrottled);
    }

    [Fact]
    public void FewerThanThreeDropsWithinTheWindow_DoesNotStepDown()
    {
        var controller = new AdaptiveEncodingController(100, 3840, Start);

        Assert.False(controller.RecordDrop(Start.AddSeconds(1)));
        Assert.False(controller.RecordDrop(Start.AddSeconds(2)));

        Assert.False(controller.IsThrottled);
        Assert.Equal(100, controller.CurrentQuality);
    }

    [Fact]
    public void ThreeDropsWithinTheWindow_StepsDownQualityFirst_NotResolution()
    {
        var controller = new AdaptiveEncodingController(100, 3840, Start);

        controller.RecordDrop(Start.AddSeconds(1));
        controller.RecordDrop(Start.AddSeconds(1.5));
        var steppedDown = controller.RecordDrop(Start.AddSeconds(2));

        Assert.True(steppedDown);
        Assert.True(controller.IsThrottled);
        Assert.Equal(85, controller.CurrentQuality); // 100 - 15 step
        Assert.Equal(3840, controller.CurrentMaxDimension); // resolution untouched while quality has room to give
    }

    [Fact]
    public void DropsOlderThanTheWindow_DoNotCountTowardTheThreshold()
    {
        var controller = new AdaptiveEncodingController(100, 3840, Start);

        controller.RecordDrop(Start); // this one falls outside the 3s window by the time the 3rd arrives
        controller.RecordDrop(Start.AddSeconds(5));
        var steppedDown = controller.RecordDrop(Start.AddSeconds(5.5));

        Assert.False(steppedDown); // only 2 drops within the rolling 3s window at this point
        Assert.False(controller.IsThrottled);
    }

    [Fact]
    public void RepeatedStepDowns_ExhaustQualityFirst_ThenReduceResolution_AndRespectBothFloors()
    {
        var controller = new AdaptiveEncodingController(100, 3840, Start);
        var t = Start;

        // Quality steps: 100 -> 85 -> 70 -> 55 -> 40 (floor) — 4 rounds of 3 drops each.
        for (var round = 0; round < 4; round++)
        {
            controller.RecordDrop(t.AddSeconds(0.1));
            controller.RecordDrop(t.AddSeconds(0.2));
            controller.RecordDrop(t.AddSeconds(0.3));
            t = t.AddSeconds(10); // well clear of the drop window so each round starts fresh
        }

        Assert.Equal(AdaptiveEncodingController.QualityFloor, controller.CurrentQuality);
        Assert.Equal(3840, controller.CurrentMaxDimension); // still untouched — quality had room for all 4 rounds

        // Quality is now at its floor — the next round of drops must reduce resolution instead.
        controller.RecordDrop(t.AddSeconds(0.1));
        controller.RecordDrop(t.AddSeconds(0.2));
        var steppedDown = controller.RecordDrop(t.AddSeconds(0.3));

        Assert.True(steppedDown);
        Assert.Equal(AdaptiveEncodingController.QualityFloor, controller.CurrentQuality); // stays at floor, doesn't go lower
        Assert.Equal(2880, controller.CurrentMaxDimension); // 3840 * 0.75

        // Drive it all the way down and confirm it never goes below either floor.
        for (var round = 0; round < 20; round++)
        {
            t = t.AddSeconds(10);
            controller.RecordDrop(t.AddSeconds(0.1));
            controller.RecordDrop(t.AddSeconds(0.2));
            controller.RecordDrop(t.AddSeconds(0.3));
        }

        Assert.Equal(AdaptiveEncodingController.QualityFloor, controller.CurrentQuality);
        Assert.Equal(AdaptiveEncodingController.MaxDimensionFloor, controller.CurrentMaxDimension);
    }

    [Fact]
    public void Tick_BeforeHealthyPeriodElapses_DoesNothing()
    {
        var controller = new AdaptiveEncodingController(100, 3840, Start);
        controller.RecordDrop(Start.AddSeconds(1));
        controller.RecordDrop(Start.AddSeconds(1.5));
        controller.RecordDrop(Start.AddSeconds(2)); // steps down to quality 85

        var changed = controller.Tick(Start.AddSeconds(5)); // only 3s of health so far, needs 12s

        Assert.False(changed);
        Assert.Equal(85, controller.CurrentQuality);
    }

    [Fact]
    public void Tick_AfterSustainedHealthyPeriod_StepsUp_ResolutionBeforeQuality()
    {
        var controller = new AdaptiveEncodingController(100, 3840, Start);
        var t = Start;

        // Drive quality all the way to its floor (4 rounds), then one more round to force a
        // resolution reduction too — so step-up has both dimensions to restore, in order.
        for (var round = 0; round < 4; round++)
        {
            controller.RecordDrop(t.AddSeconds(0.1));
            controller.RecordDrop(t.AddSeconds(0.2));
            controller.RecordDrop(t.AddSeconds(0.3));
            t = t.AddSeconds(10);
        }

        controller.RecordDrop(t.AddSeconds(0.1));
        controller.RecordDrop(t.AddSeconds(0.2));
        var lastDropAt = t.AddSeconds(0.3);
        controller.RecordDrop(lastDropAt);

        Assert.Equal(AdaptiveEncodingController.QualityFloor, controller.CurrentQuality);
        Assert.Equal(2880, controller.CurrentMaxDimension); // 3840 * 0.75, quality had no room left to give

        var stillTooSoon = controller.Tick(lastDropAt.AddSeconds(11));
        Assert.False(stillTooSoon);

        var steppedUp = controller.Tick(lastDropAt.AddSeconds(12.5));
        Assert.True(steppedUp);
        Assert.Equal(3840, controller.CurrentMaxDimension); // resolution restored first (2880 / 0.75 = 3840, clamped to target)
        Assert.Equal(AdaptiveEncodingController.QualityFloor, controller.CurrentQuality); // quality untouched this step
        Assert.True(controller.IsThrottled); // quality is still below target 100

        // Another full healthy period now starts climbing quality back up, one step at a time.
        var steppedUpAgain = controller.Tick(lastDropAt.AddSeconds(12.5 + 12));
        Assert.True(steppedUpAgain);
        Assert.Equal(55, controller.CurrentQuality); // 40 + 15
        Assert.Equal(3840, controller.CurrentMaxDimension);
    }

    [Fact]
    public void SetTarget_ImmediatelyOverridesAnyThrottling_AsAnExplicitUserChoiceShould()
    {
        var controller = new AdaptiveEncodingController(100, 3840, Start);
        controller.RecordDrop(Start.AddSeconds(1));
        controller.RecordDrop(Start.AddSeconds(1.5));
        controller.RecordDrop(Start.AddSeconds(2)); // throttled to quality 85

        Assert.True(controller.IsThrottled);

        controller.SetTarget(60, 1920, Start.AddSeconds(3));

        Assert.False(controller.IsThrottled);
        Assert.Equal(60, controller.TargetQuality);
        Assert.Equal(60, controller.CurrentQuality);
        Assert.Equal(1920, controller.CurrentMaxDimension);

        // The drop history should also be cleared — a fresh 2 drops right after shouldn't combine
        // with anything from before SetTarget to trigger an immediate step-down.
        Assert.False(controller.RecordDrop(Start.AddSeconds(4)));
        Assert.False(controller.RecordDrop(Start.AddSeconds(4.5)));
    }

    [Fact]
    public void Tick_WhenNotThrottled_DoesNothing_EvenAfterALongTime()
    {
        var controller = new AdaptiveEncodingController(100, 3840, Start);

        var changed = controller.Tick(Start.AddDays(1));

        Assert.False(changed);
        Assert.Equal(100, controller.CurrentQuality);
        Assert.Equal(3840, controller.CurrentMaxDimension);
    }
}
