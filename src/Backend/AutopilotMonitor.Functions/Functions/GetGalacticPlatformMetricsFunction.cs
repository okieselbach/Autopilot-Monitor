using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// Function for retrieving platform agent metrics (Galactic Admin only).
    /// Returns per-session CPU, memory, network metrics with 5-minute backend cache.
    /// </summary>
    public class GetGalacticPlatformMetricsFunction
    {
        private readonly ILogger<GetGalacticPlatformMetricsFunction> _logger;
        private readonly PlatformMetricsService _platformMetricsService;
        private readonly GalacticAdminService _galacticAdminService;

        public GetGalacticPlatformMetricsFunction(
            ILogger<GetGalacticPlatformMetricsFunction> logger,
            PlatformMetricsService platformMetricsService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _platformMetricsService = platformMetricsService;
            _galacticAdminService = galacticAdminService;
        }

        [Function("GetGalacticPlatformMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/metrics/platform")] HttpRequestData req)
        {
            _logger.LogInformation("Platform agent metrics requested");

            try
            {
                // Validate authentication
                if (!TenantHelper.IsAuthenticated(req))
                {
                    _logger.LogWarning("Unauthenticated platform metrics attempt");
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Authentication required. Please provide a valid JWT token."
                    });
                    return unauthorizedResponse;
                }

                // Check if user is Galactic Admin
                var userEmail = TenantHelper.GetUserIdentifier(req);
                var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userEmail);

                if (!isGalacticAdmin)
                {
                    _logger.LogWarning($"Non-Galactic Admin user {userEmail} attempted to access platform metrics");
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Access denied. Galactic Admin role required."
                    });
                    return forbiddenResponse;
                }

                _logger.LogInformation($"Platform agent metrics accessed by Galactic Admin: {userEmail}");

                var metrics = await _platformMetricsService.ComputePlatformMetricsAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(metrics);

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing platform agent metrics");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Failed to compute platform agent metrics"
                });

                return errorResponse;
            }
        }
    }
}
