using System.Net;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
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
        /// GET /api/galactic/metrics/usage - Compute and return platform usage metrics
        /// On-demand computation with 5-minute cache (Galactic Admin only)
        /// </summary>
        [Function("GetPlatformUsageMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/metrics/usage")]
            HttpRequestData req)
        {
            _logger.LogInformation("Platform usage metrics requested");

            try
            {
                // Authentication + GalacticAdminOnly authorization enforced by PolicyEnforcementMiddleware

                // Optional tenantId query parameter: when provided, return tenant-specific metrics
                var tenantId = req.Url.Query?.Contains("tenantId=") == true
                    ? System.Web.HttpUtility.ParseQueryString(req.Url.Query).Get("tenantId")
                    : null;

                var metrics = !string.IsNullOrEmpty(tenantId)
                    ? await _usageMetricsService.ComputeTenantUsageMetricsAsync(tenantId)
                    : await _usageMetricsService.ComputeUsageMetricsAsync();

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
