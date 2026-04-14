using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    public class GetGlobalGeographicMetricsFunction
    {
        private readonly ILogger<GetGlobalGeographicMetricsFunction> _logger;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly IMetricsRepository _metricsRepo;

        public GetGlobalGeographicMetricsFunction(
            ILogger<GetGlobalGeographicMetricsFunction> logger,
            IMaintenanceRepository maintenanceRepo,
            IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _maintenanceRepo = maintenanceRepo;
            _metricsRepo = metricsRepo;
        }

        [Function("GetGlobalGeographicMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/geographic")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userEmail = TenantHelper.GetUserIdentifier(req);

                _logger.LogInformation("Fetching global geographic metrics (User: {UserEmail})", userEmail);

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var daysParam = query["days"];
                int days = 30;
                if (!string.IsNullOrEmpty(daysParam) && int.TryParse(daysParam, out var parsedDays) && parsedDays > 0)
                    days = parsedDays;

                var groupBy = query["groupBy"] ?? "city";
                var tenantIdFilter = query["tenantId"];

                var cutoff = DateTime.UtcNow.AddDays(-days);
                var sessions = !string.IsNullOrWhiteSpace(tenantIdFilter)
                    ? await _maintenanceRepo.GetSessionsByDateRangeAsync(cutoff, DateTime.UtcNow.AddDays(1), tenantIdFilter)
                    : await _maintenanceRepo.GetSessionsByDateRangeAsync(cutoff, DateTime.UtcNow.AddDays(1));
                var allSummaries = !string.IsNullOrWhiteSpace(tenantIdFilter)
                    ? await _metricsRepo.GetAppInstallSummariesByTenantAsync(tenantIdFilter)
                    : await _metricsRepo.GetAllAppInstallSummariesAsync();
                var summaries = allSummaries.Where(s => s.StartedAt >= cutoff).ToList();

                var result = GetGeographicMetricsFunction.ComputeGeographicMetrics(sessions, summaries, groupBy);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(result);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching global geographic metrics");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
