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
        private readonly TenantConfigurationService _configService;

        public ListBootstrapSessionsFunction(
            ILogger<ListBootstrapSessionsFunction> logger,
            BootstrapSessionService bootstrapService,
            TenantConfigurationService configService)
        {
            _logger = logger;
            _bootstrapService = bootstrapService;
            _configService = configService;
        }

        [Function("ListBootstrapSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bootstrap/sessions")] HttpRequestData req)
        {
            try
            {
                // Authentication + BootstrapManagerOrGA authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                var authenticatedTenantId = requestCtx.TenantId;
                var userIdentifier = requestCtx.UserPrincipalName;

                var tenantId = req.Query["tenantId"] ?? authenticatedTenantId;

                // Cross-tenant boundary check — only Global Admins may operate on other tenants
                if (!requestCtx.IsGlobalAdmin && !string.Equals(authenticatedTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                {
                    var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbidden.WriteAsJsonAsync(new { error = "Access denied: tenant mismatch" });
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
