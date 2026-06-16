using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace FatGuysSpeak.Server.Services;

public class WebhookDeliveryService(IHttpClientFactory httpFactory, ILogger<WebhookDeliveryService> logger)
{
    public async Task DeliverAsync(string url, string eventName, object payload)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            var addresses = await Dns.GetHostAddressesAsync(uri.Host);
            if (addresses.Length == 0 || addresses.Any(IsPrivateOrLoopback)) return;

            var http = httpFactory.CreateClient("webhook");
            var body = new { @event = eventName, data = payload, timestamp = DateTime.UtcNow };
            var resp = await http.PostAsJsonAsync(url, body);
            if (!resp.IsSuccessStatusCode)
                logger.LogWarning("Webhook {Url} returned {Status} for event {Event}", url, (int)resp.StatusCode, eventName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Webhook delivery failed for {Url}", url);
        }
    }

    internal static bool IsPrivateOrLoopback(IPAddress ip)
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
