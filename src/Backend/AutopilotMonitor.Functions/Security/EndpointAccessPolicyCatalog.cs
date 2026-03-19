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

    /// <summary>Tenant Admin or Galactic Admin. Tenant-scoped write access.</summary>
    TenantAdminOrGA,

    /// <summary>Admin (always) or Operator with CanManageBootstrapTokens permission, or Galactic Admin.</summary>
    BootstrapManagerOrGA,

    /// <summary>Galactic Admin only. Platform-wide access.</summary>
    GalacticAdminOnly,
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
        new("GET",    "auth/is-galactic-admin",    EndpointPolicy.AuthenticatedUser),
        new("POST",   "realtime/negotiate",        EndpointPolicy.AuthenticatedUser),
        new("POST",   "realtime/groups/join",      EndpointPolicy.AuthenticatedUser),
        new("POST",   "realtime/groups/leave",     EndpointPolicy.AuthenticatedUser),
        new("GET",    "progress/sessions",         EndpointPolicy.AuthenticatedUser),
        new("GET",    "progress/sessions/{sessionId}/events", EndpointPolicy.AuthenticatedUser),
        new("PUT",    "preview/notification-email", EndpointPolicy.AuthenticatedUser),
        new("GET",    "feedback/status",           EndpointPolicy.AuthenticatedUser),
        new("POST",   "feedback",                  EndpointPolicy.AuthenticatedUser),

        // ── MemberRead (Admin + Operator, later + Viewer) ───────────────
        new("GET",    "sessions",                  EndpointPolicy.MemberRead),
        new("GET",    "sessions/{sessionId}",      EndpointPolicy.MemberRead),
        new("GET",    "sessions/{sessionId}/events", EndpointPolicy.MemberRead),
        new("GET",    "sessions/{sessionId}/analysis", EndpointPolicy.MemberRead),
        new("GET",    "metrics/app",               EndpointPolicy.MemberRead),
        new("GET",    "metrics/usage",             EndpointPolicy.MemberRead),
        new("GET",    "metrics/geographic",        EndpointPolicy.MemberRead),
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

        // ── GalacticAdminOnly ───────────────────────────────────────────
        new("GET",    "global/config",             EndpointPolicy.GalacticAdminOnly),
        new("PUT",    "global/config",             EndpointPolicy.GalacticAdminOnly),
        new("POST",   "global/config",             EndpointPolicy.GalacticAdminOnly),
        new("GET",    "config/all",                EndpointPolicy.GalacticAdminOnly),
        new("GET",    "auth/galactic-admins",      EndpointPolicy.GalacticAdminOnly),
        new("POST",   "auth/galactic-admins",      EndpointPolicy.GalacticAdminOnly),
        new("DELETE", "auth/galactic-admins/{upn}", EndpointPolicy.GalacticAdminOnly),
        new("GET",    "preview/whitelist",          EndpointPolicy.GalacticAdminOnly),
        new("POST",   "preview/whitelist/{tenantId}", EndpointPolicy.GalacticAdminOnly),
        new("DELETE", "preview/whitelist/{tenantId}", EndpointPolicy.GalacticAdminOnly),
        new("GET",    "preview/notification-email/{tenantId}", EndpointPolicy.GalacticAdminOnly),
        new("POST",   "preview/send-welcome-email/{tenantId}", EndpointPolicy.GalacticAdminOnly),
        new("GET",    "galactic/sessions",          EndpointPolicy.GalacticAdminOnly),
        new("GET",    "galactic/audit/logs",        EndpointPolicy.GalacticAdminOnly),
        new("GET",    "galactic/metrics/platform",  EndpointPolicy.GalacticAdminOnly),
        new("GET",    "galactic/metrics/app",       EndpointPolicy.GalacticAdminOnly),
        new("GET",    "galactic/metrics/geographic", EndpointPolicy.GalacticAdminOnly),
        new("GET",    "galactic/metrics/geographic/sessions", EndpointPolicy.GalacticAdminOnly),
        new("GET",    "galactic/metrics/usage",     EndpointPolicy.GalacticAdminOnly),
        new("GET",    "galactic/session-reports",   EndpointPolicy.GalacticAdminOnly),
        new("GET",    "galactic/session-reports/download-url", EndpointPolicy.GalacticAdminOnly),
        new("PATCH",  "galactic/session-reports/{reportId}/note", EndpointPolicy.GalacticAdminOnly),
        new("GET",    "galactic/rules/gather",      EndpointPolicy.GalacticAdminOnly),
        new("GET",    "galactic/rules/analyze",     EndpointPolicy.GalacticAdminOnly),
        new("GET",    "galactic/devices/blocked",   EndpointPolicy.GalacticAdminOnly),
        new("GET",    "devices/blocked",            EndpointPolicy.GalacticAdminOnly),
        new("POST",   "devices/block",              EndpointPolicy.GalacticAdminOnly),
        new("DELETE", "devices/block/{encodedSerialNumber}", EndpointPolicy.GalacticAdminOnly),
        new("GET",    "versions/blocked",           EndpointPolicy.GalacticAdminOnly),
        new("POST",   "versions/block",             EndpointPolicy.GalacticAdminOnly),
        new("DELETE", "versions/block/{encodedPattern}", EndpointPolicy.GalacticAdminOnly),
        new("POST",   "maintenance/trigger",        EndpointPolicy.GalacticAdminOnly),
        new("GET",    "health/detailed",            EndpointPolicy.AuthenticatedUser),
        new("POST",   "rules/reseed-from-github",   EndpointPolicy.GalacticAdminOnly),
        new("POST",   "rules/ime-log-patterns/reseed", EndpointPolicy.GalacticAdminOnly),
        new("GET",    "feedback/all",                                     EndpointPolicy.GalacticAdminOnly),
        new("GET",    "galactic/notifications",                          EndpointPolicy.GalacticAdminOnly),
        new("POST",   "galactic/notifications/dismiss-all",              EndpointPolicy.GalacticAdminOnly),
        new("POST",   "galactic/notifications/{notificationId}/dismiss", EndpointPolicy.GalacticAdminOnly),
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
