using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// Function for retrieving tenant-specific usage metrics (Tenant Admin)
    /// </summary>
    public class UsageMetricsFunction
    {
        private readonly ILogger<UsageMetricsFunction> _logger;
        private readonly UsageMetricsService _usageMetricsService;

        public UsageMetricsFunction(
            ILogger<UsageMetricsFunction> logger,
            UsageMetricsService usageMetricsService)
        {
            _logger = logger;
            _usageMetricsService = usageMetricsService;
        }

        /// <summary>
        /// GET /api/usage-metrics?tenantId={tenantId} - Compute and return tenant-specific usage metrics
        /// On-demand computation with 5-minute cache (Tenant Admin)
        /// </summary>
        [Function("GetTenantUsageMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "usage-metrics")]
            HttpRequestData req)
        {
            _logger.LogInformation("Tenant usage metrics requested");

            try
            {
                // Validate authentication
                if (!TenantHelper.IsAuthenticated(req))
                {
                    _logger.LogWarning("Unauthenticated UsageMetrics attempt");
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Authentication required. Please provide a valid JWT token."
                    });
                    return unauthorizedResponse;
                }

                string tenantId = TenantHelper.GetTenantId(req);
                string userIdentifier = TenantHelper.GetUserIdentifier(req);

                _logger.LogInformation($"Fetching usage metrics for tenant {tenantId} by user {userIdentifier}");

                var metrics = await _usageMetricsService.ComputeTenantUsageMetricsAsync(tenantId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(metrics);

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing tenant usage metrics");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Failed to compute tenant usage metrics",
                    error = ex.Message
                });

                return errorResponse;
            }
        }
    }
}
