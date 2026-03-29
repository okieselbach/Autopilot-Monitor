using System.Text.RegularExpressions;

namespace AutopilotMonitor.Functions.Security;

/// <summary>
/// Authorization policy tiers for endpoint access control.
/// Ordered from least restrictive to most restrictive.
/// </summary>
public enum EndpointPolicy
{
    /// <summary>No authentication required. Fully public.</summary>
    PublicAnonymous,

    /// <summary>Device certificate or bootstrap token auth. No JWT.</summary>
    DeviceOrBootstrapAuth,

    /// <summary>Valid JWT token required. Any authenticated user.</summary>
    AuthenticatedUser,

    /// <summary>Tenant member with Admin, Operator, or Viewer role. Tenant-scoped read access.</summary>
    MemberRead,

    /// <summary>Tenant Admin or Global Admin. Tenant-scoped write access.</summary>
    TenantAdminOrGA,

    /// <summary>Admin (always) or Operator with CanManageBootstrapTokens permission, or Global Admin.</summary>
    BootstrapManagerOrGA,

    /// <summary>Global Admin only. Platform-wide access.</summary>
    GlobalAdminOnly,
}

/// <summary>
/// A single entry in the endpoint access policy catalog.
/// </summary>
public sealed class EndpointPolicyEntry
{
    public string HttpMethod { get; }
    public string RouteTemplate { get; }
    public EndpointPolicy Policy { get; }

    // Pre-compiled regex for matching actual request paths against the route template
    internal Regex RouteRegex { get; }

    public EndpointPolicyEntry(string httpMethod, string routeTemplate, EndpointPolicy policy)
    {
        HttpMethod = httpMethod.ToUpperInvariant();
        RouteTemplate = routeTemplate;
        Policy = policy;
        RouteRegex = BuildRouteRegex(routeTemplate);
    }

    /// <summary>
    /// Converts a route template like "sessions/{sessionId}/events" into a regex
    /// that matches actual paths like "sessions/abc-123/events".
    /// </summary>
    private static Regex BuildRouteRegex(string routeTemplate)
    {
        // Escape regex special chars, then replace {param} placeholders with [^/]+
        // Note: Regex.Escape escapes { to \{ but does NOT escape } — so pattern matches \{...}
        var escaped = Regex.Escape(routeTemplate);
        var pattern = Regex.Replace(escaped, @"\\\{[^}]+}", "[^/]+");
        return new Regex($"^{pattern}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}

/// <summary>
/// Single source of truth for endpoint authorization policies.
/// Every HTTP route must be registered here. Unregistered routes fail closed.
/// </summary>
public static class EndpointAccessPolicyCatalog
{
    private static readonly EndpointPolicyEntry[] _entries =
    {
        // ── PublicAnonymous ─────────────────────────────────────────────
        new("GET",    "health",                    EndpointPolicy.PublicAnonymous),
        new("GET",    "stats/platform",            EndpointPolicy.PublicAnonymous),
        new("GET",    "bootstrap/validate/{code}", EndpointPolicy.PublicAnonymous),

        // ── DeviceOrBootstrapAuth ───────────────────────────────────────
        new("POST",   "agent/register-session",    EndpointPolicy.DeviceOrBootstrapAuth),
        new("POST",   "agent/ingest",              EndpointPolicy.DeviceOrBootstrapAuth),
        new("GET",    "agent/config",              EndpointPolicy.DeviceOrBootstrapAuth),
        new("POST",   "agent/upload-url",          EndpointPolicy.DeviceOrBootstrapAuth),
        new("POST",   "agent/error",               EndpointPolicy.DeviceOrBootstrapAuth),
        new("POST",   "bootstrap/register-session", EndpointPolicy.DeviceOrBootstrapAuth),
        new("POST",   "bootstrap/ingest",          EndpointPolicy.DeviceOrBootstrapAuth),
        new("GET",    "bootstrap/config",          EndpointPolicy.DeviceOrBootstrapAuth),
        new("POST",   "bootstrap/error",           EndpointPolicy.DeviceOrBootstrapAuth),

        // ── AuthenticatedUser ───────────────────────────────────────────
        new("GET",    "auth/me",                   EndpointPolicy.AuthenticatedUser),
        new("GET",    "auth/is-global-admin",      EndpointPolicy.AuthenticatedUser),
        new("POST",   "realtime/negotiate",        EndpointPolicy.AuthenticatedUser),
        new("POST",   "realtime/groups/join",      EndpointPolicy.MemberRead),
        new("POST",   "realtime/groups/leave",     EndpointPolicy.MemberRead),
        new("GET",    "progress/sessions",         EndpointPolicy.AuthenticatedUser),
        new("GET",    "progress/sessions/{sessionId}/events", EndpointPolicy.AuthenticatedUser),
        new("PUT",    "preview/notification-email", EndpointPolicy.AuthenticatedUser),
        new("GET",    "feedback/status",           EndpointPolicy.AuthenticatedUser),
        new("POST",   "feedback",                  EndpointPolicy.AuthenticatedUser),

        // ── MemberRead (Admin + Operator, later + Viewer) ───────────────
        new("GET",    "raw/sessions",                        EndpointPolicy.MemberRead),
        new("GET",    "raw/events",                          EndpointPolicy.MemberRead),
        new("GET",    "search/quick",                   EndpointPolicy.MemberRead),
        new("GET",    "search/sessions",                EndpointPolicy.MemberRead),
        new("GET",    "search/sessions-by-event",       EndpointPolicy.MemberRead),
        new("GET",    "search/sessions-by-cve",         EndpointPolicy.MemberRead),
        new("GET",    "metrics/summary",              EndpointPolicy.MemberRead),
        new("GET",    "sessions",                  EndpointPolicy.MemberRead),
        new("GET",    "sessions/{sessionId}",      EndpointPolicy.MemberRead),
        new("GET",    "sessions/{sessionId}/events", EndpointPolicy.MemberRead),
        new("GET",    "sessions/{sessionId}/analysis", EndpointPolicy.MemberRead),
        new("GET",    "sessions/{sessionId}/vulnerability-report", EndpointPolicy.MemberRead),
        new("GET",    "metrics/app",               EndpointPolicy.MemberRead),
        new("GET",    "metrics/usage",             EndpointPolicy.MemberRead),
        new("GET",    "metrics/geographic",        EndpointPolicy.MemberRead),
        new("GET",    "metrics/mcp-usage/{userId}", EndpointPolicy.TenantAdminOrGA),
        new("GET",    "metrics/geographic/sessions", EndpointPolicy.MemberRead),
        new("GET",    "audit/logs",                EndpointPolicy.MemberRead),
        new("GET",    "diagnostics/download-url",  EndpointPolicy.MemberRead),
        new("GET",    "rules/gather",              EndpointPolicy.MemberRead),
        new("GET",    "rules/analyze",             EndpointPolicy.MemberRead),
        new("GET",    "rules/ime-log-patterns",    EndpointPolicy.MemberRead),
        new("GET",    "config/{tenantId}",         EndpointPolicy.MemberRead),

        // ── TenantAdminOrGA ─────────────────────────────────────────────
        new("PUT",    "config/{tenantId}",         EndpointPolicy.TenantAdminOrGA),
        new("POST",   "config/{tenantId}",         EndpointPolicy.TenantAdminOrGA),
        new("POST",   "rules/gather",              EndpointPolicy.TenantAdminOrGA),
        new("PUT",    "rules/gather/{ruleId}",     EndpointPolicy.TenantAdminOrGA),
        new("DELETE", "rules/gather/{ruleId}",     EndpointPolicy.TenantAdminOrGA),
        new("POST",   "rules/analyze",             EndpointPolicy.TenantAdminOrGA),
        new("PUT",    "rules/analyze/{ruleId}",    EndpointPolicy.TenantAdminOrGA),
        new("DELETE", "rules/analyze/{ruleId}",    EndpointPolicy.TenantAdminOrGA),
        new("PUT",    "rules/ime-log-patterns/{patternId}", EndpointPolicy.TenantAdminOrGA),
        new("POST",   "sessions/{sessionId}/mark-failed",     EndpointPolicy.TenantAdminOrGA),
        new("POST",   "sessions/{sessionId}/mark-succeeded", EndpointPolicy.TenantAdminOrGA),
        new("POST",   "sessions/{sessionId}/report",        EndpointPolicy.TenantAdminOrGA),
        new("DELETE", "sessions/{sessionId}",      EndpointPolicy.TenantAdminOrGA),
        new("GET",    "tenants/{tenantId}/admins",           EndpointPolicy.TenantAdminOrGA),
        new("POST",   "tenants/{tenantId}/admins",           EndpointPolicy.TenantAdminOrGA),
        new("DELETE", "tenants/{tenantId}/admins/{adminUpn}", EndpointPolicy.TenantAdminOrGA),
        new("PATCH",  "tenants/{tenantId}/admins/{adminUpn}/disable",     EndpointPolicy.TenantAdminOrGA),
        new("PATCH",  "tenants/{tenantId}/admins/{adminUpn}/enable",      EndpointPolicy.TenantAdminOrGA),
        new("PATCH",  "tenants/{tenantId}/admins/{adminUpn}/permissions", EndpointPolicy.TenantAdminOrGA),
        new("DELETE", "tenants/{tenantId}/offboard", EndpointPolicy.TenantAdminOrGA),
        new("GET",    "config/{tenantId}/autopilot-device-validation/consent-url",    EndpointPolicy.TenantAdminOrGA),
        new("GET",    "config/{tenantId}/autopilot-device-validation/consent-status",  EndpointPolicy.TenantAdminOrGA),
        new("POST",   "config/{tenantId}/test-notification",                           EndpointPolicy.TenantAdminOrGA),

        // ── BootstrapManagerOrGA ────────────────────────────────────────
        new("GET",    "bootstrap/sessions",        EndpointPolicy.BootstrapManagerOrGA),
        new("POST",   "bootstrap/sessions",        EndpointPolicy.BootstrapManagerOrGA),
        new("DELETE", "bootstrap/sessions/{code}", EndpointPolicy.BootstrapManagerOrGA),

        // ── MCP Access Check (any authenticated user can check their own access) ──
        new("GET",    "auth/mcp",                              EndpointPolicy.AuthenticatedUser),

        // ── MCP Usage (self-service) ──────────────────────────────────
        new("GET",    "metrics/mcp-usage/me",                  EndpointPolicy.AuthenticatedUser),

        // ── GlobalAdminOnly ────────────────────────────────────────────
        new("GET",    "global/raw/sessions",                  EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/raw/events",                    EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/raw/tables",                    EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/raw/tables/{tableName}",        EndpointPolicy.GlobalAdminOnly),
        new("POST",   "global/raw/logs",                      EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/search/sessions",              EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/search/sessions-by-event",   EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/search/sessions-by-cve",     EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/summary",           EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/config",             EndpointPolicy.GlobalAdminOnly),
        new("PUT",    "global/config",             EndpointPolicy.GlobalAdminOnly),
        new("POST",   "global/config",             EndpointPolicy.GlobalAdminOnly),
        new("GET",    "config/all",                EndpointPolicy.GlobalAdminOnly),
        new("GET",    "auth/global-admins",        EndpointPolicy.GlobalAdminOnly),
        new("POST",   "auth/global-admins",        EndpointPolicy.GlobalAdminOnly),
        new("DELETE", "auth/global-admins/{upn}",  EndpointPolicy.GlobalAdminOnly),
        new("GET",    "preview/whitelist",          EndpointPolicy.GlobalAdminOnly),
        new("POST",   "preview/whitelist/{tenantId}", EndpointPolicy.GlobalAdminOnly),
        new("DELETE", "preview/whitelist/{tenantId}", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "preview/notification-email/{tenantId}", EndpointPolicy.GlobalAdminOnly),
        new("POST",   "preview/send-welcome-email/{tenantId}", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/sessions",            EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/audit/logs",          EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/platform",    EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/app",         EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/geographic",  EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/geographic/sessions", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/usage",       EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/mcp-usage",       EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/mcp-usage/daily", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/session-reports",     EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/session-reports/download-url", EndpointPolicy.GlobalAdminOnly),
        new("PATCH",  "global/session-reports/{reportId}/note", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/rules/gather",        EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/rules/analyze",       EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/devices/blocked",     EndpointPolicy.GlobalAdminOnly),
        new("GET",    "devices/blocked",            EndpointPolicy.GlobalAdminOnly),
        new("POST",   "devices/block",              EndpointPolicy.GlobalAdminOnly),
        new("DELETE", "devices/block/{encodedSerialNumber}", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "versions/blocked",           EndpointPolicy.GlobalAdminOnly),
        new("POST",   "versions/block",             EndpointPolicy.GlobalAdminOnly),
        new("DELETE", "versions/block/{encodedPattern}", EndpointPolicy.GlobalAdminOnly),
        new("POST",   "maintenance/trigger",        EndpointPolicy.GlobalAdminOnly),
        new("GET",    "health/detailed",            EndpointPolicy.AuthenticatedUser),
        new("POST",   "rules/reseed-from-github",   EndpointPolicy.GlobalAdminOnly),
        new("GET",    "vulnerability/unmatched-software", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "vulnerability/software-inventory", EndpointPolicy.GlobalAdminOnly),
        new("POST",   "vulnerability/sync",              EndpointPolicy.GlobalAdminOnly),
        new("POST",   "vulnerability/cpe-mapping",       EndpointPolicy.GlobalAdminOnly),
        new("DELETE", "vulnerability/cpe-mapping",       EndpointPolicy.GlobalAdminOnly),
        new("GET",    "vulnerability/cpe-mappings",      EndpointPolicy.GlobalAdminOnly),
        new("POST",   "rules/ime-log-patterns/reseed", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/mcp-users",                     EndpointPolicy.GlobalAdminOnly),
        new("POST",   "global/mcp-users",                     EndpointPolicy.GlobalAdminOnly),
        new("DELETE", "global/mcp-users/{upn}",               EndpointPolicy.GlobalAdminOnly),
        new("PATCH",  "global/mcp-users/{upn}/enable",        EndpointPolicy.GlobalAdminOnly),
        new("PATCH",  "global/mcp-users/{upn}/disable",       EndpointPolicy.GlobalAdminOnly),
        new("PATCH",  "global/mcp-users/{upn}/usage-plan",    EndpointPolicy.GlobalAdminOnly),
        new("PATCH",  "config/{tenantId}/plan",                            EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/config/plan-tiers",                           EndpointPolicy.GlobalAdminOnly),
        new("PUT",    "global/config/plan-tiers",                           EndpointPolicy.GlobalAdminOnly),
        new("GET",    "feedback/all",                                     EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/notifications",                            EndpointPolicy.GlobalAdminOnly),
        new("POST",   "global/notifications/dismiss-all",                EndpointPolicy.GlobalAdminOnly),
        new("POST",   "global/notifications/{notificationId}/dismiss",   EndpointPolicy.GlobalAdminOnly),
    };

    /// <summary>
    /// All registered policy entries. Used by completeness tests.
    /// </summary>
    public static IReadOnlyList<EndpointPolicyEntry> Entries => _entries;

    /// <summary>
    /// Finds the policy for a given HTTP method and request path.
    /// Path should include /api/ prefix (e.g., "/api/sessions/abc-123/events").
    /// Returns null if no matching entry is found (fail-closed: caller should deny).
    /// </summary>
    public static EndpointPolicyEntry? FindPolicy(string httpMethod, string path)
    {
        // Strip /api/ prefix for matching against route templates
        var normalizedPath = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            ? path.Substring(5)
            : path;

        var method = httpMethod.ToUpperInvariant();

        // Find the best match. Priority:
        // 1. Literal (no {param}) matches over parameterized ones
        // 2. Among same type, longer template wins (more specific)
        EndpointPolicyEntry? bestMatch = null;
        var bestIsLiteral = false;

        foreach (var entry in _entries)
        {
            if (entry.HttpMethod != method)
                continue;

            if (!entry.RouteRegex.IsMatch(normalizedPath))
                continue;

            var isLiteral = !entry.RouteTemplate.Contains('{');

            // Literal match always beats parameterized match
            if (isLiteral && !bestIsLiteral)
            {
                bestMatch = entry;
                bestIsLiteral = true;
            }
            else if (isLiteral == bestIsLiteral)
            {
                // Same category: prefer longer (more specific) template
                if (bestMatch == null || entry.RouteTemplate.Length > bestMatch.RouteTemplate.Length)
                {
                    bestMatch = entry;
                    bestIsLiteral = isLiteral;
                }
            }
        }

        return bestMatch;
    }
}
