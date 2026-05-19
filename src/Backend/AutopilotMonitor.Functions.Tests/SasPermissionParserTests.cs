using AutopilotMonitor.Functions.Services.Diagnostics;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pinpoints the §5b cascade-delete decision: a customer's SAS is only allowed to
/// have its diagnostics blob deleted by the cascade when its <c>sp=</c> permission
/// string includes <c>d</c>. Misclassifying here either (a) silently leaks an
/// orphan blob into the customer's container or (b) attempts a DELETE that 403s
/// and poisons the cascade — both are blast-radius outcomes worth pinning.
/// </summary>
public class SasPermissionParserTests
{
    [Theory]
    [InlineData("https://acct.blob/container?sv=2024&sp=rwd&sig=x", true)]
    [InlineData("https://acct.blob/container?sv=2024&sp=rwdl&sig=x", true)]
    [InlineData("https://acct.blob/container?sv=2024&sp=racwdli&sig=x", true)]
    [InlineData("https://acct.blob/container?sv=2024&sp=D&sig=x", true)]    // uppercase
    [InlineData("https://acct.blob/container?sp=d&sig=x", true)]            // d alone
    public void HasDelete_ReturnsTrue_WhenSpContainsD(string sasUrl, bool expected)
    {
        Assert.Equal(expected, SasPermissionParser.HasDelete(sasUrl));
    }

    [Theory]
    [InlineData("https://acct.blob/container?sv=2024&sp=rwc&sig=x")]        // read+write+create only
    [InlineData("https://acct.blob/container?sv=2024&sp=rw&sig=x")]         // read+write only
    [InlineData("https://acct.blob/container?sv=2024&sp=&sig=x")]           // empty sp
    [InlineData("https://acct.blob/container?sv=2024&sig=x")]               // no sp at all
    [InlineData("https://acct.blob/container")]                              // no query string
    [InlineData("")]                                                          // empty input
    [InlineData(null)]                                                        // null input
    public void HasDelete_ReturnsFalse_WhenSpLacksD(string? sasUrl)
    {
        Assert.False(SasPermissionParser.HasDelete(sasUrl));
    }

    [Fact]
    public void HasPermission_IsCaseInsensitive_ForBothInputAndFlag()
    {
        // Both sides of the comparison are lowercased before matching.
        Assert.True(SasPermissionParser.HasPermission("https://x?sp=RWDLA&sig=y", 'l'));
        Assert.True(SasPermissionParser.HasPermission("https://x?sp=rwdla&sig=y", 'L'));
        Assert.False(SasPermissionParser.HasPermission("https://x?sp=RWC&sig=y", 'd'));
    }

    [Fact]
    public void HasPermission_ToleratesMalformedQueryStrings()
    {
        // Designed never to throw — the caller's "skip-delete" branch is the safe
        // fallback under uncertainty rather than blowing up the cascade.
        Assert.False(SasPermissionParser.HasPermission("not-a-url-at-all", 'd'));
        Assert.False(SasPermissionParser.HasPermission("https://no-query", 'd'));
        Assert.False(SasPermissionParser.HasPermission("https://x?sp=%%%&sig=y", 'd'));
    }
}
