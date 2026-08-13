using System.Net;
using System.Net.Sockets;
using System.Text;
using TradeFix.Common.Logging;

namespace TradeFix.Network.Discovery;

/// <summary>
/// Master-side LAN discovery responder: listens for <see cref="DiscoveryProtocol.ProbeMessage"/>
/// UDP datagrams on <see cref="DiscoveryProtocol.Port"/> and answers with this Master's name and
/// control port — so an Agent on the same network can find the Master with zero typing (the
/// "everyone can use it with ease" requirement: operators should never read or enter an IP).
/// Best-effort by design: UDP broadcast doesn't cross networks (Tailscale peers are probed a
/// different way — see MasterDiscovery) and a firewall may eat it; the connect-code flow remains
/// as the manual fallback.
/// </summary>
public sealed class DiscoveryBeacon : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();

    public DiscoveryBeacon(string masterName, int controlPort, ILogSink? log = null)
    {
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.Port));

        _ = Task.Run(async () =>
        {
            var reply = Encoding.UTF8.GetBytes(DiscoveryProtocol.FormatReply(masterName, controlPort));
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var datagram = await _udp.ReceiveAsync(_cts.Token);
                    var text = Encoding.UTF8.GetString(datagram.Buffer);
                    if (text == DiscoveryProtocol.ProbeMessage)
                    {
                        await _udp.SendAsync(reply, datagram.RemoteEndPoint, _cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A malformed datagram or transient socket error must not kill the responder.
                    log?.Write(new LogEntry(DateTimeOffset.UtcNow, LogCategory.Network, "DiscoveryBeacon", "Probe handling failed", ex.ToString()));
                }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
    }
}
