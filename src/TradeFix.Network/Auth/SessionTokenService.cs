using System.Security.Cryptography;

namespace TradeFix.Network.Auth;

/// <summary>
/// Session tokens are opaque, high-entropy secrets issued once at pairing time. Only the SHA-256
/// hash is ever persisted (spec section 29: "store credentials securely") — the raw token exists
/// only in memory on the Master at issuance and on the Agent's local encrypted-at-rest config.
/// </summary>
public static class SessionTokenService
{
    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Hash(string token)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    public static bool Verify(string token, string expectedHash) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(Hash(token)),
            System.Text.Encoding.UTF8.GetBytes(expectedHash));
}
