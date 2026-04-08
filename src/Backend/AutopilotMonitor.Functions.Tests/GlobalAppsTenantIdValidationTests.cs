using AutopilotMonitor.Functions.Functions.Apps;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Unit tests for the shared <c>?tenantId=</c> query-param validator used by
/// the three <c>global/apps/*</c> Global-Admin endpoints
/// (<see cref="GetGlobalAppsListFunction"/>, <see cref="GetGlobalAppAnalyticsFunction"/>,
/// <see cref="GetGlobalAppSessionsFunction"/>).
///
/// A malformed tenantId previously reached the table-storage query layer as a
/// raw string; this test ensures the guard short-circuits with a predictable
/// false (→ 400 BadRequest in the caller) for anything that isn't a GUID.
/// </summary>
public class GlobalAppsTenantIdValidationTests
{
    [Theory]
    [InlineData(null)]                                       // not supplied → aggregate all tenants
    [InlineData("")]                                         // empty string → same
    [InlineData("00000000-0000-0000-0000-000000000000")]     // empty GUID is still valid-format
    [InlineData("11111111-2222-3333-4444-555555555555")]     // normal GUID
    [InlineData("{11111111-2222-3333-4444-555555555555}")]   // braced GUID (Guid.TryParse accepts)
    public void Accepts_NullEmpty_And_ValidGuids(string? raw)
    {
        Assert.True(AppsAnalyticsHelper.IsValidOptionalTenantIdQueryParam(raw));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("notaguid")]
    [InlineData("11111111-2222-3333-4444")]                  // truncated
    [InlineData("11111111-2222-3333-4444-5555555555555")]    // too long
    [InlineData("'; DROP TABLE devices; --")]                // injection attempt
    [InlineData("../admin")]
    public void Rejects_MalformedValues(string raw)
    {
        Assert.False(AppsAnalyticsHelper.IsValidOptionalTenantIdQueryParam(raw));
    }
}
