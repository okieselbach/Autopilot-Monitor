using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Functions
{
    public class DeviceBlockFunction
    {
        private readonly ILogger<DeviceBlockFunction> _logger;
        private readonly BlockedDeviceService _blockedDeviceService;
        private readonly GalacticAdminService _galacticAdminService;

        public DeviceBlockFunction(
            ILogger<DeviceBlockFunction> logger,
            BlockedDeviceService blockedDeviceService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _blockedDeviceService = blockedDeviceService;
            _galacticAdminService = galacticAdminService;
        }

        /// <summary>GET /api/devices/blocked?tenantId={tenantId} — list all active blocks for a tenant</summary>
        [Function("GetBlockedDevices")]
        public async Task<HttpResponseData> GetBlockedDevices(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "devices/blocked")] HttpRequestData req)
        {
            try
            {
                if (!TenantHelper.IsAuthenticated(req))
                    return await UnauthorizedAsync(req);

                var userIdentifier = TenantHelper.GetUserIdentifier(req);
                if (!await _galacticAdminService.IsGalacticAdminAsync(userIdentifier))
                    return await ForbiddenAsync(req);

                var tenantId = req.Query["tenantId"];
                if (string.IsNullOrEmpty(tenantId))
                    return await BadRequestAsync(req, "tenantId query parameter is required");

                var blocked = await _blockedDeviceService.GetBlockedDevicesAsync(tenantId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { success = true, blocked });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting blocked devices");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        /// <summary>POST /api/devices/block — block a device temporarily</summary>
        [Function("BlockDevice")]
        public async Task<HttpResponseData> BlockDevice(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "devices/block")] HttpRequestData req)
        {
            try
            {
                if (!TenantHelper.IsAuthenticated(req))
                    return await UnauthorizedAsync(req);

                var userIdentifier = TenantHelper.GetUserIdentifier(req);
                if (!await _galacticAdminService.IsGalacticAdminAsync(userIdentifier))
                    return await ForbiddenAsync(req);

                string body;
                using (var reader = new System.IO.StreamReader(req.Body))
                    body = await reader.ReadToEndAsync();

                JObject json;
                try { json = JObject.Parse(body); }
                catch { return await BadRequestAsync(req, "Invalid JSON body"); }

                var tenantId = json["tenantId"]?.ToString();
                var serialNumber = json["serialNumber"]?.ToString();
                var durationHours = json["durationHours"]?.Value<int>() ?? 12;
                var reason = json["reason"]?.ToString();

                if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(serialNumber))
                    return await BadRequestAsync(req, "tenantId and serialNumber are required");

                if (durationHours < 1 || durationHours > 720) // max 30 days
                    return await BadRequestAsync(req, "durationHours must be between 1 and 720");

                await _blockedDeviceService.BlockDeviceAsync(tenantId, serialNumber, durationHours, userIdentifier, reason);

                _logger.LogWarning(
                    "Galactic Admin {User} blocked device {SerialNumber} in tenant {TenantId} for {Hours}h. Reason: {Reason}",
                    userIdentifier, serialNumber, tenantId, durationHours, reason);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = $"Device {serialNumber} blocked for {durationHours} hours.",
                    unblockAt = DateTime.UtcNow.AddHours(durationHours)
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error blocking device");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        /// <summary>DELETE /api/devices/block/{encodedSerialNumber}?tenantId={tenantId} — unblock a device</summary>
        [Function("UnblockDevice")]
        public async Task<HttpResponseData> UnblockDevice(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "devices/block/{encodedSerialNumber}")] HttpRequestData req,
            string encodedSerialNumber)
        {
            try
            {
                if (!TenantHelper.IsAuthenticated(req))
                    return await UnauthorizedAsync(req);

                var userIdentifier = TenantHelper.GetUserIdentifier(req);
                if (!await _galacticAdminService.IsGalacticAdminAsync(userIdentifier))
                    return await ForbiddenAsync(req);

                var tenantId = req.Query["tenantId"];
                if (string.IsNullOrEmpty(tenantId))
                    return await BadRequestAsync(req, "tenantId query parameter is required");

                var serialNumber = Uri.UnescapeDataString(encodedSerialNumber ?? string.Empty);
                if (string.IsNullOrEmpty(serialNumber))
                    return await BadRequestAsync(req, "serialNumber is required");

                await _blockedDeviceService.UnblockDeviceAsync(tenantId, serialNumber);

                _logger.LogInformation(
                    "Galactic Admin {User} unblocked device {SerialNumber} in tenant {TenantId}",
                    userIdentifier, serialNumber, tenantId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { success = true, message = $"Device {serialNumber} unblocked." });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unblocking device");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static async Task<HttpResponseData> UnauthorizedAsync(HttpRequestData req)
        {
            var r = req.CreateResponse(HttpStatusCode.Unauthorized);
            await r.WriteAsJsonAsync(new { success = false, message = "Authentication required." });
            return r;
        }

        private static async Task<HttpResponseData> ForbiddenAsync(HttpRequestData req)
        {
            var r = req.CreateResponse(HttpStatusCode.Forbidden);
            await r.WriteAsJsonAsync(new { success = false, message = "Access denied. Only Galactic Admins can manage device blocks." });
            return r;
        }

        private static async Task<HttpResponseData> BadRequestAsync(HttpRequestData req, string message)
        {
            var r = req.CreateResponse(HttpStatusCode.BadRequest);
            await r.WriteAsJsonAsync(new { success = false, message });
            return r;
        }
    }
}
