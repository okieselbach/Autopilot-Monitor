using System.Security.Claims;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="PolicyEnforcementMiddleware"/> via its transport-agnostic <c>DecideAsync</c> seam
/// (authorization + cross-tenant enforcement + RequestContext construction — everything <c>Invoke</c> does
/// minus reading the request / writing the response). Uses the REAL GlobalAdminService /
/// TenantAdminsService / TenantConfigurationService — they all resolve through <see cref="IAdminRepository"/>
/// + an in-memory cache, so only the repository is mocked.
///
/// Primary purpose: lock in the ADDITIVE GlobalReader semantics in the resolved RequestContext —
/// a UPN that is both a GlobalReader and its own tenant's Admin must still surface IsTenantAdmin=true
/// (otherwise admin SignalR group joins + admin-audience notifications regress), while a pure GlobalReader
/// stays read-only. Also covers the cross-tenant gate keying off HasGlobalScope.
/// </summary>
public class PolicyEnforcementMiddlewareTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    private sealed class Harness
    {
        public required PolicyEnforcementMiddleware Middleware { get; init; }
        public required Mock<IAdminRepository> Repo { get; init; }

        public void AsGlobalRole(string role) =>
            Repo.Setup(r => r.GetGlobalRoleAsync(It.IsAny<string>())).ReturnsAsync(role);

        public void AsTenantAdmin(string tenantId, string upn) =>
            Repo.Setup(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new TenantMember
                {
                    Upn = upn,
                    TenantId = tenantId,
                    Role = Constants.TenantRoles.Admin,
                    IsEnabled = true,
                });
    }

    private static Harness BuildHarness()
    {
        var repo = new Mock<IAdminRepository>();
        // Defaults: no platform role, no tenant membership. Tests override per-case.
        repo.Setup(r => r.GetGlobalRoleAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        repo.Setup(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((TenantMember?)null);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var globalAdmin = new GlobalAdminService(repo.Object, cache, NullLogger<GlobalAdminService>.Instance);
        var tenantAdmins = new TenantAdminsService(repo.Object, cache, NullLogger<TenantAdminsService>.Instance);
        var tenantConfig = new TenantConfigurationService(
            Mock.Of<IConfigRepository>(), NullLogger<TenantConfigurationService>.Instance, cache);

        var mw = new PolicyEnforcementMiddleware(
            NullLogger<PolicyEnforcementMiddleware>.Instance, globalAdmin, tenantAdmins, tenantConfig);

        return new Harness { Middleware = mw, Repo = repo };
    }

    private static ClaimsPrincipal AuthedPrincipal(string tenantId, string upn)
        => new(new ClaimsIdentity(new[] { new Claim("tid", tenantId), new Claim("upn", upn) }, "TestAuth"));

    // ── ADDITIVE: GlobalReader + own-tenant Admin keeps IsTenantAdmin ──────────────

    [Fact]
    public async Task MemberRead_GlobalReaderWhoIsAlsoTenantAdmin_KeepsIsTenantAdmin()
    {
        const string upn = "dual@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);
        h.AsTenantAdmin(TenantA, upn);

        // /api/notifications is MemberRead — its audience depends on IsTenantAdmin.
        var result = await h.Middleware.DecideAsync("GET", "/api/notifications", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        var rc = result.Context!;
        Assert.True(rc.IsGlobalReader);
        Assert.False(rc.IsGlobalAdmin);
        Assert.True(rc.IsTenantAdmin); // additive: own-tenant admin survives the global-role branch
        Assert.True(rc.HasGlobalScope);
    }

    [Fact]
    public async Task AuthenticatedUserWithRole_GlobalReaderWhoIsAlsoTenantAdmin_KeepsIsTenantAdmin()
    {
        const string upn = "dual@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);
        h.AsTenantAdmin(TenantA, upn);

        // SignalR group join is AuthenticatedUserWithRole — the admin notification group gates on IsTenantAdmin.
        var result = await h.Middleware.DecideAsync("POST", "/api/realtime/groups/join", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        Assert.True(result.Context!.IsGlobalReader);
        Assert.True(result.Context!.IsTenantAdmin);
    }

    // ── Pure roles (regression guards for the TenantRole ?? UserRole fallback) ─────

    [Fact]
    public async Task MemberRead_PureGlobalReader_IsNotTenantAdmin()
    {
        const string upn = "reader@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);
        // No tenant membership (default null).

        var result = await h.Middleware.DecideAsync("GET", "/api/notifications", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        Assert.True(result.Context!.IsGlobalReader);
        Assert.False(result.Context!.IsTenantAdmin); // no own-tenant role ⇒ not a tenant admin
    }

    [Fact]
    public async Task MemberRead_PureTenantAdmin_IsTenantAdmin_NoGlobalScope()
    {
        const string upn = "admin@contoso.com";
        var h = BuildHarness();
        h.AsTenantAdmin(TenantA, upn); // enabled Admin row, no global role

        var result = await h.Middleware.DecideAsync("GET", "/api/notifications", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        Assert.True(result.Context!.IsTenantAdmin);   // via the TenantRole ?? UserRole fallback (UserRole = "Admin")
        Assert.False(result.Context!.IsGlobalReader);
        Assert.False(result.Context!.IsGlobalAdmin);
    }

    // ── ADDITIVE write: GlobalReader + own-tenant Admin may still write own tenant ─

    [Fact]
    public async Task WriteRoute_GlobalReaderWhoIsAlsoTenantAdmin_AllowedInOwnTenant()
    {
        const string upn = "dual@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);
        h.AsTenantAdmin(TenantA, upn);

        // PUT config/{tenantId} is TenantAdminOrGA (write). Route tenant == JWT tenant ⇒ own-tenant write.
        var result = await h.Middleware.DecideAsync("PUT", $"/api/config/{TenantA}", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed); // additive: the tenant-admin hat still grants write in the own tenant
    }

    // ── Pure GlobalReader is read-only: write routes are denied ────────────────────

    [Fact]
    public async Task WriteRoute_PureGlobalReader_IsForbidden()
    {
        const string upn = "reader@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);
        // No tenant membership ⇒ no write path.

        var result = await h.Middleware.DecideAsync("PUT", $"/api/config/{TenantA}", null, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    // ── config GET (TenantAdminOrGlobalReader): own-tenant admin view vs reader view ──
    // GetTenantConfigurationFunction redacts only when IsGlobalReader && !(IsTenantAdmin && Target==Tenant).
    // These lock in the RequestContext that drives that decision.

    [Fact]
    public async Task ConfigGet_GlobalReaderWhoIsOwnTenantAdmin_OwnTenant_IsFullView()
    {
        const string upn = "dual@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);
        h.AsTenantAdmin(TenantA, upn);

        var result = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantA}", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        var rc = result.Context!;
        Assert.True(rc.IsTenantAdmin);
        Assert.Equal(rc.TenantId, rc.TargetTenantId); // own tenant ⇒ ownTenantAdminView ⇒ FULL config (not redacted)
    }

    [Fact]
    public async Task ConfigGet_GlobalReaderCrossTenant_IsReaderView()
    {
        const string upn = "dual@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);
        h.AsTenantAdmin(TenantA, upn); // admin of their OWN tenant (A); role resolves against JWT tenant

        var result = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantB}", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        var rc = result.Context!;
        Assert.True(rc.IsGlobalReader);
        Assert.True(rc.IsTenantAdmin);                  // admin of A…
        Assert.NotEqual(rc.TenantId, rc.TargetTenantId); // …but viewing B ⇒ reader view ⇒ REDACTED
        Assert.Equal(TenantB, rc.TargetTenantId);
    }

    // ── Cross-tenant read: GlobalReader crosses tenants (HasGlobalScope gate) ───────

    [Fact]
    public async Task CrossTenantRead_GlobalReader_AllowedAndTargetsForeignTenant()
    {
        const string upn = "reader@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);

        // Reader in TenantA reading TenantB's config (RouteParam). HasGlobalScope ⇒ cross-tenant allowed.
        var result = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantB}", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        Assert.Equal(TenantB, result.Context!.TargetTenantId);
    }

    [Fact]
    public async Task CrossTenantRead_PureTenantAdmin_IsForbidden()
    {
        const string upn = "admin@contoso.com";
        var h = BuildHarness();
        h.AsTenantAdmin(TenantA, upn); // admin of A only, no global scope

        // Admin of TenantA trying to read TenantB's config → cross-tenant blocked.
        var result = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantB}", null, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("CrossTenantAccessDenied", result.ErrorCode);
    }

    // ── Unauthenticated / unregistered ─────────────────────────────────────────────

    [Fact]
    public async Task MemberRead_NoPrincipal_IsUnauthorized()
    {
        var h = BuildHarness();
        var result = await h.Middleware.DecideAsync("GET", "/api/notifications", null, principal: null);

        Assert.False(result.Allowed);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task UnregisteredRoute_IsForbidden()
    {
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalAdmin);

        var result = await h.Middleware.DecideAsync("GET", "/api/does-not-exist", null, AuthedPrincipal(TenantA, "ga@contoso.com"));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("Forbidden", result.ErrorCode);
    }
}
