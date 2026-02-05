using System.Net;
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

        public PlatformUsageMetricsFunction(
            ILogger<PlatformUsageMetricsFunction> logger,
            UsageMetricsService usageMetricsService)
        {
            _logger = logger;
            _usageMetricsService = usageMetricsService;
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
            _logger.LogInformation("Usage metrics requested");

            try
            {
                // TODO: Add Entra ID authentication check here (Galactic Admin role required)
                // When Entra ID is implemented:
                // 1. Extract JWT token from Authorization header
                // 2. Validate token with Azure AD
                // 3. Check user has "GalacticAdmin" role
                // 4. Return 403 Forbidden if not authorized
                //
                // For now, anyone can access (will be restricted with Entra ID later)

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
                    message = "Failed to compute usage metrics",
                    error = ex.Message
                });

                return errorResponse;
            }
        }
    }
}
