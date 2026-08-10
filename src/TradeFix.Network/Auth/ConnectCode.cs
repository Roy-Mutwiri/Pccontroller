using System.Globalization;

namespace TradeFix.Network.Auth;

/// <summary>
/// A single copy-pasteable string combining the pairing code with the Master's address, so a
/// first-time Agent setup is "paste one thing, click Connect" instead of separately typing an IP,
/// a port, and a code (a real point of confusion — see docs/NODE_SYSTEM.md). Format:
/// <c>{pairingCode}@{host}:{port}</c>, e.g. <c>TRADE-8391@100.116.30.51:8791</c>.
/// </summary>
public static class ConnectCode
{
    public static string Format(string pairingCode, string host, int port) =>
        $"{pairingCode}@{host}:{port}";

    public static bool TryParse(string input, out string pairingCode, out string host, out int port)
    {
        pairingCode = string.Empty;
        host = string.Empty;
        port = 0;

        var trimmed = input?.Trim() ?? string.Empty;
        var atIndex = trimmed.IndexOf('@');
        if (atIndex <= 0 || atIndex == trimmed.Length - 1)
        {
            return false;
        }

        var codePart = trimmed[..atIndex];
        var addressPart = trimmed[(atIndex + 1)..];

        var colonIndex = addressPart.LastIndexOf(':');
        if (colonIndex <= 0 || colonIndex == addressPart.Length - 1)
        {
            return false;
        }

        var hostPart = addressPart[..colonIndex];
        var portPart = addressPart[(colonIndex + 1)..];

        if (!int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort))
        {
            return false;
        }

        pairingCode = codePart;
        host = hostPart;
        port = parsedPort;
        return true;
    }
}
