namespace TradeFix.Shared.Models;

public sealed record AudioSourceConfig
{
    public bool HasAudio { get; init; }
    public double VolumeDb { get; init; }
    public bool Muted { get; init; }
    public double GainDb { get; init; }
    public bool MonitoringEnabled { get; init; }
}
