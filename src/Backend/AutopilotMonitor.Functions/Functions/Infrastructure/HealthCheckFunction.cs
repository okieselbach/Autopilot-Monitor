using System.Net;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Infrastructure
{
    public class HealthCheckFunction
    {
        private readonly ILogger<HealthCheckFunction> _logger;
        private readonly HealthCheckService _healthCheckService;

        public HealthCheckFunction(
            ILogger<HealthCheckFunction> logger,
            HealthCheckService healthCheckService)
        {
            _logger = logger;
            _healthCheckService = healthCheckService;
        }

        /// <summary>
        /// GET /api/health
        /// Basic health check endpoint (anonymous access)
        /// </summary>
        [Function("HealthCheck")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
        {
            _logger.LogInformation("Basic health check requested");

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                status = "healthy",
                service = "Autopilot Monitor API",
                version = "1.0.0", // TODO: Version should be dynamically retrieved from assembly info or configuration
                timestamp = DateTime.UtcNow
            });

            return response;
        }

        /// <summary>
        /// GET /api/health/detailed
        /// Detailed health check with comprehensive system checks (Galactic Admin only)
        /// </summary>
        [Function("DetailedHealthCheck")]
        public async Task<HttpResponseData> GetDetailedHealthCheck(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/detailed")] HttpRequestData req,
            FunctionContext context)
        {
            _logger.LogInformation("Detailed health check requested");

            // Authentication + GalacticAdminOnly authorization enforced by PolicyEnforcementMiddleware

            // Perform comprehensive health checks
            var healthCheckResult = await _healthCheckService.PerformAllChecksAsync();

            // Always return 200 OK with the health status in the body
            // This allows the frontend to properly display the results even if some checks fail
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                service = "Autopilot Monitor API",
                version = "1.0.0-phase1",
                timestamp = healthCheckResult.Timestamp,
                overallStatus = healthCheckResult.OverallStatus,
                checks = healthCheckResult.Checks
            });

            return response;
        }
    }
}
