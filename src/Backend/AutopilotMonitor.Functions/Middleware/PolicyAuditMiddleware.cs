using System.Net;
using System.Security.Claims;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Logging-only middleware that evaluates the EndpointAccessPolicyCatalog against each request
/// and compares its decision with the actual pipeline outcome. Never blocks requests.
///
/// Designed for Phase 2 of the auth refactor: deploy to production, observe mismatches,
/// then switch to enforcement in Phase 3.
///
/// Middleware order: AuthenticationMiddleware → PolicyAuditMiddleware → MemberAuthorizationMiddleware
/// </summary>
public class PolicyAuditMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<PolicyAuditMiddleware> _logger;
    private readonly GalacticAdminService _galacticAdminService;
    private readonly TenantAdminsService _tenantAdminsService;

    public PolicyAuditMiddleware(
        ILogger<PolicyAuditMiddleware> logger,
        GalacticAdminService galacticAdminService,
        TenantAdminsService tenantAdminsService)
    {
        _logger = logger;
        _galacticAdminService = galacticAdminService;
        _tenantAdminsService = tenantAdminsService;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            await next(context);
            return;
        }

        var requestPath = httpContext.Request.Path.Value ?? string.Empty;
        var httpMethod = httpContext.Request.Method;

        // Look up the catalog policy for this route
        var catalogEntry = EndpointAccessPolicyCatalog.FindPolicy(httpMethod, requestPath);

        if (catalogEntry == null)
        {
            _logger.LogWarning("[PolicyAudit] UNREGISTERED route: {Method} {Path} — not in catalog (fail-closed)",
                httpMethod, requestPath);
            await next(context);
            return;
        }

        // Evaluate what the catalog WOULD decide for the current user
        var catalogDecision = await EvaluateCatalogPolicyAsync(context, catalogEntry);

        // Let the rest of the pipeline run (MemberAuth + function)
        await next(context);

        // Compare catalog decision against actual outcome
        var actualStatusCode = httpContext.Response.StatusCode;
        var isAuthFailure = actualStatusCode == (int)HttpStatusCode.Unauthorized
                         || actualStatusCode == (int)HttpStatusCode.Forbidden;
        var actuallyAllowed = !isAuthFailure;

        if (catalogDecision.IsAllowed == actuallyAllowed)
        {
            // Decisions match — no mismatch
            _logger.LogDebug("[PolicyAudit] OK {Method} {Path} policy={Policy} catalogDecision={Decision} actualStatus={Status}",
                httpMethod, requestPath, catalogEntry.Policy, catalogDecision.IsAllowed ? "Allow" : "Deny", actualStatusCode);
            return;
        }

        // Mismatch detected
        if (catalogDecision.IsAllowed && !actuallyAllowed)
        {
            // Catalog says Allow but pipeline denied → pipeline is MORE restrictive than catalog
            // Expected for Viewer role (MemberAuth blocks, catalog would allow MemberRead)
            _logger.LogWarning(
                "[PolicyAudit] MISMATCH(TooRestrictive) {Method} {Path} " +
                "policy={Policy} catalogDecision=Allow actualStatus={Status} " +
                "user={User} role={Role} reason={Reason}",
                httpMethod, requestPath, catalogEntry.Policy, actualStatusCode,
                catalogDecision.UserIdentifier, catalogDecision.UserRole, catalogDecision.Reason);
        }
        else if (!catalogDecision.IsAllowed && actuallyAllowed)
        {
            // Catalog says Deny but pipeline allowed → SECURITY CONCERN: pipeline is too permissive
            _logger.LogError(
                "[PolicyAudit] MISMATCH(TooPermissive) {Method} {Path} " +
                "policy={Policy} catalogDecision=Deny actualStatus={Status} " +
                "user={User} role={Role} reason={Reason}",
                httpMethod, requestPath, catalogEntry.Policy, actualStatusCode,
                catalogDecision.UserIdentifier, catalogDecision.UserRole, catalogDecision.Reason);
        }
    }

    private async Task<CatalogDecisionResult> EvaluateCatalogPolicyAsync(
        FunctionContext context, EndpointPolicyEntry entry)
    {
        // Get the ClaimsPrincipal set by AuthenticationMiddleware (may be null for anonymous routes)
        ClaimsPrincipal? principal = null;
        if (context.Items.TryGetValue("ClaimsPrincipal", out var principalObj)
            && principalObj is ClaimsPrincipal cp
            && cp.Identity?.IsAuthenticated == true)
        {
            principal = cp;
        }

        var userIdentifier = principal?.FindFirst("upn")?.Value
                          ?? principal?.FindFirst("preferred_username")?.Value
                          ?? "anonymous";
        var tenantId = principal?.GetTenantId();
        var upn = principal?.GetUserPrincipalName();

        switch (entry.Policy)
        {
            case EndpointPolicy.PublicAnonymous:
            case EndpointPolicy.DeviceOrBootstrapAuth:
                // These are always allowed at the middleware level
                // (device auth is enforced in functions via ValidateSecurityAsync)
                return CatalogDecisionResult.Allow(userIdentifier, "N/A", "PolicyDoesNotRequireJWT");

            case EndpointPolicy.AuthenticatedUser:
                if (principal != null)
                    return CatalogDecisionResult.Allow(userIdentifier, "Authenticated", "ValidJWT");
                return CatalogDecisionResult.Deny(userIdentifier, "N/A", "NoJWT");

            case EndpointPolicy.MemberRead:
                return await EvaluateMemberReadAsync(tenantId, upn, userIdentifier);

            case EndpointPolicy.TenantAdminOrGA:
                return await EvaluateTenantAdminOrGAAsync(tenantId, upn, userIdentifier);

            case EndpointPolicy.BootstrapManagerOrGA:
                return await EvaluateBootstrapManagerOrGAAsync(tenantId, upn, userIdentifier);

            case EndpointPolicy.GalacticAdminOnly:
                return await EvaluateGalacticAdminOnlyAsync(upn, userIdentifier);

            default:
                return CatalogDecisionResult.Deny(userIdentifier, "N/A", $"UnknownPolicy:{entry.Policy}");
        }
    }

    private async Task<CatalogDecisionResult> EvaluateMemberReadAsync(
        string? tenantId, string? upn, string userIdentifier)
    {
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(upn))
            return CatalogDecisionResult.Deny(userIdentifier, "N/A", "MissingClaims");

        if (await _galacticAdminService.IsGalacticAdminAsync(upn))
            return CatalogDecisionResult.Allow(userIdentifier, "GalacticAdmin", "GABypass");

        var role = await _tenantAdminsService.GetMemberRoleAsync(tenantId, upn);
        if (role == null)
            return CatalogDecisionResult.Deny(userIdentifier, "NonMember", "NotInTenant");

        // MemberRead allows Admin, Operator, AND Viewer (future Phase 3 behavior)
        return CatalogDecisionResult.Allow(userIdentifier, role.Role ?? "Admin", "TenantMember");
    }

    private async Task<CatalogDecisionResult> EvaluateTenantAdminOrGAAsync(
        string? tenantId, string? upn, string userIdentifier)
    {
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(upn))
            return CatalogDecisionResult.Deny(userIdentifier, "N/A", "MissingClaims");

        if (await _galacticAdminService.IsGalacticAdminAsync(upn))
            return CatalogDecisionResult.Allow(userIdentifier, "GalacticAdmin", "GABypass");

        if (await _tenantAdminsService.IsTenantAdminAsync(tenantId, upn))
            return CatalogDecisionResult.Allow(userIdentifier, Constants.TenantRoles.Admin, "TenantAdmin");

        var role = await _tenantAdminsService.GetMemberRoleAsync(tenantId, upn);
        var roleName = role?.Role ?? "NonMember";
        return CatalogDecisionResult.Deny(userIdentifier, roleName, "NotAdminOrGA");
    }

    private async Task<CatalogDecisionResult> EvaluateBootstrapManagerOrGAAsync(
        string? tenantId, string? upn, string userIdentifier)
    {
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(upn))
            return CatalogDecisionResult.Deny(userIdentifier, "N/A", "MissingClaims");

        if (await _galacticAdminService.IsGalacticAdminAsync(upn))
            return CatalogDecisionResult.Allow(userIdentifier, "GalacticAdmin", "GABypass");

        if (await _tenantAdminsService.CanManageBootstrapAsync(tenantId, upn))
            return CatalogDecisionResult.Allow(userIdentifier, "BootstrapManager", "CanManageBootstrap");

        var role = await _tenantAdminsService.GetMemberRoleAsync(tenantId, upn);
        var roleName = role?.Role ?? "NonMember";
        return CatalogDecisionResult.Deny(userIdentifier, roleName, "NoBootstrapPermission");
    }

    private async Task<CatalogDecisionResult> EvaluateGalacticAdminOnlyAsync(
        string? upn, string userIdentifier)
    {
        if (string.IsNullOrEmpty(upn))
            return CatalogDecisionResult.Deny(userIdentifier, "N/A", "MissingClaims");

        if (await _galacticAdminService.IsGalacticAdminAsync(upn))
            return CatalogDecisionResult.Allow(userIdentifier, "GalacticAdmin", "IsGA");

        return CatalogDecisionResult.Deny(userIdentifier, "NonGA", "NotGalacticAdmin");
    }

    /// <summary>
    /// Result of evaluating a catalog policy against the current request context.
    /// </summary>
    private sealed class CatalogDecisionResult
    {
        public bool IsAllowed { get; private init; }
        public string UserIdentifier { get; private init; } = "anonymous";
        public string UserRole { get; private init; } = "N/A";
        public string Reason { get; private init; } = "";

        public static CatalogDecisionResult Allow(string user, string role, string reason)
            => new() { IsAllowed = true, UserIdentifier = user, UserRole = role, Reason = reason };

        public static CatalogDecisionResult Deny(string user, string role, string reason)
            => new() { IsAllowed = false, UserIdentifier = user, UserRole = role, Reason = reason };
    }
}
