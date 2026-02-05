using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Services;
using Microsoft.AspNetCore.Authorization;
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
        [Authorize]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "maintenance/trigger")] HttpRequestData req,
            FunctionContext context)
        {
            _logger.LogInformation("Manual maintenance trigger requested");

            // Check if user is galactic admin
            var principal = context.GetUser();
            if (principal == null)
            {
                return req.CreateResponse(HttpStatusCode.Unauthorized);
            }

            var upn = principal.GetUserPrincipalName();
            var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(upn);

            if (!isGalacticAdmin)
            {
                var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                await forbiddenResponse.WriteAsJsonAsync(new { error = "Only Galactic Admins can trigger maintenance tasks" });
                return forbiddenResponse;
            }

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
                var result = await _maintenanceFunction.RunManualAsync(targetDate, aggregateOnly, upn);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = "Maintenance tasks completed",
                    result = result,
                    triggeredBy = upn,
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
