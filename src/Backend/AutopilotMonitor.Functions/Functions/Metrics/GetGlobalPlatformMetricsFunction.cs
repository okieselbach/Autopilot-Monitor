using System.Net;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Function for retrieving platform agent metrics (Global Admin only).
    /// Returns per-session CPU, memory, network metrics with 5-minute backend cache.
    /// </summary>
    public class GetGlobalPlatformMetricsFunction
    {
        private readonly ILogger<GetGlobalPlatformMetricsFunction> _logger;
        private readonly PlatformMetricsService _platformMetricsService;

        public GetGlobalPlatformMetricsFunction(
            ILogger<GetGlobalPlatformMetricsFunction> logger,
            PlatformMetricsService platformMetricsService)
        {
            _logger = logger;
            _platformMetricsService = platformMetricsService;
        }

        [Function("GetGlobalPlatformMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/platform")] HttpRequestData req)
        {
            _logger.LogInformation("Platform agent metrics requested");

            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware

                var days = ParseDays(req);
                var metrics = await _platformMetricsService.ComputePlatformMetricsAsync(days);

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

        private static int ParseDays(HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var raw = query["days"];
            var days = 90;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var parsed) && parsed > 0)
                days = parsed;
            if (days < 1) days = 1;
            if (days > 365) days = 365;
            return days;
        }
    }
}
