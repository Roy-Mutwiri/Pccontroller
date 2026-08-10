namespace TradeFix.Shared.Models;

public sealed record NodeMetrics
{
    public double CpuPercent { get; init; }
    public double GpuPercent { get; init; }
    public double RamPercent { get; init; }
    public double Fps { get; init; }
    public double LatencyMs { get; init; }
    public long UptimeSeconds { get; init; }
}
