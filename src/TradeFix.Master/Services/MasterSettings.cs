namespace TradeFix.Master.Services;

public sealed record MasterSettings
{
    public int ControlPort { get; init; } = 8791;
    public bool BindAllInterfaces { get; init; } = true;
    public string ServerName { get; init; } = Environment.MachineName;
}
