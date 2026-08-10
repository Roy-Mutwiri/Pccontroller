namespace TradeFix.Common.Logging;

/// <summary>Fans a single log call out to every registered sink (in-memory UI viewer, file, etc).</summary>
public sealed class LogBus : ILogSink
{
    private readonly List<ILogSink> _sinks = [];

    public void AddSink(ILogSink sink) => _sinks.Add(sink);

    public void Write(LogEntry entry)
    {
        foreach (var sink in _sinks)
        {
            sink.Write(entry);
        }
    }

    public void Write(LogCategory category, string source, string message, Exception? exception = null) =>
        Write(new LogEntry(DateTimeOffset.UtcNow, category, source, message, exception?.ToString()));
}
