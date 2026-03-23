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
    public class GetAdminConfigurationFunction
    {
        private readonly ILogger<GetAdminConfigurationFunction> _logger;
        private readonly AdminConfigurationService _adminConfigService;

        public GetAdminConfigurationFunction(
            ILogger<GetAdminConfigurationFunction> logger,
            AdminConfigurationService adminConfigService)
        {
            _logger = logger;
            _adminConfigService = adminConfigService;
        }

        [Function("GetAdminConfiguration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/config")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                string userIdentifier = TenantHelper.GetUserIdentifier(req);

                _logger.LogInformation($"GetAdminConfiguration by Global Admin user {userIdentifier}");

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
