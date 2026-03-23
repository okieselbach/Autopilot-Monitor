using System.Net;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Function for retrieving platform agent metrics (Galactic Admin only).
    /// Returns per-session CPU, memory, network metrics with 5-minute backend cache.
    /// </summary>
    public class GetGalacticPlatformMetricsFunction
    {
        private readonly ILogger<GetGalacticPlatformMetricsFunction> _logger;
        private readonly PlatformMetricsService _platformMetricsService;

        public GetGalacticPlatformMetricsFunction(
            ILogger<GetGalacticPlatformMetricsFunction> logger,
            PlatformMetricsService platformMetricsService)
        {
            _logger = logger;
            _platformMetricsService = platformMetricsService;
        }

        [Function("GetGalacticPlatformMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/metrics/platform")] HttpRequestData req)
        {
            _logger.LogInformation("Platform agent metrics requested");

            try
            {
                // Authentication + GalacticAdminOnly authorization enforced by PolicyEnforcementMiddleware

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
