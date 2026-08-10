using TradeFix.Database.Repositories;

namespace TradeFix.Network.Auth;

/// <summary>Issues pairing codes for the Master's "Pair a new node" UI (spec section 8).</summary>
public sealed class PairingService(PairingCodeRepository repository)
{
    // 10 minutes proved too tight in practice for a genuine first-time setup on a fresh PC —
    // downloading and extracting the Agent app alone can eat several minutes. 30 minutes is
    // still short enough that a stale/forgotten code isn't a meaningful standing risk.
    private static readonly TimeSpan DefaultValidity = TimeSpan.FromMinutes(30);

    public string IssueCode(TimeSpan? validity = null)
    {
        var code = PairingCodeGenerator.Generate();
        var now = DateTimeOffset.UtcNow;
        repository.Insert(code, now, now + (validity ?? DefaultValidity));
        return code;
    }
}
