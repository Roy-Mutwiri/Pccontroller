using TradeFix.Network.Metrics;

namespace TradeFix.Network.Tests;

/// <summary>
/// Regression coverage for the 2026-08-11 crash fix: <see cref="BasicNodeMetricsProvider"/> used
/// to construct a <c>System.Diagnostics.PerformanceCounter</c>, which crashed a real render node's
/// Agent process every ~9-14 seconds with an uncatchable <c>AccessViolationException</c> (confirmed
/// via that PC's Windows Event Log — see docs/PROGRESS.md). These tests call the real Win32 APIs
/// (<c>GetSystemTimes</c>, <c>GlobalMemoryStatusEx</c>) that replaced it — no mocking, since a mock
/// can't catch "this native call crashes on some real machines" the way an actual call can.
/// </summary>
public sealed class BasicNodeMetricsProviderTests
{
    [Fact]
    public void Sample_ReturnsPlausibleValues_OnRealHardware()
    {
        var provider = new BasicNodeMetricsProvider();

        var first = provider.Sample();
        Assert.InRange(first.RamPercent, 0, 100);
        Assert.InRange(first.CpuPercent, 0, 100);
        Assert.Equal(0, first.GpuPercent);
    }

    [Fact]
    public void Sample_SecondCallAfterRealDelay_ReturnsANonNegativeCpuDelta()
    {
        // The first call has no prior GetSystemTimes sample to diff against, so it always reports
        // 0 (see BasicNodeMetricsProvider.SampleCpuPercent) — the second call, after real elapsed
        // time, is the one that exercises the actual delta computation this fix depends on.
        var provider = new BasicNodeMetricsProvider();
        provider.Sample();

        Thread.Sleep(200);
        var second = provider.Sample();

        Assert.InRange(second.CpuPercent, 0, 100);
    }

    [Fact]
    public void Sample_CalledRepeatedly_NeverThrows()
    {
        // Mirrors the real failure mode: AgentConnection's heartbeat calls Sample() every 2
        // seconds for as long as the connection is alive. The bug this fix addresses crashed the
        // whole process (uncatchable) on effectively the first or second such call on affected
        // hardware — repeated real calls here are the closest a unit test can get to that
        // scenario without an actual corrupted-perfcounter machine.
        var provider = new BasicNodeMetricsProvider();

        for (var i = 0; i < 10; i++)
        {
            var metrics = provider.Sample();
            Assert.InRange(metrics.RamPercent, 0, 100);
            Assert.True(metrics.UptimeSeconds >= 0);
        }
    }
}
