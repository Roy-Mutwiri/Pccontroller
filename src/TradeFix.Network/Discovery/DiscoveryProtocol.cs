namespace TradeFix.Network.Discovery;

/// <summary>Wire convention for zero-typing Master discovery (see DiscoveryBeacon /
/// MasterDiscovery). Deliberately tiny and versioned in the message names.</summary>
public static class DiscoveryProtocol
{
    /// <summary>UDP port the Master's beacon listens on — one below the default control port.</summary>
    public const int Port = 8790;

    public const string ProbeMessage = "TFX_DISCOVER_V1";

    private const string ReplyPrefix = "TFX_MASTER_V1|";

    public static string FormatReply(string masterName, int controlPort) =>
        $"{ReplyPrefix}{masterName.Replace("|", " ")}|{controlPort}";

    public static bool TryParseReply(string text, out string masterName, out int controlPort)
    {
        masterName = string.Empty;
        controlPort = 0;

        if (!text.StartsWith(ReplyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = text.Split('|');
        if (parts.Length != 3 || !int.TryParse(parts[2], out controlPort) || controlPort is <= 0 or > 65535)
        {
            return false;
        }

        masterName = parts[1];
        return masterName.Length > 0;
    }
}
