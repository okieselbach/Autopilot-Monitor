using AutopilotMonitor.Functions.Functions.Metrics;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Unit tests for the cross-tenant guard on <c>GET /api/metrics/mcp-usage/user/{userId}</c>.
///
/// Background: the route is catalog-policy <c>TenantAdminOrGA</c> with no <c>TenantScoping</c>
/// (the path parameter is an Azure AD object id, not a tenant id). Without this guard, a tenant
/// admin from tenant A could read MCP usage records of any oid — including oids in tenant B —
/// because the function blindly hands the oid to <c>IUserUsageRepository.GetUsageByUserAsync</c>.
/// </summary>
public class UsageCrossTenantGuardTests
{
    private const string TenantA = "00000000-0000-0000-0000-aaaaaaaaaaaa";
    private const string TenantB = "00000000-0000-0000-0000-bbbbbbbbbbbb";

    private static UserUsageRecord Rec(string tid, string endpoint = "ep") => new()
    {
        UserId = "oid-1",
        UserPrincipalName = "alice@contoso.com",
        TenantId = tid,
        Endpoint = endpoint,
        Date = "20260505",
        RequestCount = 1,
    };

    [Fact]
    public void OwnTenantOnly_NonGA_Allowed()
    {
        var records = new[] { Rec(TenantA), Rec(TenantA, "ep2") };

        var blocked = UsageCrossTenantGuard.IsForeignTenantAccess(records, TenantA, isGlobalAdmin: false);

        Assert.False(blocked);
    }

    [Fact]
    public void ForeignTenantOnly_NonGA_Blocked()
    {
        var records = new[] { Rec(TenantB), Rec(TenantB, "ep2") };

        var blocked = UsageCrossTenantGuard.IsForeignTenantAccess(records, TenantA, isGlobalAdmin: false);

        Assert.True(blocked);
    }

    [Fact]
    public void MixedTenants_NonGA_Blocked()
    {
        // Even one foreign record must trip the guard — a tenant admin must not see another tenant's row.
        var records = new[] { Rec(TenantA), Rec(TenantB) };

        var blocked = UsageCrossTenantGuard.IsForeignTenantAccess(records, TenantA, isGlobalAdmin: false);

        Assert.True(blocked);
    }

    [Fact]
    public void ForeignTenant_GlobalAdmin_Allowed()
    {
        // GA can see usage across all tenants.
        var records = new[] { Rec(TenantB) };

        var blocked = UsageCrossTenantGuard.IsForeignTenantAccess(records, TenantA, isGlobalAdmin: true);

        Assert.False(blocked);
    }

    [Fact]
    public void EmptyRecords_NonGA_Allowed()
    {
        // No records → caller learns nothing; safe to return 200 with empty list.
        var blocked = UsageCrossTenantGuard.IsForeignTenantAccess(
            Array.Empty<UserUsageRecord>(), TenantA, isGlobalAdmin: false);

        Assert.False(blocked);
    }

    [Fact]
    public void NullRecords_DoesNotThrow()
    {
        var blocked = UsageCrossTenantGuard.IsForeignTenantAccess(null!, TenantA, isGlobalAdmin: false);
        Assert.False(blocked);
    }

    [Fact]
    public void RecordWithEmptyTenantId_Ignored()
    {
        // Defensive: legacy records without TenantId must not be treated as a leak signal
        // (they could belong to anyone). Other records still drive the decision.
        var records = new[]
        {
            Rec(""),         // unknown tenant — ignored
            Rec(TenantA),    // own tenant
        };

        var blocked = UsageCrossTenantGuard.IsForeignTenantAccess(records, TenantA, isGlobalAdmin: false);

        Assert.False(blocked);
    }

    [Fact]
    public void CallerTenantIdEmpty_NotBlocked()
    {
        // Anonymous / device path scenarios shouldn't reach this code — but if they do,
        // we don't synthesize a 403 from missing claims; let the catalog policy handle auth.
        var records = new[] { Rec(TenantA) };

        var blocked = UsageCrossTenantGuard.IsForeignTenantAccess(records, "", isGlobalAdmin: false);

        Assert.False(blocked);
    }

    [Fact]
    public void TenantIdComparison_IsCaseInsensitive()
    {
        // Azure AD tids are GUIDs; some sources upper-case them. Comparison must not flag those as foreign.
        var records = new[] { Rec(TenantA.ToUpperInvariant()) };

        var blocked = UsageCrossTenantGuard.IsForeignTenantAccess(records, TenantA, isGlobalAdmin: false);

        Assert.False(blocked);
    }
}
