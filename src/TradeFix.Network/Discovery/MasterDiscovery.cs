using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TradeFix.Network.Discovery;

public sealed record DiscoveredMaster(string Name, string Host, int Port);

/// <summary>
/// Agent-side "find my Master with zero typing": probes every plausible location and returns the
/// Masters that answered, by NAME — so the operator picks "DESKTOP-8RVANT9" from a list instead
/// of reading IPs off another screen.
///
/// Three probes run together, covering the three real deployments:
/// - UDP broadcast on the LAN (answered by the Master's DiscoveryBeacon) — same-network setups.
/// - The Tailscale peer list (via <c>tailscale status --json</c> when Tailscale is installed),
///   each peer probed on the control port's <c>/assets/discover</c> endpoint — the cross-network
///   setup this project actually runs (broadcast doesn't cross a tailnet).
/// - localhost — the Master-and-node-on-one-PC dev case.
/// Every probe is best-effort with a short timeout; a missing Tailscale or a firewalled UDP port
/// just means that probe contributes nothing.
/// </summary>
public static class MasterDiscovery
{
    public static async Task<IReadOnlyList<DiscoveredMaster>> FindAsync(int controlPort = 8791, TimeSpan? timeout = null)
    {
        var overall = timeout ?? TimeSpan.FromSeconds(3);
        using var cts = new CancellationTokenSource(overall);

        var results = new List<DiscoveredMaster>();
        var tasks = new List<Task<IReadOnlyList<DiscoveredMaster>>>
        {
            ProbeLanBroadcastAsync(cts.Token),
            ProbeTailscalePeersAsync(controlPort, cts.Token),
            // "localhost" (not 127.0.0.1): a localhost-bound HttpListener registers the literal
            // "localhost" host with http.sys and rejects other host headers; production servers
            // bind "+" and accept either.
            ProbeHostsAsync(["localhost"], controlPort, cts.Token),
        };

        foreach (var task in tasks)
        {
            try
            {
                results.AddRange(await task);
            }
            catch
            {
                // one probe path failing (no tailscale, UDP blocked) must not sink the others
            }
        }

        // Dedup by (name, port): the same Master often answers several probes under different
        // host spellings (localhost HTTP + loopback UDP + a LAN IP) — the operator should see it
        // once. Names are machine names, so a genuine collision is vanishingly unlikely.
        return results
            .GroupBy(m => $"{m.Name}|{m.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<DiscoveredMaster>> ProbeLanBroadcastAsync(CancellationToken cancellationToken)
    {
        var probe = Encoding.UTF8.GetBytes(DiscoveryProtocol.ProbeMessage);

        // One unbound socket (default route + loopback) PLUS one socket bound to each up IPv4
        // interface address. The unbound broadcast follows the DEFAULT route — and when a VPN is
        // active, the VPN owns the default route, so the probe disappears into the tunnel and
        // same-LAN discovery silently dies even though the LAN is fine (field report: "when I use
        // VPN the app doesn't work but they are on the same network"). Binding a sender to every
        // interface pushes the probe out of every NIC — Wi-Fi, ethernet, and tunnels alike — and
        // whichever one the Master actually lives on carries the reply back on that same socket,
        // with the Master's address as reachable via THAT interface.
        var sockets = new List<UdpClient>();
        try
        {
            try
            {
                var defaultSocket = new UdpClient { EnableBroadcast = true };
                sockets.Add(defaultSocket);
                await defaultSocket.SendAsync(probe, new IPEndPoint(IPAddress.Broadcast, DiscoveryProtocol.Port), cancellationToken);
                await defaultSocket.SendAsync(probe, new IPEndPoint(IPAddress.Loopback, DiscoveryProtocol.Port), cancellationToken);
            }
            catch
            {
                // even the default socket failing must not sink the per-interface probes
            }

            foreach (var (localAddress, subnetBroadcast) in EnumerateInterfaceBroadcastTargets())
            {
                try
                {
                    var socket = new UdpClient(new IPEndPoint(localAddress, 0)) { EnableBroadcast = true };
                    sockets.Add(socket);
                    await socket.SendAsync(probe, new IPEndPoint(IPAddress.Broadcast, DiscoveryProtocol.Port), cancellationToken);
                    if (subnetBroadcast is not null && !subnetBroadcast.Equals(IPAddress.Broadcast))
                    {
                        // Subnet-directed broadcast (e.g. 192.168.1.255) — some networks/drivers
                        // deliver it where the global 255.255.255.255 gets filtered.
                        await socket.SendAsync(probe, new IPEndPoint(subnetBroadcast, DiscoveryProtocol.Port), cancellationToken);
                    }
                }
                catch
                {
                    // an interface that refuses to bind or send simply doesn't participate
                }
            }

            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(TimeSpan.FromMilliseconds(1500));

            var found = new System.Collections.Concurrent.ConcurrentBag<DiscoveredMaster>();
            await Task.WhenAll(sockets.Select(async socket =>
            {
                try
                {
                    while (true)
                    {
                        var datagram = await socket.ReceiveAsync(window.Token);
                        if (DiscoveryProtocol.TryParseReply(Encoding.UTF8.GetString(datagram.Buffer), out var name, out var port))
                        {
                            found.Add(new DiscoveredMaster(name, datagram.RemoteEndPoint.Address.ToString(), port));
                        }
                    }
                }
                catch
                {
                    // listen window elapsed (or this socket faulted) — normal end of collection
                }
            }));

            return found.ToList();
        }
        finally
        {
            foreach (var socket in sockets)
            {
                socket.Dispose();
            }
        }
    }

    /// <summary>Every up, non-loopback IPv4 interface address on this PC, each with its
    /// subnet-directed broadcast address when the mask allows computing one. This is what makes
    /// discovery interface-aware instead of default-route-dependent.</summary>
    private static IReadOnlyList<(IPAddress LocalAddress, IPAddress? SubnetBroadcast)> EnumerateInterfaceBroadcastTargets()
    {
        var targets = new List<(IPAddress, IPAddress?)>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    IPAddress? subnetBroadcast = null;
                    try
                    {
                        var addressBytes = unicast.Address.GetAddressBytes();
                        var maskBytes = unicast.IPv4Mask.GetAddressBytes();
                        var broadcastBytes = new byte[4];
                        for (var i = 0; i < 4; i++)
                        {
                            broadcastBytes[i] = (byte)(addressBytes[i] | ~maskBytes[i]);
                        }

                        subnetBroadcast = new IPAddress(broadcastBytes);
                    }
                    catch
                    {
                        // no usable mask (e.g. a point-to-point tunnel) — global broadcast only
                    }

                    targets.Add((unicast.Address, subnetBroadcast));
                }
            }
        }
        catch
        {
            // interface enumeration unavailable — the default-route socket still probes
        }

        return targets;
    }

    private static async Task<IReadOnlyList<DiscoveredMaster>> ProbeTailscalePeersAsync(int controlPort, CancellationToken cancellationToken)
    {
        var exe = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            "tailscale.exe"
        }.FirstOrDefault(p => p == "tailscale.exe" || File.Exists(p));

        if (exe is null)
        {
            return [];
        }

        string json;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "status", "--json" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return [];
            }

            json = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            return []; // tailscale not runnable here — the other probes still apply
        }

        var hosts = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Peer", out var peers) && peers.ValueKind == JsonValueKind.Object)
            {
                foreach (var peer in peers.EnumerateObject())
                {
                    if (peer.Value.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
                    {
                        var v4 = ips.EnumerateArray()
                            .Select(ip => ip.GetString())
                            .FirstOrDefault(ip => ip is not null && !ip.Contains(':'));
                        if (v4 is not null)
                        {
                            hosts.Add(v4);
                        }
                    }
                }
            }
        }
        catch
        {
            return [];
        }

        return await ProbeHostsAsync(hosts, controlPort, cancellationToken);
    }

    /// <summary>Asks each candidate host's <c>/assets/discover</c> endpoint whether a TradeFix
    /// Master lives there. Short per-host timeout, all in parallel — a tailnet has few peers.</summary>
    private static async Task<IReadOnlyList<DiscoveredMaster>> ProbeHostsAsync(IReadOnlyList<string> hosts, int controlPort, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        var probes = hosts.Select(async host =>
        {
            try
            {
                var json = await http.GetStringAsync($"http://{host}:{controlPort}/assets/discover", cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("app", out var app) && app.GetString() == "tradefix-master"
                    && doc.RootElement.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } masterName)
                {
                    return new DiscoveredMaster(masterName, host, controlPort);
                }
            }
            catch
            {
                // not a Master / unreachable — that's what probing determines
            }

            return null;
        });

        return (await Task.WhenAll(probes)).Where(m => m is not null).Select(m => m!).ToList();
    }
}
