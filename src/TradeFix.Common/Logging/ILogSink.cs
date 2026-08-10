namespace TradeFix.Common.Logging;

public interface ILogSink
{
    void Write(LogEntry entry);
}
