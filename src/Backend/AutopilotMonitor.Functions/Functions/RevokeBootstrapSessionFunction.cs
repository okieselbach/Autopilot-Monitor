using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// DELETE /api/bootstrap/sessions/{code}?tenantId={tenantId} — Revoke a bootstrap session.
    /// Requires JWT authentication and TenantAdmin role.
    /// </summary>
    public class RevokeBootstrapSessionFunction
    {
        private readonly ILogger<RevokeBootstrapSessionFunction> _logger;
        private readonly BootstrapSessionService _bootstrapService;
        private readonly GalacticAdminService _galacticAdminService;
        private readonly TenantAdminsService _tenantAdminsService;

        public RevokeBootstrapSessionFunction(
            ILogger<RevokeBootstrapSessionFunction> logger,
            BootstrapSessionService bootstrapService,
            GalacticAdminService galacticAdminService,
            TenantAdminsService tenantAdminsService)
        {
            _logger = logger;
            _bootstrapService = bootstrapService;
            _galacticAdminService = galacticAdminService;
            _tenantAdminsService = tenantAdminsService;
        }

        [Function("RevokeBootstrapSession")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "bootstrap/sessions/{code}")] HttpRequestData req,
            string code)
        {
            try
            {
                if (!TenantHelper.IsAuthenticated(req))
                {
                    var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauth.WriteAsJsonAsync(new { error = "Authentication required" });
                    return unauth;
                }

                var authenticatedTenantId = TenantHelper.GetTenantId(req);
                var userIdentifier = TenantHelper.GetUserIdentifier(req);

                var tenantId = req.Query["tenantId"] ?? authenticatedTenantId;

                // Tenant boundary check
                if (!string.Equals(authenticatedTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                {
                    var isGalactic = await _galacticAdminService.IsGalacticAdminAsync(userIdentifier);
                    if (!isGalactic)
                    {
                        var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                        await forbidden.WriteAsJsonAsync(new { error = "Access denied: tenant mismatch" });
                        return forbidden;
                    }
                }

                // Admin check
                var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userIdentifier);
                var isTenantAdmin = await _tenantAdminsService.IsTenantAdminAsync(tenantId, userIdentifier);
                if (!isGalacticAdmin && !isTenantAdmin)
                {
                    var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbidden.WriteAsJsonAsync(new { error = "Tenant admin access required" });
                    return forbidden;
                }

                var revoked = await _bootstrapService.RevokeAsync(tenantId, code);

                if (!revoked)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new { success = false, message = "Bootstrap session not found" });
                    return notFound;
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { success = true, message = "Bootstrap session revoked" });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error revoking bootstrap session {Code}", code);
                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteAsJsonAsync(new { error = "Failed to revoke bootstrap session" });
                return error;
            }
        }
    }
}
