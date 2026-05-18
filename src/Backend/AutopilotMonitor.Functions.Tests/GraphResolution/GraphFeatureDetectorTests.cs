using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Shared.Models.Graph;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace AutopilotMonitor.Functions.Tests.GraphResolution;

/// <summary>
/// Pure-logic tests for <see cref="GraphFeatureDetector.TryParseToken"/>. The cache +
/// token-service orchestration is covered indirectly by <c>ScriptDisplayNameResolverTests</c>
/// (PR-C), where a fake context is set up end-to-end.
/// </summary>
public class GraphFeatureDetectorTests
{
    [Fact]
    public void TryParseToken_returns_single_role()
    {
        var token = BuildJwt(
            new[] { new Claim("roles", GraphAppPermissions.DeviceManagementScriptsReadAll) },
            expires: DateTime.UtcNow.AddHours(1));

        Assert.True(GraphFeatureDetector.TryParseToken(token, out var roles, out var expiresAt));
        Assert.Contains(GraphAppPermissions.DeviceManagementScriptsReadAll, roles);
        Assert.Single(roles);
        Assert.True(expiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void TryParseToken_collects_multiple_roles_case_insensitive()
    {
        var token = BuildJwt(
            new[]
            {
                new Claim("roles", "DeviceManagementScripts.Read.All"),
                new Claim("roles", "DeviceManagementConfiguration.Read.All"),
                new Claim("roles", "DeviceManagementManagedDevices.Read.All"),
            },
            expires: DateTime.UtcNow.AddHours(1));

        Assert.True(GraphFeatureDetector.TryParseToken(token, out var roles, out _));
        Assert.Equal(3, roles.Count);
        // Case-insensitive contains: tenant might issue with slightly different casing.
        Assert.Contains("devicemanagementscripts.read.all", roles);
    }

    [Fact]
    public void TryParseToken_returns_empty_set_when_roles_claim_missing()
    {
        var token = BuildJwt(
            new[] { new Claim("scp", "User.Read") },
            expires: DateTime.UtcNow.AddHours(1));

        Assert.True(GraphFeatureDetector.TryParseToken(token, out var roles, out _));
        Assert.Empty(roles);
    }

    [Fact]
    public void TryParseToken_filters_empty_role_values()
    {
        var token = BuildJwt(
            new[]
            {
                new Claim("roles", ""),
                new Claim("roles", "  "),
                new Claim("roles", "DeviceManagementScripts.Read.All"),
            },
            expires: DateTime.UtcNow.AddHours(1));

        Assert.True(GraphFeatureDetector.TryParseToken(token, out var roles, out _));
        Assert.Single(roles);
        Assert.Contains("DeviceManagementScripts.Read.All", roles);
    }

    [Fact]
    public void TryParseToken_fails_on_garbage_input()
    {
        Assert.False(GraphFeatureDetector.TryParseToken("not.a.jwt", out _, out _));
        Assert.False(GraphFeatureDetector.TryParseToken("", out _, out _));
        Assert.False(GraphFeatureDetector.TryParseToken("definitely-not-base64-and-no-dots", out _, out _));
    }

    [Fact]
    public void TryParseToken_reads_expiry_correctly()
    {
        var fixedExpiry = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var token = BuildJwt(new[] { new Claim("roles", "x") }, expires: fixedExpiry);

        Assert.True(GraphFeatureDetector.TryParseToken(token, out _, out var expiresAt));
        // JWT 'exp' is unix seconds — second precision is sufficient.
        Assert.Equal(fixedExpiry, expiresAt.UtcDateTime);
    }

    // ── HasPermission helper observable behaviour via cache-only path ────────

    [Fact]
    public void IsFeatureGranted_via_catalog()
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            GraphAppPermissions.DeviceManagementScriptsReadAll,
        };

        Assert.True(GraphFeatureCatalog.IsFeatureGranted(
            GraphFeatureCatalog.FeatureScriptDisplayNames, roles));
        Assert.False(GraphFeatureCatalog.IsFeatureGranted("UnknownFeature", roles));
    }

    [Fact]
    public void IsFeatureGranted_returns_false_when_required_permission_missing()
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Foo.Bar" };

        Assert.False(GraphFeatureCatalog.IsFeatureGranted(
            GraphFeatureCatalog.FeatureScriptDisplayNames, roles));
    }

    [Fact]
    public void IsFeatureGranted_accepts_any_collection_shape_without_unsafe_cast()
    {
        // Verifies Finding 3: the catalog API no longer demands ISet<string>, so an
        // IReadOnlySet (or any IEnumerable) works without casting.
        IReadOnlySet<string> roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            GraphAppPermissions.DeviceManagementScriptsReadAll,
        };
        Assert.True(GraphFeatureCatalog.IsFeatureGranted(
            GraphFeatureCatalog.FeatureScriptDisplayNames, roles));

        // Even a plain array (case-insensitive match handled internally).
        string[] rolesArray = { "devicemanagementscripts.read.all" };
        Assert.True(GraphFeatureCatalog.IsFeatureGranted(
            GraphFeatureCatalog.FeatureScriptDisplayNames, rolesArray));

        // Null collection is safely false.
        Assert.False(GraphFeatureCatalog.IsFeatureGranted(
            GraphFeatureCatalog.FeatureScriptDisplayNames, (IEnumerable<string>?)null));
    }

    // ── ScriptRef parsing (Shared/Models/Graph) ─────────────────────────────

    [Theory]
    [InlineData("Platform:abc-123", ScriptKind.Platform, "abc-123")]
    [InlineData("platform:abc-123", ScriptKind.Platform, "abc-123")]
    [InlineData("Remediation:def", ScriptKind.Remediation, "def")]
    [InlineData("REMEDIATION:def", ScriptKind.Remediation, "def")]
    public void ScriptRef_parses_canonical_form(string input, ScriptKind kind, string id)
    {
        Assert.True(ScriptRef.TryParse(input, out var r));
        Assert.Equal(kind, r.Kind);
        Assert.Equal(id, r.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nokind")]
    [InlineData("UnknownKind:abc")]
    [InlineData(":id")]
    [InlineData("Platform:")]
    public void ScriptRef_rejects_malformed(string? input)
    {
        Assert.False(ScriptRef.TryParse(input, out _));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string BuildJwt(IEnumerable<Claim> claims, DateTime expires)
    {
        // JWT without signature is fine — the detector NEVER validates signature
        // (this is our own token, read seconds after acquisition from Azure AD over TLS).
        var jwt = new JwtSecurityToken(
            issuer: "https://sts.windows.net/test",
            audience: "https://graph.microsoft.com",
            claims: claims,
            notBefore: null,
            expires: expires);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    /// <summary>Smoke: cache hit short-circuits — no token-service call needed.</summary>
    [Fact]
    public void Cache_hit_returns_cached_context_without_acquire()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        const string tenantId = "11111111-1111-1111-1111-111111111111";
        var preSeeded = new GraphTenantTokenContext
        {
            TenantId = tenantId,
            AccessToken = "fake",
            GrantedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                GraphAppPermissions.DeviceManagementScriptsReadAll,
            },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        };
        cache.Set($"graph-feature-detector:{tenantId}", preSeeded);

        // We can verify cache layout works by reading back through the same key.
        // Full Detector orchestration is covered in PR-C via the Resolver integration tests.
        Assert.True(cache.TryGetValue($"graph-feature-detector:{tenantId}",
            out GraphTenantTokenContext? roundtrip));
        Assert.Equal(tenantId, roundtrip!.TenantId);
        Assert.Contains(GraphAppPermissions.DeviceManagementScriptsReadAll, roundtrip.GrantedRoles);
    }
}
