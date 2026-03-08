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
    public class GetAllTenantConfigurationsFunction
    {
        private readonly ILogger<GetAllTenantConfigurationsFunction> _logger;
        private readonly TenantConfigurationService _configService;

        public GetAllTenantConfigurationsFunction(
            ILogger<GetAllTenantConfigurationsFunction> logger,
            TenantConfigurationService configService)
        {
            _logger = logger;
            _configService = configService;
        }

        [Function("GetAllTenantConfigurations")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/all")] HttpRequestData req)
        {
            try
            {
                // Authentication + GalacticAdminOnly authorization enforced by PolicyEnforcementMiddleware
                string userIdentifier = TenantHelper.GetUserIdentifier(req);

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
