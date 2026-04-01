using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Prevents Server-Side Request Forgery (SSRF) by validating webhook URLs
    /// against private/reserved network ranges before allowing outbound requests.
    /// </summary>
    public static class SsrfGuard
    {
        /// <summary>
        /// Validates webhook URL format (sync). Call at config save time for immediate feedback.
        /// Returns null if valid, or an error message if invalid.
        /// Empty/null URLs are considered valid (means "no webhook configured").
        /// </summary>
        public static string? ValidateWebhookUrlFormat(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return "Webhook URL is not a valid absolute URL.";

            if (uri.Scheme != Uri.UriSchemeHttps)
                return "Webhook URL must use HTTPS.";

            if (uri.IsLoopback)
                return "Webhook URL must not target localhost.";

            if (IPAddress.TryParse(uri.Host, out _))
                return "Webhook URL must use a DNS hostname, not an IP address.";

            return null;
        }

        /// <summary>
        /// Resolves the webhook URL hostname via DNS and validates all resolved IPs
        /// against blocked ranges. Call before every outbound HTTP request.
        /// Throws <see cref="SsrfException"/> if the destination is blocked.
        /// Fails closed: DNS resolution errors are treated as blocked.
        /// </summary>
        public static async Task ValidateDestinationAsync(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                throw new SsrfException("Webhook URL is not a valid URL.");

            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new SsrfException("Webhook URL must use HTTPS.");

            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.Host);
            }
            catch (Exception)
            {
                throw new SsrfException("Could not resolve webhook hostname.");
            }

            if (addresses.Length == 0)
                throw new SsrfException("Webhook hostname resolved to no addresses.");

            foreach (var addr in addresses)
            {
                if (IsBlockedAddress(addr))
                    throw new SsrfException("Webhook URL targets a private or reserved network.");
            }
        }

        /// <summary>
        /// Returns true if the IP address is private, loopback, link-local, multicast,
        /// cloud metadata, reserved, or an IPv6-mapped version of any of the above.
        /// </summary>
        internal static bool IsBlockedAddress(IPAddress addr)
        {
            // Normalize IPv6-mapped IPv4 (e.g. ::ffff:10.0.0.1 -> 10.0.0.1)
            if (addr.IsIPv4MappedToIPv6)
                addr = addr.MapToIPv4();

            // Loopback: 127.0.0.0/8, ::1
            if (IPAddress.IsLoopback(addr))
                return true;

            // IPv6 link-local (fe80::/10)
            if (addr.IsIPv6LinkLocal)
                return true;

            // IPv6 multicast (ff00::/8)
            if (addr.IsIPv6Multicast)
                return true;

            if (addr.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = addr.GetAddressBytes();

                // 10.0.0.0/8 (RFC 1918)
                if (bytes[0] == 10)
                    return true;

                // 172.16.0.0/12 (RFC 1918)
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    return true;

                // 192.168.0.0/16 (RFC 1918)
                if (bytes[0] == 192 && bytes[1] == 168)
                    return true;

                // 169.254.0.0/16 (link-local, includes Azure IMDS 169.254.169.254)
                if (bytes[0] == 169 && bytes[1] == 254)
                    return true;

                // 0.0.0.0/8 (current network)
                if (bytes[0] == 0)
                    return true;

                // 100.64.0.0/10 (Carrier-grade NAT, RFC 6598)
                if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                    return true;

                // 192.0.0.0/24 (IETF Protocol Assignments)
                if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
                    return true;

                // 192.0.2.0/24 (TEST-NET-1)
                if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
                    return true;

                // 198.18.0.0/15 (Benchmark testing)
                if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
                    return true;

                // 198.51.100.0/24 (TEST-NET-2)
                if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                    return true;

                // 203.0.113.0/24 (TEST-NET-3)
                if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                    return true;

                // 224.0.0.0/4 (Multicast) and 240.0.0.0/4 (Reserved)
                if (bytes[0] >= 224)
                    return true;
            }

            if (addr.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var bytes = addr.GetAddressBytes();

                // fc00::/7 (Unique local address)
                if ((bytes[0] & 0xFE) == 0xFC)
                    return true;

                // :: (unspecified)
                if (addr.Equals(IPAddress.IPv6None) || addr.Equals(IPAddress.IPv6Any))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Thrown when a webhook URL targets a blocked (private/reserved) network destination.
    /// </summary>
    public class SsrfException : Exception
    {
        public SsrfException(string message) : base(message) { }
    }
}
