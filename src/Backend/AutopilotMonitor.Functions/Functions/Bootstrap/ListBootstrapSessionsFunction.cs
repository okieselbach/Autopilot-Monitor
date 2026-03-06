using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Bootstrap
{
    /// <summary>
    /// GET /api/bootstrap/sessions?tenantId={tenantId} — List bootstrap sessions for a tenant.
    /// Requires JWT authentication and TenantAdmin role.
    /// </summary>
    public class ListBootstrapSessionsFunction
    {
        private readonly ILogger<ListBootstrapSessionsFunction> _logger;
        private readonly BootstrapSessionService _bootstrapService;
        private readonly GalacticAdminService _galacticAdminService;
        private readonly TenantAdminsService _tenantAdminsService;
        private readonly TenantConfigurationService _configService;

        public ListBootstrapSessionsFunction(
            ILogger<ListBootstrapSessionsFunction> logger,
            BootstrapSessionService bootstrapService,
            GalacticAdminService galacticAdminService,
            TenantAdminsService tenantAdminsService,
            TenantConfigurationService configService)
        {
            _logger = logger;
            _bootstrapService = bootstrapService;
            _galacticAdminService = galacticAdminService;
            _tenantAdminsService = tenantAdminsService;
            _configService = configService;
        }

        [Function("ListBootstrapSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bootstrap/sessions")] HttpRequestData req)
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

                // Check if bootstrap token feature is enabled for this tenant
                var tenantConfig = await _configService.GetConfigurationAsync(tenantId);
                if (!tenantConfig.BootstrapTokenEnabled)
                {
                    // Return empty list instead of error — feature simply not visible
                    var emptyResponse = req.CreateResponse(HttpStatusCode.OK);
                    await emptyResponse.WriteAsJsonAsync(new ListBootstrapSessionsResponse { Success = true, Sessions = new System.Collections.Generic.List<BootstrapSessionListItem>() });
                    return emptyResponse;
                }

                var sessions = await _bootstrapService.ListAsync(tenantId);
                var now = DateTime.UtcNow;

                var responseData = new ListBootstrapSessionsResponse
                {
                    Success = true,
                    Sessions = sessions.Select(s => new BootstrapSessionListItem
                    {
                        ShortCode = s.ShortCode,
                        Label = s.Label,
                        CreatedAt = s.CreatedAt,
                        ExpiresAt = s.ExpiresAt,
                        CreatedByUpn = s.CreatedByUpn,
                        IsRevoked = s.IsRevoked,
                        IsExpired = s.ExpiresAt < now,
                        UsageCount = s.UsageCount
                    }).ToList()
                };

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(responseData);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing bootstrap sessions");
                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteAsJsonAsync(new { error = "Failed to list bootstrap sessions" });
                return error;
            }
        }
    }
}
