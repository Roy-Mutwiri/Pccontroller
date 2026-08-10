namespace TradeFix.Common.Logging;

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogCategory Category,
    string Source,
    string Message,
    string? Exception = null);
