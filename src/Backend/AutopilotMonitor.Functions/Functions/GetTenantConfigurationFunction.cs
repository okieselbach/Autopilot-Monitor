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
    public class GetTenantConfigurationFunction
    {
        private readonly ILogger<GetTenantConfigurationFunction> _logger;
        private readonly TenantConfigurationService _configService;

        public GetTenantConfigurationFunction(
            ILogger<GetTenantConfigurationFunction> logger,
            TenantConfigurationService configService)
        {
            _logger = logger;
            _configService = configService;
        }

        [Function("GetTenantConfiguration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/{tenantId}")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // Get tenant ID from JWT token and validate access
                string authenticatedTenantId;
                string userIdentifier;
                try
                {
                    authenticatedTenantId = TenantHelper.GetTenantId(req);
                    userIdentifier = TenantHelper.GetUserIdentifier(req);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning($"Unauthorized get tenant configuration attempt for tenant {tenantId}: {ex.Message}");
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Authentication required. Please provide a valid JWT token."
                    });
                    return unauthorizedResponse;
                }

                // Validate tenant access: User can only access their own tenant's configuration
                if (authenticatedTenantId != tenantId)
                {
                    _logger.LogWarning($"User {userIdentifier} from tenant {authenticatedTenantId} attempted to access configuration for tenant {tenantId}");
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Access denied. You can only access your own tenant's configuration."
                    });
                    return forbiddenResponse;
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
