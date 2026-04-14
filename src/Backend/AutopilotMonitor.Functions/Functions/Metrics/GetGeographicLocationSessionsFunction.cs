using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    public class GetGeographicLocationSessionsFunction
    {
        private readonly ILogger<GetGeographicLocationSessionsFunction> _logger;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly IMetricsRepository _metricsRepo;

        public GetGeographicLocationSessionsFunction(
            ILogger<GetGeographicLocationSessionsFunction> logger,
            IMaintenanceRepository maintenanceRepo,
            IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _maintenanceRepo = maintenanceRepo;
            _metricsRepo = metricsRepo;
        }

        [Function("GetGeographicLocationSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/geographic/sessions")] HttpRequestData req)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var tenantId = TenantHelper.GetTenantId(req);

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

                _logger.LogInformation("Fetching sessions for location '{LocationKey}' tenant {TenantId} ({Days}d, groupBy={GroupBy})",
                    locationKey, tenantId, days, groupBy);

                var cutoff = DateTime.UtcNow.AddDays(-days);
                var sessions = await _maintenanceRepo.GetSessionsByDateRangeAsync(cutoff, DateTime.UtcNow.AddDays(1), tenantId);
                var filtered = FilterSessionsByLocation(sessions, locationKey, groupBy);

                var appSummaries = await _metricsRepo.GetAppInstallSummariesByTenantAsync(tenantId);
                var rows = BuildRows(filtered, appSummaries);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { success = true, sessions = rows, totalCount = rows.Count });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching geographic location sessions");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }

        internal static List<SessionSummary> FilterSessionsByLocation(List<SessionSummary> sessions, string locationKey, string groupBy)
        {
            return sessions
                .Where(s => !string.IsNullOrEmpty(s.GeoCountry))
                .Where(s => GetGeographicMetricsFunction.GetLocationKey(s, groupBy) == locationKey)
                .OrderByDescending(s => s.StartedAt)
                .ToList();
        }

        internal static List<LocationSessionRow> BuildRows(
            List<SessionSummary> sessions, List<AppInstallSummary> appSummaries)
        {
            var appsBySession = appSummaries
                .GroupBy(a => a.SessionId)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var rows = new List<LocationSessionRow>(sessions.Count);
            foreach (var s in sessions)
            {
                var apps = appsBySession.TryGetValue(s.SessionId, out var list) ? list : new List<AppInstallSummary>();
                var agg = DoAggregator.Compute(apps);
                rows.Add(LocationSessionRow.From(s, agg, apps.Count));
            }
            return rows;
        }
    }
}
