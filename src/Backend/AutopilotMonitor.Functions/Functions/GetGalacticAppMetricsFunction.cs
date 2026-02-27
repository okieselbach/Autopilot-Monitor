using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    public class GetGalacticAppMetricsFunction
    {
        private readonly ILogger<GetGalacticAppMetricsFunction> _logger;
        private readonly TableStorageService _storageService;
        private readonly GalacticAdminService _galacticAdminService;

        public GetGalacticAppMetricsFunction(
            ILogger<GetGalacticAppMetricsFunction> logger,
            TableStorageService storageService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _storageService = storageService;
            _galacticAdminService = galacticAdminService;
        }

        [Function("GetGalacticAppMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/app-metrics")] HttpRequestData req)
        {
            try
            {
                if (!TenantHelper.IsAuthenticated(req))
                {
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new { success = false, message = "Authentication required." });
                    return unauthorizedResponse;
                }

                var userEmail = TenantHelper.GetUserIdentifier(req);
                var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userEmail);

                if (!isGalacticAdmin)
                {
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new { success = false, message = "Access denied. Galactic Admin role required." });
                    return forbiddenResponse;
                }

                _logger.LogInformation($"Fetching galactic app metrics (User: {userEmail})");

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var daysParam = query["days"];
                int days = 30;
                if (!string.IsNullOrEmpty(daysParam) && int.TryParse(daysParam, out var parsedDays) && parsedDays > 0)
                    days = parsedDays;

                var cutoff = DateTime.UtcNow.AddDays(-days);

                var allSummaries = await _storageService.GetAllAppInstallSummariesAsync();
                var summaries = allSummaries.Where(s => s.StartedAt >= cutoff).ToList();

                var appGroups = summaries.GroupBy(s => s.AppName).Select(g =>
                {
                    var completed = g.Where(s => s.Status == "Succeeded").ToList();
                    var failed = g.Where(s => s.Status == "Failed").ToList();
                    var total = g.Count();

                    return new
                    {
                        appName = g.Key,
                        totalInstalls = total,
                        succeeded = completed.Count,
                        failed = failed.Count,
                        failureRate = total > 0 ? Math.Round((double)failed.Count / total * 100, 1) : 0,
                        avgDurationSeconds = completed.Count > 0 ? Math.Round(completed.Average(s => s.DurationSeconds), 0) : 0,
                        maxDurationSeconds = completed.Count > 0 ? completed.Max(s => s.DurationSeconds) : 0,
                        avgDownloadBytes = completed.Count > 0 ? (long)completed.Average(s => s.DownloadBytes) : 0,
                        topFailureCodes = failed
                            .Where(f => !string.IsNullOrEmpty(f.FailureCode))
                            .GroupBy(f => f.FailureCode)
                            .OrderByDescending(fc => fc.Count())
                            .Take(3)
                            .Select(fc => new { code = fc.Key, count = fc.Count() })
                    };
                }).ToList();

                var slowestApps = appGroups
                    .Where(a => a.succeeded > 0)
                    .OrderByDescending(a => a.avgDurationSeconds)
                    .Take(10)
                    .ToList();

                var topFailingApps = appGroups
                    .Where(a => a.failed > 0)
                    .OrderByDescending(a => a.failed)
                    .ThenByDescending(a => a.failureRate)
                    .Take(10)
                    .ToList();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    totalApps = appGroups.Count,
                    totalInstalls = summaries.Count,
                    slowestApps,
                    topFailingApps
                });

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching galactic app metrics");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
