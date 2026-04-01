using System.Net;
using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

public class SsrfGuardTests
{
    // ── ValidateWebhookUrlFormat: valid URLs ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ValidateFormat_NullOrEmpty_ReturnsNull(string? url)
    {
        Assert.Null(SsrfGuard.ValidateWebhookUrlFormat(url));
    }

    [Theory]
    [InlineData("https://hooks.slack.com/services/T00/B00/xxx")]
    [InlineData("https://contoso.webhook.office.com/webhookb2/abc")]
    [InlineData("https://prod-12.westeurope.logic.azure.com:443/workflows/abc")]
    public void ValidateFormat_ValidHttpsUrls_ReturnsNull(string url)
    {
        Assert.Null(SsrfGuard.ValidateWebhookUrlFormat(url));
    }

    // ── ValidateWebhookUrlFormat: invalid URLs ──

    [Fact]
    public void ValidateFormat_HttpScheme_ReturnsError()
    {
        var result = SsrfGuard.ValidateWebhookUrlFormat("http://hooks.slack.com/services/T00/B00/xxx");
        Assert.Contains("HTTPS", result);
    }

    [Fact]
    public void ValidateFormat_FtpScheme_ReturnsError()
    {
        var result = SsrfGuard.ValidateWebhookUrlFormat("ftp://example.com/webhook");
        Assert.Contains("HTTPS", result);
    }

    [Fact]
    public void ValidateFormat_NotAUrl_ReturnsError()
    {
        var result = SsrfGuard.ValidateWebhookUrlFormat("not-a-url");
        Assert.Contains("valid absolute URL", result);
    }

    [Fact]
    public void ValidateFormat_Localhost_ReturnsError()
    {
        var result = SsrfGuard.ValidateWebhookUrlFormat("https://localhost/webhook");
        Assert.Contains("localhost", result);
    }

    [Fact]
    public void ValidateFormat_LoopbackIpv6_ReturnsError()
    {
        var result = SsrfGuard.ValidateWebhookUrlFormat("https://[::1]/webhook");
        Assert.Contains("localhost", result);
    }

    [Theory]
    [InlineData("https://10.0.0.1/webhook")]
    [InlineData("https://192.168.1.1/webhook")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://172.16.0.1/webhook")]
    public void ValidateFormat_IpAddress_ReturnsError(string url)
    {
        var result = SsrfGuard.ValidateWebhookUrlFormat(url);
        Assert.Contains("DNS hostname", result);
    }

    // ── IsBlockedAddress: blocked IPv4 ranges ──

    [Theory]
    [InlineData("127.0.0.1")]       // loopback
    [InlineData("127.0.0.2")]       // loopback range
    [InlineData("10.0.0.1")]        // RFC 1918 Class A
    [InlineData("10.255.255.255")]   // RFC 1918 Class A end
    [InlineData("172.16.0.1")]      // RFC 1918 Class B start
    [InlineData("172.31.255.255")]   // RFC 1918 Class B end
    [InlineData("192.168.0.1")]     // RFC 1918 Class C
    [InlineData("192.168.255.255")] // RFC 1918 Class C end
    [InlineData("169.254.169.254")] // Azure IMDS
    [InlineData("169.254.0.1")]     // Link-local
    [InlineData("0.0.0.0")]         // Current network
    [InlineData("0.255.255.255")]   // Current network end
    [InlineData("100.64.0.1")]      // CGNAT
    [InlineData("100.127.255.255")] // CGNAT end
    [InlineData("192.0.0.1")]       // IETF Protocol Assignments
    [InlineData("192.0.2.1")]       // TEST-NET-1
    [InlineData("198.18.0.1")]      // Benchmark
    [InlineData("198.19.255.255")]  // Benchmark end
    [InlineData("198.51.100.1")]    // TEST-NET-2
    [InlineData("203.0.113.1")]     // TEST-NET-3
    [InlineData("224.0.0.1")]       // Multicast
    [InlineData("240.0.0.1")]       // Reserved
    [InlineData("255.255.255.255")] // Broadcast
    public void IsBlocked_PrivateIpv4_ReturnsTrue(string ip)
    {
        Assert.True(SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    // ── IsBlockedAddress: blocked IPv6 ──

    [Theory]
    [InlineData("::1")]             // loopback
    [InlineData("fe80::1")]         // link-local
    [InlineData("fc00::1")]         // unique local
    [InlineData("fd00::1")]         // unique local
    [InlineData("ff02::1")]         // multicast
    public void IsBlocked_PrivateIpv6_ReturnsTrue(string ip)
    {
        Assert.True(SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    // ── IsBlockedAddress: IPv6-mapped IPv4 ──

    [Theory]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:192.168.1.1")]
    public void IsBlocked_Ipv6MappedPrivateIpv4_ReturnsTrue(string ip)
    {
        Assert.True(SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    // ── IsBlockedAddress: allowed public IPs ──

    [Theory]
    [InlineData("8.8.8.8")]         // Google DNS
    [InlineData("1.1.1.1")]         // Cloudflare
    [InlineData("52.114.77.33")]    // Azure public (Teams range)
    [InlineData("104.18.6.192")]    // Cloudflare (Slack range)
    [InlineData("172.15.255.255")]  // Just below RFC 1918 172.16/12
    [InlineData("172.32.0.0")]      // Just above RFC 1918 172.16/12
    [InlineData("100.63.255.255")]  // Just below CGNAT
    [InlineData("100.128.0.0")]     // Just above CGNAT
    public void IsBlocked_PublicIp_ReturnsFalse(string ip)
    {
        Assert.False(SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip)));
    }

    // ── ValidateDestinationAsync: integration ──

    [Fact]
    public async Task ValidateDestination_Localhost_ThrowsSsrfException()
    {
        var ex = await Assert.ThrowsAsync<SsrfException>(
            () => SsrfGuard.ValidateDestinationAsync("https://localhost/webhook"));
        Assert.Contains("private or reserved", ex.Message);
    }

    [Fact]
    public async Task ValidateDestination_HttpScheme_ThrowsSsrfException()
    {
        var ex = await Assert.ThrowsAsync<SsrfException>(
            () => SsrfGuard.ValidateDestinationAsync("http://example.com/webhook"));
        Assert.Contains("HTTPS", ex.Message);
    }

    [Fact]
    public async Task ValidateDestination_InvalidUrl_ThrowsSsrfException()
    {
        await Assert.ThrowsAsync<SsrfException>(
            () => SsrfGuard.ValidateDestinationAsync("not-a-url"));
    }

    [Fact]
    public async Task ValidateDestination_UnresolvableHost_ThrowsSsrfException()
    {
        var ex = await Assert.ThrowsAsync<SsrfException>(
            () => SsrfGuard.ValidateDestinationAsync("https://this-host-does-not-exist-xyz123.invalid/webhook"));
        Assert.Contains("resolve", ex.Message);
    }
}
