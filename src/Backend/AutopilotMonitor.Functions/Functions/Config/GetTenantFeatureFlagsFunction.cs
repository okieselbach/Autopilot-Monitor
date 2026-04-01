using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    public class GetTenantFeatureFlagsFunction
    {
        private readonly ILogger<GetTenantFeatureFlagsFunction> _logger;
        private readonly TenantConfigurationService _configService;

        public GetTenantFeatureFlagsFunction(
            ILogger<GetTenantFeatureFlagsFunction> logger,
            TenantConfigurationService configService)
        {
            _logger = logger;
            _configService = configService;
        }

        [Function("GetTenantFeatureFlags")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/{tenantId}/feature-flags")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                var userIdentifier = requestCtx.UserPrincipalName;

                // Validate tenant access: cross-tenant only for Global Admins
                if (!requestCtx.IsGlobalAdmin && !string.Equals(requestCtx.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("User {User} from tenant {AuthTenant} attempted to access feature flags for tenant {TargetTenant}",
                        userIdentifier, requestCtx.TenantId, tenantId);
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Access denied. You can only access your own tenant's feature flags."
                    });
                    return forbiddenResponse;
                }

                var config = await _configService.GetConfigurationAsync(tenantId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    bootstrapTokenEnabled = config.BootstrapTokenEnabled
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting feature flags for tenant {TenantId}", tenantId);
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }
    }
}
