namespace TradeFix.Network;

/// <summary>Exponential backoff with a cap, per spec section 27: 1s, 2s, 4s, 8s, 16s, then capped.</summary>
public sealed class ReconnectPolicy(TimeSpan? cap = null)
{
    private static readonly TimeSpan DefaultCap = TimeSpan.FromSeconds(16);
    private int _attempt;

    public TimeSpan Cap { get; } = cap ?? DefaultCap;

    public TimeSpan NextDelay()
    {
        var seconds = Math.Pow(2, _attempt);
        _attempt++;
        var delay = TimeSpan.FromSeconds(seconds);
        return delay > Cap ? Cap : delay;
    }

    public void Reset() => _attempt = 0;
}
