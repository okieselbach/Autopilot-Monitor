using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions
{
    public class UpdateTenantConfigurationFunction
    {
        private readonly ILogger<UpdateTenantConfigurationFunction> _logger;
        private readonly TenantConfigurationService _configService;

        public UpdateTenantConfigurationFunction(
            ILogger<UpdateTenantConfigurationFunction> logger,
            TenantConfigurationService configService)
        {
            _logger = logger;
            _configService = configService;
        }

        [Function("UpdateTenantConfiguration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", "post", Route = "config/{tenantId}")] HttpRequestData req,
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
                    _logger.LogWarning($"Unauthorized update tenant configuration attempt for tenant {tenantId}: {ex.Message}");
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Authentication required. Please provide a valid JWT token."
                    });
                    return unauthorizedResponse;
                }

                // Validate tenant access: User can only update their own tenant's configuration
                if (authenticatedTenantId != tenantId)
                {
                    _logger.LogWarning($"User {userIdentifier} from tenant {authenticatedTenantId} attempted to update configuration for tenant {tenantId}");
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Access denied. You can only update your own tenant's configuration."
                    });
                    return forbiddenResponse;
                }

                _logger.LogInformation($"UpdateTenantConfiguration: {tenantId} by user {userIdentifier}");

                // Parse request body
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var config = JsonConvert.DeserializeObject<TenantConfiguration>(requestBody);

                if (config == null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { error = "Invalid configuration" });
                    return badRequest;
                }

                // Ensure tenant ID matches
                config.TenantId = tenantId;

                // Set the actual user identifier for audit logging
                config.UpdatedBy = userIdentifier;

                // Save configuration
                await _configService.SaveConfigurationAsync(config);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = "Configuration updated successfully",
                    config = config
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating configuration for tenant {tenantId}");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }
    }
}
