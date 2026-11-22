using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Daikin2Mqtt.Daikin;

/// <summary>A unit that answered the network scan.</summary>
public sealed record DaikinScanResult(string Host, string? Name, string? Mac, string? Firmware);

public interface IDaikinScanner
{
    Task<IReadOnlyList<DaikinScanResult>> ScanAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Finds the units of the LAN: a UDP broadcast on port 30050 with the payload <c>DAIKIN_UDP/common/basic_info</c>; every
/// module answers with its <c>basic_info</c>. Broadcasts do not cross a Docker bridge network: from a container, use
/// <c>network_mode: host</c> or declare the units by IP address.
/// </summary>
public sealed class DaikinScanner : IDaikinScanner
{
    public const int DiscoveryPort = 30050;
    public const string DiscoveryPayload = "DAIKIN_UDP/common/basic_info";

    private readonly int _port;
    private readonly Func<IReadOnlyList<IPAddress>> _targets;

    /// <param name="port">UDP port of the modules (30050); tests use another one.</param>
    /// <param name="targets">Addresses to send the probe to; default: the broadcast address of every network interface.</param>
    public DaikinScanner(int port = DiscoveryPort, Func<IReadOnlyList<IPAddress>>? targets = null)
    {
        _port = port;
        _targets = targets ?? BroadcastAddresses;
    }

    public async Task<IReadOnlyList<DaikinScanResult>> ScanAsync(TimeSpan timeout, CancellationToken ct)
    {
        var found = new Dictionary<string, DaikinScanResult>(StringComparer.OrdinalIgnoreCase);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var probe = Encoding.ASCII.GetBytes(DiscoveryPayload);

        foreach (var target in _targets())
        {
            try
            {
                await udp.SendAsync(probe, probe.Length, new IPEndPoint(target, _port)).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // an interface that cannot broadcast (VPN, down...) is skipped
            }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                UdpReceiveResult reply;
                try
                {
                    reply = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
                {
                    // Windows reports the ICMP "port unreachable" of a probe sent to an address with no module here: not an error.
                    continue;
                }

                var response = DaikinResponseParser.Parse(Encoding.UTF8.GetString(reply.Buffer));
                if (!response.IsOk)
                {
                    continue;
                }

                var host = reply.RemoteEndPoint.Address.ToString();
                found[host] = new DaikinScanResult(
                    host, response.Values.GetValueOrDefault("name"), response.Values.GetValueOrDefault("mac"), response.Values.GetValueOrDefault("ver"));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // end of the listening window
        }

        return found.Values.OrderBy(r => r.Host, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>255.255.255.255 plus the directed broadcast address of every IPv4 network the machine is on.</summary>
    public static IReadOnlyList<IPAddress> BroadcastAddresses()
    {
        var addresses = new List<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                {
                    continue;
                }

                var ip = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var broadcast = new IPAddress(ip.Zip(mask, (a, m) => (byte)(a | ~m)).ToArray());
                if (!addresses.Contains(broadcast))
                {
                    addresses.Add(broadcast);
                }
            }
        }

        return addresses;
    }
}
