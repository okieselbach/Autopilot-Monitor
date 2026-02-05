using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    public class HealthCheckFunction
    {
        private readonly ILogger<HealthCheckFunction> _logger;
        private readonly HealthCheckService _healthCheckService;
        private readonly GalacticAdminService _galacticAdminService;

        public HealthCheckFunction(
            ILogger<HealthCheckFunction> logger,
            HealthCheckService healthCheckService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _healthCheckService = healthCheckService;
            _galacticAdminService = galacticAdminService;
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
                version = "1.0.0-phase1",
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

            // Validate authentication
            var httpContext = req.FunctionContext.GetHttpContext();
            if (httpContext?.User?.Identity?.IsAuthenticated != true)
            {
                _logger.LogWarning("Unauthenticated detailed health check attempt");
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
                _logger.LogWarning($"Non-Galactic Admin user {userEmail} attempted to access detailed health check");
                var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                await forbiddenResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Access denied. Galactic Admin role required."
                });
                return forbiddenResponse;
            }

            _logger.LogInformation($"Detailed health check accessed by Galactic Admin: {userEmail}");

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
