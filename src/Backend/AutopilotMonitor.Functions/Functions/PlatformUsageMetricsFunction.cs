using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// Function for retrieving platform usage metrics (Galactic Admin only)
    /// </summary>
    public class PlatformUsageMetricsFunction
    {
        private readonly ILogger<PlatformUsageMetricsFunction> _logger;
        private readonly UsageMetricsService _usageMetricsService;
        private readonly GalacticAdminService _galacticAdminService;

        public PlatformUsageMetricsFunction(
            ILogger<PlatformUsageMetricsFunction> logger,
            UsageMetricsService usageMetricsService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _usageMetricsService = usageMetricsService;
            _galacticAdminService = galacticAdminService;
        }

        /// <summary>
        /// GET /api/platform/usage-metrics - Compute and return platform usage metrics
        /// On-demand computation with 5-minute cache (Galactic Admin only)
        /// </summary>
        [Function("GetPlatformUsageMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "platform/usage-metrics")]
            HttpRequestData req)
        {
            _logger.LogInformation("Platform usage metrics requested");

            try
            {
                // Validate authentication
                if (!TenantHelper.IsAuthenticated(req))
                {
                    _logger.LogWarning("Unauthenticated platform usage metrics attempt");
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Authentication required. Please provide a valid JWT token."
                    });
                    return unauthorizedResponse;
                }

                // Check if user is Galactic Admin via GalacticAdminService (Azure Table Storage)
                var userEmail = TenantHelper.GetUserIdentifier(req);
                var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userEmail);

                if (!isGalacticAdmin)
                {
                    _logger.LogWarning($"Non-Galactic Admin user {userEmail} attempted to access platform usage metrics");
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Access denied. Galactic Admin role required."
                    });
                    return forbiddenResponse;
                }

                _logger.LogInformation($"Platform usage metrics accessed by Galactic Admin: {userEmail}");

                var metrics = await _usageMetricsService.ComputeUsageMetricsAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(metrics);

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing usage metrics");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Failed to compute usage metrics"
                });

                return errorResponse;
            }
        }
    }
}
