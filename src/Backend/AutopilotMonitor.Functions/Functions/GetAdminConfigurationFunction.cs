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
    public class GetAdminConfigurationFunction
    {
        private readonly ILogger<GetAdminConfigurationFunction> _logger;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly GalacticAdminService _galacticAdminService;

        public GetAdminConfigurationFunction(
            ILogger<GetAdminConfigurationFunction> logger,
            AdminConfigurationService adminConfigService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _adminConfigService = adminConfigService;
            _galacticAdminService = galacticAdminService;
        }

        [Function("GetAdminConfiguration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/config")] HttpRequestData req)
        {
            try
            {
                // Validate authentication
                if (!TenantHelper.IsAuthenticated(req))
                {
                    _logger.LogWarning("Unauthenticated GetAdminConfiguration attempt");
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Authentication required. Please provide a valid JWT token."
                    });
                    return unauthorizedResponse;
                }

                // Get tenant ID and user identifier from JWT token
                string tenantId = TenantHelper.GetTenantId(req);
                string userIdentifier = TenantHelper.GetUserIdentifier(req);

                // Validate Galactic Admin role
                if (!await _galacticAdminService.IsGalacticAdminAsync(userIdentifier))
                {
                    _logger.LogWarning($"Non-Galactic Admin user {userIdentifier} from tenant {tenantId} attempted to access admin configuration");
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Access denied. Only Galactic Admins can access global configuration."
                    });
                    return forbiddenResponse;
                }

                _logger.LogInformation($"GetAdminConfiguration by Galactic Admin user {userIdentifier}");

                var config = await _adminConfigService.GetConfigurationAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(config);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting admin configuration");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }
    }
}
