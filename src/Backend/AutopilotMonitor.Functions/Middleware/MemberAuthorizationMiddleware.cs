using System.Net;
using System.Security.Claims;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Middleware that enforces tenant membership (Admin or Operator role) on protected routes.
/// Runs after AuthenticationMiddleware. Non-members receive 403 and can only use the Progress Portal.
/// </summary>
public class MemberAuthorizationMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<MemberAuthorizationMiddleware> _logger;
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly GalacticAdminService _galacticAdminService;

    /// <summary>
    /// Route prefixes that are exempt from the member check.
    /// These routes either have their own authorization, use device auth, or are open to any tenant user.
    /// </summary>
    private static readonly string[] _exemptPrefixes =
    {
        "/api/health",           // Anonymous health check
        "/api/stats/",           // Anonymous platform stats
        "/api/agent/",           // Device cert auth, no JWT
        "/api/bootstrap/device", // Bootstrap device routes use cert auth
        "/api/auth/",            // Must work for any authenticated user (role info)
        "/api/progress/",        // Progress Portal — open to any tenant user
        "/api/realtime/",        // SignalR — tenant isolation via group validation
        "/api/galactic/",        // Galactic Admin routes — have own GA checks
        "/api/global/",          // Galactic Admin config — has own GA check
        "/api/versions/",        // Galactic Admin — has own GA check
        "/api/preview/",         // Preview whitelist — has own GA check
    };

    public MemberAuthorizationMiddleware(
        ILogger<MemberAuthorizationMiddleware> logger,
        TenantAdminsService tenantAdminsService,
        GalacticAdminService galacticAdminService)
    {
        _logger = logger;
        _tenantAdminsService = tenantAdminsService;
        _galacticAdminService = galacticAdminService;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            // Non-HTTP trigger (e.g. timer, queue) — no auth needed
            await next(context);
            return;
        }

        var requestPath = httpContext.Request.Path.Value ?? string.Empty;

        // Skip exempt routes
        if (IsExemptRoute(requestPath))
        {
            await next(context);
            return;
        }

        // Get the ClaimsPrincipal set by AuthenticationMiddleware
        if (!context.Items.TryGetValue("ClaimsPrincipal", out var principalObj)
            || principalObj is not ClaimsPrincipal principal
            || principal.Identity?.IsAuthenticated != true)
        {
            // Not authenticated — AuthenticationMiddleware will have already returned 401
            // or this is an anonymous route handled above. Just pass through.
            await next(context);
            return;
        }

        var tenantId = principal.GetTenantId();
        var upn = principal.GetUserPrincipalName();

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(upn))
        {
            // Missing claims — let the function handle it
            await next(context);
            return;
        }

        // Galactic Admins bypass member check
        var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(upn);
        if (isGalacticAdmin)
        {
            await next(context);
            return;
        }

        // Check if user is a tenant member (Admin or Operator)
        var isMember = await _tenantAdminsService.IsTenantMemberAsync(tenantId, upn);
        if (isMember)
        {
            await next(context);
            return;
        }

        // Non-member — block access
        _logger.LogWarning("[MemberAuth] Non-member {Upn} (tenant {TenantId}) blocked from {Path}", upn, tenantId, requestPath);

        httpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
        httpContext.Response.ContentType = "application/json";
        await httpContext.Response.WriteAsJsonAsync(new
        {
            error = "MembershipRequired",
            message = "Access denied. You must be an Admin or Operator for this tenant. Use the Progress Portal for session monitoring."
        });
    }

    private static bool IsExemptRoute(string path)
    {
        foreach (var prefix in _exemptPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
