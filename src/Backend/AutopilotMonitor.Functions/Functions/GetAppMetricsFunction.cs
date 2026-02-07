using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    public class GetAppMetricsFunction
    {
        private readonly ILogger<GetAppMetricsFunction> _logger;
        private readonly TableStorageService _storageService;

        public GetAppMetricsFunction(ILogger<GetAppMetricsFunction> logger, TableStorageService storageService)
        {
            _logger = logger;
            _storageService = storageService;
        }

        [Function("GetAppMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "app-metrics")] HttpRequestData req)
        {
            try
            {
                // Validate authentication
                if (!TenantHelper.IsAuthenticated(req))
                {
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new { success = false, message = "Authentication required." });
                    return unauthorizedResponse;
                }

                var tenantId = TenantHelper.GetTenantId(req);
                _logger.LogInformation($"Fetching app metrics for tenant {tenantId}");

                var summaries = await _storageService.GetAppInstallSummariesByTenantAsync(tenantId);

                // Aggregate: slowest apps (avg duration) and top failing apps
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
                    .Where(a => a.avgDurationSeconds > 0)
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
                _logger.LogError(ex, "Error fetching app metrics");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
