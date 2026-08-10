using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TradeFix.Network;

/// <summary>
/// Picks the address the Master should advertise to Agents, so the operator never has to look up
/// or type an IP manually. Preference order: Tailscale (CGNAT 100.64.0.0/10 — works across
/// different physical networks, which is exactly the case that caused confusion before this
/// existed), then a private LAN address, then loopback as a last resort for same-machine testing.
/// </summary>
public static class NetworkAddressHelper
{
    public static string GetBestAdvertisableAddress()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Where(a => !IPAddress.IsLoopback(a))
            .Where(a => !IsLinkLocal(a))
            .ToList();

        var tailscale = addresses.FirstOrDefault(IsTailscaleAddress);
        if (tailscale is not null)
        {
            return tailscale.ToString();
        }

        var lan = addresses.FirstOrDefault(IsPrivateLan);
        if (lan is not null)
        {
            return lan.ToString();
        }

        return IPAddress.Loopback.ToString();
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    /// <summary>Tailscale assigns addresses from the CGNAT range 100.64.0.0/10 (100.64.0.0–100.127.255.255).</summary>
    private static bool IsTailscaleAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }

    private static bool IsPrivateLan(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }
}
