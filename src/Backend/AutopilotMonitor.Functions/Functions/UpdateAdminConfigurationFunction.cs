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
    public class UpdateAdminConfigurationFunction
    {
        private readonly ILogger<UpdateAdminConfigurationFunction> _logger;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly GalacticAdminService _galacticAdminService;

        public UpdateAdminConfigurationFunction(
            ILogger<UpdateAdminConfigurationFunction> logger,
            AdminConfigurationService adminConfigService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _adminConfigService = adminConfigService;
            _galacticAdminService = galacticAdminService;
        }

        [Function("UpdateAdminConfiguration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", "post", Route = "global/config")] HttpRequestData req)
        {
            try
            {
                // Validate authentication
                if (!TenantHelper.IsAuthenticated(req))
                {
                    _logger.LogWarning("Unauthenticated UpdateAdminConfiguration attempt");
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
                    _logger.LogWarning($"Non-Galactic Admin user {userIdentifier} from tenant {tenantId} attempted to update admin configuration");
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Access denied. Only Galactic Admins can modify global configuration."
                    });
                    return forbiddenResponse;
                }

                _logger.LogInformation($"UpdateAdminConfiguration by Galactic Admin user {userIdentifier}");

                // Parse request body
                if (req.Headers.TryGetValues("Content-Length", out var clValues)
                    && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                    && contentLength > 1_048_576) // 1 MB limit
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "Request body too large" });
                    return badRequest;
                }
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var config = JsonConvert.DeserializeObject<AdminConfiguration>(requestBody);

                if (config == null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { error = "Invalid configuration" });
                    return badRequest;
                }

                // Set the actual user identifier for audit logging
                config.UpdatedBy = userIdentifier;

                // Ensure PartitionKey and RowKey are correct
                config.PartitionKey = "GlobalConfig";
                config.RowKey = "config";

                // Save configuration
                await _adminConfigService.SaveConfigurationAsync(config);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = "Admin configuration updated successfully",
                    config = config
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating admin configuration");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }
    }
}
