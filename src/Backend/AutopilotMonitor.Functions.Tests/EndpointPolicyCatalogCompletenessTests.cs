using System.Reflection;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Security;
using Microsoft.Azure.Functions.Worker;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Ensures the EndpointAccessPolicyCatalog is a complete and accurate mapping
/// of every HTTP route in the Functions project. Fail-closed: any unregistered
/// route causes a test failure.
/// </summary>
public class EndpointPolicyCatalogCompletenessTests
{
    /// <summary>
    /// Every [HttpTrigger] route + method combination must have a matching catalog entry.
    /// This prevents new endpoints from being deployed without an explicit policy decision.
    /// </summary>
    [Fact]
    public void AllHttpTriggers_HaveMatchingCatalogEntry()
    {
        var assembly = typeof(AuthenticationMiddleware).Assembly;
        var missing = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var triggerParam = method.GetParameters()
                    .FirstOrDefault(p => p.GetCustomAttribute<HttpTriggerAttribute>() != null);

                if (triggerParam == null)
                    continue;

                var trigger = triggerParam.GetCustomAttribute<HttpTriggerAttribute>()!;
                var route = trigger.Route;

                if (string.IsNullOrEmpty(route))
                    continue;

                var httpMethods = trigger.Methods ?? Array.Empty<string>();
                if (httpMethods.Length == 0)
                    httpMethods = new[] { "GET" }; // default if no method specified

                foreach (var httpMethod in httpMethods)
                {
                    var entry = EndpointAccessPolicyCatalog.FindPolicy(httpMethod, $"/api/{route}");
                    if (entry == null)
                    {
                        missing.Add($"{httpMethod.ToUpper()} /api/{route}");
                    }
                }
            }
        }

        Assert.True(missing.Count == 0,
            $"The following HTTP routes are NOT registered in EndpointAccessPolicyCatalog (fail-closed):\n" +
            string.Join("\n", missing.Select(m => $"  - {m}")));
    }

    /// <summary>
    /// Every catalog entry must match at least one actual [HttpTrigger] route.
    /// Prevents stale/orphaned entries that could mask security misconfigurations.
    /// </summary>
    [Fact]
    public void AllCatalogEntries_MatchExistingRoutes()
    {
        var assembly = typeof(AuthenticationMiddleware).Assembly;

        // Collect all actual (method, route) pairs from HttpTrigger attributes
        var actualRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var triggerParam = method.GetParameters()
                    .FirstOrDefault(p => p.GetCustomAttribute<HttpTriggerAttribute>() != null);

                if (triggerParam == null)
                    continue;

                var trigger = triggerParam.GetCustomAttribute<HttpTriggerAttribute>()!;
                var route = trigger.Route;

                if (string.IsNullOrEmpty(route))
                    continue;

                var httpMethods = trigger.Methods ?? Array.Empty<string>();
                if (httpMethods.Length == 0)
                    httpMethods = new[] { "GET" };

                foreach (var httpMethod in httpMethods)
                {
                    actualRoutes.Add($"{httpMethod.ToUpper()}:{route}");
                }
            }
        }

        var orphaned = new List<string>();

        foreach (var entry in EndpointAccessPolicyCatalog.Entries)
        {
            // Check if any actual route matches this catalog entry via FindPolicy
            var hasMatch = actualRoutes.Any(ar =>
            {
                var parts = ar.Split(':', 2);
                var method = parts[0];
                var route = parts[1];
                var found = EndpointAccessPolicyCatalog.FindPolicy(method, $"/api/{route}");
                return found != null && found.RouteTemplate == entry.RouteTemplate && found.HttpMethod == entry.HttpMethod;
            });

            if (!hasMatch)
            {
                orphaned.Add($"{entry.HttpMethod} {entry.RouteTemplate} [{entry.Policy}]");
            }
        }

        Assert.True(orphaned.Count == 0,
            $"The following catalog entries have no matching [HttpTrigger] route (stale entries):\n" +
            string.Join("\n", orphaned.Select(o => $"  - {o}")));
    }

    /// <summary>
    /// Verifies that parameterized route templates correctly match actual request paths.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/sessions/abc-123", "sessions/{sessionId}")]
    [InlineData("GET", "/api/sessions/abc-123/events", "sessions/{sessionId}/events")]
    [InlineData("GET", "/api/sessions/abc-123/analysis", "sessions/{sessionId}/analysis")]
    [InlineData("DELETE", "/api/sessions/abc-123", "sessions/{sessionId}")]
    [InlineData("GET", "/api/bootstrap/validate/ABCD12", "bootstrap/validate/{code}")]
    [InlineData("DELETE", "/api/bootstrap/sessions/MYCODE", "bootstrap/sessions/{code}")]
    [InlineData("GET", "/api/config/00000000-0000-0000-0000-000000000001", "config/{tenantId}")]
    [InlineData("PUT", "/api/rules/gather/rule-1", "rules/gather/{ruleId}")]
    [InlineData("DELETE", "/api/tenants/tid-1/admins/user@contoso.com", "tenants/{tenantId}/admins/{adminUpn}")]
    [InlineData("PATCH", "/api/tenants/tid-1/admins/user@contoso.com/permissions", "tenants/{tenantId}/admins/{adminUpn}/permissions")]
    [InlineData("DELETE", "/api/devices/block/SN123456", "devices/block/{encodedSerialNumber}")]
    [InlineData("DELETE", "/api/versions/block/v1.0.*", "versions/block/{encodedPattern}")]
    [InlineData("PATCH", "/api/global/session-reports/report-1/note", "global/session-reports/{reportId}/note")]
    [InlineData("POST", "/api/rules/analyze/ANALYZE-ID-001/create-from-template", "rules/analyze/{ruleId}/create-from-template")]
    public void ParameterizedRoutes_MatchCorrectly(string httpMethod, string requestPath, string expectedTemplate)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(httpMethod, requestPath);

        Assert.NotNull(entry);
        Assert.Equal(expectedTemplate, entry.RouteTemplate);
    }

    /// <summary>
    /// Routes that differ only by HTTP method should resolve to different policies.
    /// </summary>
    [Fact]
    public void SameRoute_DifferentMethods_ResolveToDifferentPolicies()
    {
        var getConfig = EndpointAccessPolicyCatalog.FindPolicy("GET", "/api/config/tenant-1");
        var putConfig = EndpointAccessPolicyCatalog.FindPolicy("PUT", "/api/config/tenant-1");

        Assert.NotNull(getConfig);
        Assert.NotNull(putConfig);
        Assert.Equal(EndpointPolicy.TenantAdminOrGA, getConfig.Policy);
        Assert.Equal(EndpointPolicy.TenantAdminOrGA, putConfig.Policy);

        var getRules = EndpointAccessPolicyCatalog.FindPolicy("GET", "/api/rules/gather");
        var postRules = EndpointAccessPolicyCatalog.FindPolicy("POST", "/api/rules/gather");

        Assert.NotNull(getRules);
        Assert.NotNull(postRules);
        Assert.Equal(EndpointPolicy.MemberRead, getRules.Policy);
        Assert.Equal(EndpointPolicy.TenantAdminOrGA, postRules.Policy);
    }

    /// <summary>
    /// Unregistered routes must return null (fail-closed).
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/nonexistent")]
    [InlineData("POST", "/api/sessions")]  // GET exists, POST doesn't
    [InlineData("DELETE", "/api/health")]   // GET exists, DELETE doesn't
    public void UnregisteredRoutes_ReturnNull(string httpMethod, string requestPath)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(httpMethod, requestPath);
        Assert.Null(entry);
    }

    /// <summary>
    /// Every route template containing {tenantId} must declare TenantScoping.RouteParam.
    /// This prevents new endpoints with {tenantId} from bypassing cross-tenant validation.
    /// </summary>
    [Fact]
    public void RoutesWithTenantIdParam_MustHaveTenantScopingRouteParam()
    {
        var missing = EndpointAccessPolicyCatalog.Entries
            .Where(e => e.RouteTemplate.Contains("{tenantId}")
                     && e.TenantScoping != TenantScoping.RouteParam)
            .Select(e => $"{e.HttpMethod} {e.RouteTemplate} [{e.Policy}]")
            .ToList();

        Assert.True(missing.Count == 0,
            "Routes with {tenantId} in template must declare TenantScoping.RouteParam:\n" +
            string.Join("\n", missing.Select(m => $"  - {m}")));
    }

    /// <summary>
    /// Every entry with TenantScoping.RouteParam must have {tenantId} in its route template.
    /// Prevents misattributed scoping declarations.
    /// </summary>
    [Fact]
    public void RouteParamScoping_RequiresTenantIdInTemplate()
    {
        var invalid = EndpointAccessPolicyCatalog.Entries
            .Where(e => e.TenantScoping == TenantScoping.RouteParam
                     && !e.RouteTemplate.Contains("{tenantId}"))
            .Select(e => $"{e.HttpMethod} {e.RouteTemplate} [{e.Policy}]")
            .ToList();

        Assert.True(invalid.Count == 0,
            "Entries with TenantScoping.RouteParam must have {tenantId} in their route template:\n" +
            string.Join("\n", invalid.Select(i => $"  - {i}")));
    }

    /// <summary>
    /// The three global/apps/* routes must be locked to GlobalAdminOnly with
    /// TenantScoping.None (the optional ?tenantId= query param is intentional cross-tenant
    /// scoping, enforced inside the function — not by the middleware).
    /// A future accidental downgrade of the policy tier would be caught here, whereas
    /// the generic completeness test only guarantees that *some* entry exists.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/global/apps/list",                       "global/apps/list")]
    [InlineData("GET", "/api/global/apps/Company%20Portal/analytics", "global/apps/{appName}/analytics")]
    [InlineData("GET", "/api/global/apps/Company%20Portal/sessions",  "global/apps/{appName}/sessions")]
    public void GlobalAppsRoutes_AreGlobalAdminOnly(string method, string path, string expectedTemplate)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(method, path);

        Assert.NotNull(entry);
        Assert.Equal(expectedTemplate, entry!.RouteTemplate);
        Assert.Equal(EndpointPolicy.GlobalAdminOnly, entry.Policy);
        Assert.Equal(TenantScoping.None, entry.TenantScoping);
    }

    /// <summary>
    /// Named capture group for {tenantId} correctly extracts the value from request paths.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/config/00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/tenants/abc-def-123/admins", "abc-def-123")]
    [InlineData("DELETE", "/api/tenants/tid-1/admins/user@contoso.com", "tid-1")]
    public void RouteParamRoutes_ExtractTenantIdFromPath(string httpMethod, string requestPath, string expectedTenantId)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(httpMethod, requestPath);
        Assert.NotNull(entry);
        Assert.Equal(TenantScoping.RouteParam, entry.TenantScoping);

        var normalizedPath = requestPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            ? requestPath.Substring(5)
            : requestPath;
        var match = entry.RouteRegex.Match(normalizedPath);
        Assert.True(match.Success);
        Assert.Equal(expectedTenantId, match.Groups["tenantId"].Value);
    }
}
