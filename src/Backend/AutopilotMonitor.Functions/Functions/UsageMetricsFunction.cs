using System.Net;
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
                // Get tenant ID from query parameter
                var tenantId = req.Query["tenantId"];

                if (string.IsNullOrEmpty(tenantId))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "tenantId query parameter is required"
                    });
                    return badRequest;
                }

                // TODO: Add Entra ID authentication check here (Tenant Admin role required)
                // When Entra ID is implemented:
                // 1. Extract JWT token from Authorization header
                // 2. Validate token with Azure AD
                // 3. Check user has "TenantAdmin" role for the requested tenantId
                // 4. Return 403 Forbidden if not authorized
                //
                // For now, anyone can access (will be restricted with Entra ID later)

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
