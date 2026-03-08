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
    public class GetTenantConfigurationFunction
    {
        private readonly ILogger<GetTenantConfigurationFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly GalacticAdminService _galacticAdminService;

        public GetTenantConfigurationFunction(
            ILogger<GetTenantConfigurationFunction> logger,
            TenantConfigurationService configService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _configService = configService;
            _galacticAdminService = galacticAdminService;
        }

        [Function("GetTenantConfiguration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/{tenantId}")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                string authenticatedTenantId = TenantHelper.GetTenantId(req);
                string userIdentifier = TenantHelper.GetUserIdentifier(req);

                // Validate tenant access: cross-tenant only for Galactic Admins
                if (!string.Equals(authenticatedTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                {
                    var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userIdentifier);
                    if (!isGalacticAdmin)
                    {
                        _logger.LogWarning("User {User} from tenant {AuthTenant} attempted to access configuration for tenant {TargetTenant}",
                            userIdentifier, authenticatedTenantId, tenantId);
                        var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                        await forbiddenResponse.WriteAsJsonAsync(new
                        {
                            success = false,
                            message = "Access denied. You can only access your own tenant's configuration."
                        });
                        return forbiddenResponse;
                    }
                }

                _logger.LogInformation($"GetTenantConfiguration: {tenantId} by user {userIdentifier}");

                var config = await _configService.GetConfigurationAsync(tenantId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(config);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting configuration for tenant {tenantId}");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }
    }
}
