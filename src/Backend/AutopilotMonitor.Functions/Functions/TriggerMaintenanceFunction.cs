using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// Allows Galactic Admins to manually trigger maintenance tasks
    /// </summary>
    public class TriggerMaintenanceFunction
    {
        private readonly ILogger<TriggerMaintenanceFunction> _logger;
        private readonly DailyMaintenanceFunction _maintenanceFunction;
        private readonly GalacticAdminService _galacticAdminService;

        public TriggerMaintenanceFunction(
            ILogger<TriggerMaintenanceFunction> logger,
            DailyMaintenanceFunction maintenanceFunction,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _maintenanceFunction = maintenanceFunction;
            _galacticAdminService = galacticAdminService;
        }

        /// <summary>
        /// POST /api/maintenance/trigger
        /// Manually trigger maintenance tasks (Galactic Admin only)
        /// Query parameters:
        /// - date: Optional date to aggregate (yyyy-MM-dd). If not provided, uses yesterday.
        /// - aggregateOnly: If true, only runs aggregation (skips timeout and cleanup)
        /// </summary>
        [Function("TriggerMaintenance")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "maintenance/trigger")] HttpRequestData req,
            FunctionContext context)
        {
            _logger.LogInformation("Manual maintenance trigger requested");

            // Validate authentication
            if (!TenantHelper.IsAuthenticated(req))
            {
                _logger.LogWarning("Unauthenticated maintenance trigger attempt");
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
                _logger.LogWarning($"Non-Galactic Admin user {userEmail} attempted to trigger maintenance");
                var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                await forbiddenResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Access denied. Galactic Admin role required."
                });
                return forbiddenResponse;
            }

            _logger.LogInformation($"Maintenance trigger initiated by Galactic Admin: {userEmail}");

            try
            {
                // Parse query parameters
                var dateParam = req.Query["date"];
                var aggregateOnlyParam = req.Query["aggregateOnly"];

                DateTime? targetDate = null;
                if (!string.IsNullOrEmpty(dateParam))
                {
                    if (DateTime.TryParse(dateParam, out var parsedDate))
                    {
                        targetDate = parsedDate.Date;
                        _logger.LogInformation($"Manual maintenance for date: {targetDate:yyyy-MM-dd}");
                    }
                    else
                    {
                        var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                        await badRequest.WriteAsJsonAsync(new { error = "Invalid date format. Use yyyy-MM-dd" });
                        return badRequest;
                    }
                }

                bool aggregateOnly = aggregateOnlyParam?.ToLower() == "true";

                // Execute maintenance tasks
                var result = await _maintenanceFunction.RunManualAsync(targetDate, aggregateOnly, userEmail);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = "Maintenance tasks completed",
                    result = result,
                    triggeredBy = userEmail,
                    triggeredAt = DateTime.UtcNow
                });

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering maintenance");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    error = ex.Message
                });

                return errorResponse;
            }
        }
    }
}
