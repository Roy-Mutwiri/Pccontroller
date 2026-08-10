using TradeFix.Network.Metrics;
using TradeFix.Shared.Models;

namespace TradeFix.Network.Simulation;

/// <summary>Plausible fake metrics for a simulated render node, so the Node dashboard has
/// something realistic to show before physical PC2/PC3 are connected (spec section 47).</summary>
public sealed class SimulatedMetricsProvider(int seed) : INodeMetricsProvider
{
    private readonly Random _random = new(seed);
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;

    public NodeMetrics Sample() => new()
    {
        CpuPercent = 25 + _random.NextDouble() * 20,
        GpuPercent = 30 + _random.NextDouble() * 25,
        RamPercent = 45 + _random.NextDouble() * 15,
        Fps = 59 + _random.NextDouble() * 1.5,
        LatencyMs = 1 + _random.NextDouble() * 4,
        UptimeSeconds = (long)(DateTimeOffset.UtcNow - _start).TotalSeconds
    };
}
