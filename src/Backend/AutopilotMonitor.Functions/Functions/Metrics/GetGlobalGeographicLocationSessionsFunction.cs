using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    public class GetGlobalGeographicLocationSessionsFunction
    {
        private readonly ILogger<GetGlobalGeographicLocationSessionsFunction> _logger;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly IMetricsRepository _metricsRepo;

        public GetGlobalGeographicLocationSessionsFunction(
            ILogger<GetGlobalGeographicLocationSessionsFunction> logger,
            IMaintenanceRepository maintenanceRepo,
            IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _maintenanceRepo = maintenanceRepo;
            _metricsRepo = metricsRepo;
        }

        [Function("GetGlobalGeographicLocationSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/geographic/sessions")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userEmail = TenantHelper.GetUserIdentifier(req);

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var locationKey = query["locationKey"];
                if (string.IsNullOrEmpty(locationKey))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "locationKey parameter is required" });
                    return badRequest;
                }

                var daysParam = query["days"];
                int days = 30;
                if (!string.IsNullOrEmpty(daysParam) && int.TryParse(daysParam, out var parsedDays) && parsedDays > 0)
                    days = parsedDays;

                var groupBy = query["groupBy"] ?? "city";

                _logger.LogInformation("Fetching global sessions for location '{LocationKey}' ({Days}d, groupBy={GroupBy}) (User: {UserEmail})",
                    locationKey, days, groupBy, userEmail);

                var cutoff = DateTime.UtcNow.AddDays(-days);
                var sessions = await _maintenanceRepo.GetSessionsByDateRangeAsync(cutoff, DateTime.UtcNow.AddDays(1));
                var filtered = GetGeographicLocationSessionsFunction.FilterSessionsByLocation(sessions, locationKey, groupBy);

                var appSummaries = await _metricsRepo.GetAllAppInstallSummariesAsync();
                var rows = GetGeographicLocationSessionsFunction.BuildRows(filtered, appSummaries);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { success = true, sessions = rows, totalCount = rows.Count });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching global geographic location sessions");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
