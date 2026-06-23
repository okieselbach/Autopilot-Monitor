using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Covers the pure logic behind the live-presence feature: UPN→RowKey hashing and the
/// per-process write throttle. The Azure Table read/write paths themselves are exercised via the
/// repository at integration time; here we lock the parts that have branches worth regressing.
/// </summary>
public class UserPresenceTests
{
    [Theory]
    [InlineData("User@Contoso.com", "user@contoso.com")]   // casing must not change the key
    [InlineData("plain.user@example.org", "PLAIN.USER@EXAMPLE.ORG")]
    public void PresenceRowKey_IsCaseInsensitive(string a, string b)
    {
        Assert.Equal(TableStorageService.PresenceRowKey(a), TableStorageService.PresenceRowKey(b));
    }

    [Fact]
    public void PresenceRowKey_IsDeterministicHexHash()
    {
        var key = TableStorageService.PresenceRowKey("alice@example.com");
        Assert.Equal(64, key.Length);                                      // SHA-256 hex
        Assert.Matches("^[0-9a-f]{64}$", key);                             // valid RowKey chars
        Assert.Equal(key, TableStorageService.PresenceRowKey("alice@example.com")); // stable
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void PresenceRowKey_NeverReturnsEmpty(string? input)
    {
        Assert.False(string.IsNullOrEmpty(TableStorageService.PresenceRowKey(input!)));
    }

    [Fact]
    public void PresenceRowKey_DistinctUpns_DoNotCollide()
    {
        // Regression: char-replacement mapped both of these to "a_b@x", letting one user overwrite
        // the other. A hash keeps them distinct.
        Assert.NotEqual(
            TableStorageService.PresenceRowKey("a/b@x"),
            TableStorageService.PresenceRowKey("a_b@x"));
    }

    [Fact]
    public void ShouldWrite_FirstCallWins_ImmediateRepeatThrottled()
    {
        // Unique identity so this test is isolated from the shared static throttle map.
        const string tenant = "11111111-1111-1111-1111-111111111111";
        var upn = $"throttle-{System.Guid.NewGuid():N}@example.com";

        Assert.True(UserPresenceMiddleware.ShouldWrite(tenant, upn));   // first → write
        Assert.False(UserPresenceMiddleware.ShouldWrite(tenant, upn));  // within window → throttled
    }

    [Fact]
    public void ShouldWrite_IsCaseInsensitiveOnUpn()
    {
        const string tenant = "22222222-2222-2222-2222-222222222222";
        var unique = System.Guid.NewGuid().ToString("N");
        var upnLower = $"case-{unique}@example.com";
        var upnUpper = $"CASE-{unique}@EXAMPLE.COM";

        Assert.True(UserPresenceMiddleware.ShouldWrite(tenant, upnLower));
        // Same user in different casing must hit the same throttle bucket.
        Assert.False(UserPresenceMiddleware.ShouldWrite(tenant, upnUpper));
    }
}
