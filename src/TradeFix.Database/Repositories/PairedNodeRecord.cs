namespace TradeFix.Database.Repositories;

public sealed record PairedNodeRecord
{
    public required string NodeId { get; init; }
    public required string Name { get; init; }
    public required string Role { get; init; }
    public required string SessionTokenHash { get; init; }
    public string? LastKnownIp { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
}
