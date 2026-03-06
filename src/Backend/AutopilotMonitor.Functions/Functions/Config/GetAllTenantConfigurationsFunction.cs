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
    public class GetAllTenantConfigurationsFunction
    {
        private readonly ILogger<GetAllTenantConfigurationsFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly GalacticAdminService _galacticAdminService;

        public GetAllTenantConfigurationsFunction(
            ILogger<GetAllTenantConfigurationsFunction> logger,
            TenantConfigurationService configService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _configService = configService;
            _galacticAdminService = galacticAdminService;
        }

        [Function("GetAllTenantConfigurations")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/all")] HttpRequestData req)
        {
            try
            {
                // Validate authentication
                if (!TenantHelper.IsAuthenticated(req))
                {
                    _logger.LogWarning("Unauthenticated GetAllTenantConfigurations attempt");
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Authentication required. Please provide a valid JWT token."
                    });
                    return unauthorizedResponse;
                }

                // Get user identifier
                string userIdentifier = TenantHelper.GetUserIdentifier(req);

                // Check if user is Galactic Admin
                bool isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userIdentifier);

                if (!isGalacticAdmin)
                {
                    _logger.LogWarning($"User {userIdentifier} attempted to access all tenant configurations without Galactic Admin privileges");
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Access denied. This endpoint requires Galactic Admin privileges."
                    });
                    return forbiddenResponse;
                }

                _logger.LogInformation($"GetAllTenantConfigurations by Galactic Admin {userIdentifier}");

                var configurations = await _configService.GetAllConfigurationsAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(configurations);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all tenant configurations");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }
    }
}
