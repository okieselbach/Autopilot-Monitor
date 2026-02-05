using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
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
                _logger.LogInformation($"UpdateTenantConfiguration: {tenantId}");

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

                // TODO: Get current user from authentication context
                config.UpdatedBy = "Admin"; // Placeholder until authentication is implemented

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
