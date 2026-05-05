using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Functions.Metrics;

/// <summary>
/// Cross-tenant access guard for <c>GET /api/metrics/mcp-usage/user/{userId}</c>.
///
/// The route accepts an Azure AD object id (oid), which has no inherent tenant scoping —
/// middleware can't validate it the way it does for <c>{tenantId}</c> routes. The function
/// therefore inspects the returned <see cref="UserUsageRecord"/>s and blocks the response
/// if any record belongs to a tenant other than the caller's, unless the caller is a Global Admin.
/// </summary>
public static class UsageCrossTenantGuard
{
    /// <summary>
    /// Returns true if the records contain at least one entry whose tenant differs from
    /// <paramref name="callerTenantId"/> AND the caller is not a Global Admin.
    /// Empty record sets and GA callers always pass (return false).
    /// </summary>
    public static bool IsForeignTenantAccess(
        IEnumerable<UserUsageRecord> records,
        string callerTenantId,
        bool isGlobalAdmin)
    {
        if (isGlobalAdmin) return false;
        if (records == null) return false;
        if (string.IsNullOrEmpty(callerTenantId)) return false;

        foreach (var r in records)
        {
            if (string.IsNullOrEmpty(r.TenantId)) continue;
            if (!string.Equals(r.TenantId, callerTenantId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
