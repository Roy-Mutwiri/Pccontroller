using TradeFix.Shared.Models;

namespace TradeFix.Network.Metrics;

public interface INodeMetricsProvider
{
    NodeMetrics Sample();
}
