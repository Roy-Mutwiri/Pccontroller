using System.Collections.Concurrent;

namespace TradeFix.Common.Logging;

/// <summary>Ring buffer of recent log entries for the UI log viewer (spec section 32).</summary>
public sealed class InMemoryLogSink(int capacity = 2000) : ILogSink
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public event Action<LogEntry>? EntryAdded;

    public void Write(LogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > capacity && _entries.TryDequeue(out _))
        {
        }

        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot() => _entries.ToArray();
}
