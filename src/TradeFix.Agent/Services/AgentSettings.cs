namespace TradeFix.Agent.Services;

public sealed record AgentSettings
{
    public string MasterHost { get; init; } = string.Empty;
    public int MasterPort { get; init; } = 8791;
    public string NodeName { get; init; } = Environment.MachineName;
}
