using System.Security.Cryptography;

namespace TradeFix.Network.Auth;

/// <summary>Generates human-typeable pairing codes like "TRADE-8391" (spec section 8).</summary>
public static class PairingCodeGenerator
{
    public static string Generate()
    {
        var digits = RandomNumberGenerator.GetInt32(0, 10_000);
        return $"TRADE-{digits:D4}";
    }
}
