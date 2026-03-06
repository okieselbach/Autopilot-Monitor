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
    public class GetAllBlockedDevicesFunction
    {
        private readonly ILogger<GetAllBlockedDevicesFunction> _logger;
        private readonly BlockedDeviceService _blockedDeviceService;
        private readonly GalacticAdminService _galacticAdminService;

        public GetAllBlockedDevicesFunction(
            ILogger<GetAllBlockedDevicesFunction> logger,
            BlockedDeviceService blockedDeviceService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _blockedDeviceService = blockedDeviceService;
            _galacticAdminService = galacticAdminService;
        }

        /// <summary>GET /api/galactic/devices/blocked — list all active blocks across all tenants</summary>
        [Function("GetAllBlockedDevices")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/devices/blocked")] HttpRequestData req)
        {
            _logger.LogInformation("GetAllBlockedDevices function processing request (Galactic Admin Mode)");

            try
            {
                if (!TenantHelper.IsAuthenticated(req))
                {
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new { success = false, message = "Authentication required." });
                    return unauthorizedResponse;
                }

                var userEmail = TenantHelper.GetUserIdentifier(req);
                if (!await _galacticAdminService.IsGalacticAdminAsync(userEmail))
                {
                    _logger.LogWarning("Non-Galactic Admin user {User} attempted to access GetAllBlockedDevices", userEmail);
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new { success = false, message = "Access denied. Galactic Admin role required." });
                    return forbiddenResponse;
                }

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
