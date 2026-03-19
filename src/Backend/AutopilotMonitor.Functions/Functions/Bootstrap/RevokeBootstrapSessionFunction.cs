using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Bootstrap
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
        private readonly TenantConfigurationService _configService;
        private readonly TableStorageService _storageService;

        public RevokeBootstrapSessionFunction(
            ILogger<RevokeBootstrapSessionFunction> logger,
            BootstrapSessionService bootstrapService,
            GalacticAdminService galacticAdminService,
            TenantConfigurationService configService,
            TableStorageService storageService)
        {
            _logger = logger;
            _bootstrapService = bootstrapService;
            _galacticAdminService = galacticAdminService;
            _configService = configService;
            _storageService = storageService;
        }

        [Function("RevokeBootstrapSession")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "bootstrap/sessions/{code}")] HttpRequestData req,
            string code)
        {
            try
            {
                // Authentication + BootstrapManagerOrGA authorization enforced by PolicyEnforcementMiddleware
                var authenticatedTenantId = TenantHelper.GetTenantId(req);
                var userIdentifier = TenantHelper.GetUserIdentifier(req);

                var tenantId = req.Query["tenantId"] ?? authenticatedTenantId;

                // Cross-tenant boundary check — only Galactic Admins may operate on other tenants
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

                // Check if bootstrap token feature is enabled for this tenant
                var tenantConfig = await _configService.GetConfigurationAsync(tenantId);
                if (!tenantConfig.BootstrapTokenEnabled)
                {
                    var disabled = req.CreateResponse(HttpStatusCode.Forbidden);
                    await disabled.WriteAsJsonAsync(new { error = "Bootstrap token feature is not enabled for this tenant" });
                    return disabled;
                }

                var revoked = await _bootstrapService.RevokeAsync(tenantId, code);

                if (!revoked)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new { success = false, message = "Bootstrap session not found" });
                    return notFound;
                }

                await _storageService.LogAuditEntryAsync(
                    tenantId,
                    "DELETE",
                    "BootstrapSession",
                    code,
                    userIdentifier
                );

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
