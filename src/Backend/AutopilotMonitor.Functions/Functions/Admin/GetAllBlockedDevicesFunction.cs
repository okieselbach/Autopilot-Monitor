using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    public class GetAllBlockedDevicesFunction
    {
        private readonly ILogger<GetAllBlockedDevicesFunction> _logger;
        private readonly BlockedDeviceService _blockedDeviceService;

        public GetAllBlockedDevicesFunction(
            ILogger<GetAllBlockedDevicesFunction> logger,
            BlockedDeviceService blockedDeviceService)
        {
            _logger = logger;
            _blockedDeviceService = blockedDeviceService;
        }

        /// <summary>GET /api/galactic/devices/blocked — list all active blocks across all tenants</summary>
        [Function("GetAllBlockedDevices")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/devices/blocked")] HttpRequestData req)
        {
            _logger.LogInformation("GetAllBlockedDevices function processing request (Galactic Admin Mode)");

            try
            {
                // Authentication + GalacticAdminOnly authorization enforced by PolicyEnforcementMiddleware

                var blocked = await _blockedDeviceService.GetAllBlockedDevicesAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { success = true, blocked });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all blocked devices");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
