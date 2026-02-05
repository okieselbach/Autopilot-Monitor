using System;
using System.Net;
using System.Threading.Tasks;
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
                _logger.LogInformation($"GetTenantConfiguration: {tenantId}");

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
