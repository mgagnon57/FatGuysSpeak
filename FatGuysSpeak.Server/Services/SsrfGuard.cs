using System.Net;
using System.Net.Sockets;

namespace FatGuysSpeak.Server.Services;

/// <summary>
/// Produces an HttpMessageHandler that validates the real destination IP of every
/// physical connection — the initial request, each redirect hop, and any DNS
/// re-resolution. Because the socket connects to the exact address that was just
/// validated, this closes both the DNS-rebinding (TOCTOU) and redirect-to-internal
/// SSRF bypasses that a one-time pre-fetch hostname check leaves open.
/// </summary>
public static class SsrfGuard
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;

            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            var target = addresses.FirstOrDefault(a => !IsPrivateOrLoopback(a));
            if (target is null)
                throw new IOException("Blocked: host resolves only to private or loopback addresses.");

            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(target, port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    public static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.Equals(IPAddress.IPv6Loopback);
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // CGNAT
            || b[0] == 127;
    }
}
