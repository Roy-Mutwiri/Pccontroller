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
        var found = new List<DiscoveredMaster>();
        using var udp = new UdpClient { EnableBroadcast = true };
        var probe = Encoding.UTF8.GetBytes(DiscoveryProtocol.ProbeMessage);
        await udp.SendAsync(probe, new IPEndPoint(IPAddress.Broadcast, DiscoveryProtocol.Port), cancellationToken);
        await udp.SendAsync(probe, new IPEndPoint(IPAddress.Loopback, DiscoveryProtocol.Port), cancellationToken);

        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try
        {
            while (true)
            {
                var datagram = await udp.ReceiveAsync(window.Token);
                if (DiscoveryProtocol.TryParseReply(Encoding.UTF8.GetString(datagram.Buffer), out var name, out var port))
                {
                    found.Add(new DiscoveredMaster(name, datagram.RemoteEndPoint.Address.ToString(), port));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // listen window elapsed — normal end of collection
        }

        return found;
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
