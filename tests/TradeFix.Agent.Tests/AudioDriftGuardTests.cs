using TradeFix.Agent.Services;

namespace TradeFix.Agent.Tests;

/// <summary>
/// The audio no-sound/lag regression, as a pure state machine. Two failure modes bracket the
/// design: clear-on-any-spike shreds playback into silence whenever the Master sends catch-up
/// bursts (the "now NO sound" report), while clear-only-when-huge parks playback ~1s behind the
/// video forever (the "sound lags so much" report). The guard must ride out bursts that drain
/// between spikes AND catch backlog whose floor never comes down.
/// </summary>
public sealed class AudioDriftGuardTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void SteadyLowBuffer_NeverResyncs()
    {
        var guard = new AudioDriftGuard();

        for (var i = 0; i < 200; i++)
        {
            Assert.False(guard.ShouldResync(Ms(80 + i % 40))); // healthy jitter around ~100ms
        }
    }

    [Fact]
    public void CatchUpBursts_ThatDrainBetweenSpikes_NeverResync()
    {
        var guard = new AudioDriftGuard();

        // A loaded Master: every 10th chunk arrives as part of a burst that spikes the buffer to
        // 800ms, but it drains back toward zero before the next burst — live playback, no dead
        // weight. The old instantaneous 350ms check cleared on every one of these spikes.
        for (var cycle = 0; cycle < 30; cycle++)
        {
            foreach (var buffered in new[] { 20, 60, 120, 250, 800, 600, 400, 250, 120, 40 })
            {
                Assert.False(guard.ShouldResync(Ms(buffered)), $"resynced on a draining burst (cycle {cycle}, {buffered}ms)");
            }
        }
    }

    [Fact]
    public void StandingBacklog_FloorNeverDropping_ResyncsWithinOneWindow()
    {
        var guard = new AudioDriftGuard(windowChunks: 20);

        // 900ms of dead weight with normal jitter on top: even the emptiest moments stay ~900ms.
        var resynced = false;
        for (var i = 0; i < 20 && !resynced; i++)
        {
            resynced = guard.ShouldResync(Ms(900 + i % 100));
        }

        Assert.True(resynced, "a 900ms standing backlog was never cleared");
    }

    [Fact]
    public void BorderlineStanding_UnderTheTolerance_IsLeftAlone()
    {
        var guard = new AudioDriftGuard(maxStandingDelay: Ms(400), windowChunks: 20);

        for (var i = 0; i < 100; i++)
        {
            Assert.False(guard.ShouldResync(Ms(300 + i % 50))); // floor ~300ms — a cushion, not lag
        }
    }

    [Fact]
    public void NearBufferCap_ResyncsImmediately_NotAWindowLater()
    {
        var guard = new AudioDriftGuard();

        // PC slept / device stalled: react on the very next chunk, not up to 20 chunks later.
        Assert.False(guard.ShouldResync(Ms(100)));
        Assert.True(guard.ShouldResync(Ms(1600)));
    }

    [Fact]
    public void Reset_DiscardsWindowState_SoOldSpikesCannotTriggerLater()
    {
        var guard = new AudioDriftGuard(windowChunks: 5);

        for (var i = 0; i < 4; i++)
        {
            guard.ShouldResync(Ms(900)); // 4 elevated observations, one short of a full window
        }

        guard.Reset();

        // A fresh healthy stream after the reset must not inherit the old window's elevated floor.
        for (var i = 0; i < 10; i++)
        {
            Assert.False(guard.ShouldResync(Ms(50)));
        }
    }

    [Fact]
    public void AfterAResync_TheNextWindowStartsClean()
    {
        var guard = new AudioDriftGuard(windowChunks: 5);

        var resynced = false;
        for (var i = 0; i < 5; i++)
        {
            resynced |= guard.ShouldResync(Ms(700));
        }

        Assert.True(resynced);

        // Post-clear the buffer is genuinely low again — no follow-up clears (which would eat
        // the freshly arriving audio and produce exactly the shredded-silence failure mode).
        for (var i = 0; i < 20; i++)
        {
            Assert.False(guard.ShouldResync(Ms(90)));
        }
    }
}
