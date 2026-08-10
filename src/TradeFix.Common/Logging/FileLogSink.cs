using System.Text;
using System.Text.Json;

namespace TradeFix.Common.Logging;

/// <summary>Appends log entries as JSON lines to a daily rolling file under the given directory.</summary>
public sealed class FileLogSink : ILogSink
{
    private readonly string _directory;
    private readonly object _writeLock = new();

    public FileLogSink(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public void Write(LogEntry entry)
    {
        var path = Path.Combine(_directory, $"{entry.Timestamp:yyyy-MM-dd}.log");
        var line = JsonSerializer.Serialize(entry);

        lock (_writeLock)
        {
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}
