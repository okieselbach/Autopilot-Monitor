using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Apps
{
    /// <summary>
    /// GET /api/apps/list?days=30
    /// Returns ALL apps observed for the tenant in the window (not just Top-10 like /metrics/app),
    /// with failure-rate trend computed as a half-window comparison.
    /// </summary>
    public class GetAppsListFunction
    {
        private readonly ILogger<GetAppsListFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;

        public GetAppsListFunction(ILogger<GetAppsListFunction> logger, IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
        }

        [Function("GetAppsList")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "apps/list")] HttpRequestData req)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var tenantId = TenantHelper.GetTenantId(req);

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                int days = 30;
                if (int.TryParse(query["days"], out var parsedDays) && parsedDays > 0 && parsedDays <= 365)
                    days = parsedDays;

                var now = DateTime.UtcNow;
                var cutoff = now.AddDays(-days);
                var midpoint = now.AddDays(-days / 2.0);

                var allSummaries = await _metricsRepo.GetAppInstallSummariesByTenantAsync(tenantId);
                var summaries = allSummaries.Where(s => s.StartedAt >= cutoff).ToList();

                var apps = summaries.GroupBy(s => s.AppName).Select(g =>
                {
                    var total = g.Count();
                    var succeeded = g.Count(s => s.Status == "Succeeded");
                    var failed = g.Count(s => s.Status == "Failed");
                    var completed = g.Where(s => s.Status == "Succeeded").ToList();
                    var failureRate = total > 0 ? Math.Round((double)failed / total * 100, 1) : 0;

                    // Trend: compare failure rate in first half vs second half of window.
                    // Only emit trend if BOTH halves have >= 5 installs (otherwise too noisy).
                    var firstHalf = g.Where(s => s.StartedAt < midpoint).ToList();
                    var secondHalf = g.Where(s => s.StartedAt >= midpoint).ToList();

                    double? trendDelta = null;
                    string trend = "stable";
                    if (firstHalf.Count >= 5 && secondHalf.Count >= 5)
                    {
                        var fhRate = (double)firstHalf.Count(s => s.Status == "Failed") / firstHalf.Count * 100;
                        var shRate = (double)secondHalf.Count(s => s.Status == "Failed") / secondHalf.Count * 100;
                        var delta = Math.Round(shRate - fhRate, 1);
                        trendDelta = delta;
                        if (delta < -1) trend = "improving";
                        else if (delta > 1) trend = "worsening";
                    }

                    return new
                    {
                        appName = g.Key,
                        appType = g.Select(s => s.AppType).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? string.Empty,
                        totalInstalls = total,
                        succeeded,
                        failed,
                        failureRate,
                        avgDurationSeconds = completed.Count > 0 ? Math.Round(completed.Average(s => s.DurationSeconds), 0) : 0,
                        maxDurationSeconds = completed.Count > 0 ? completed.Max(s => s.DurationSeconds) : 0,
                        avgDownloadBytes = completed.Count > 0 ? (long)completed.Average(s => s.DownloadBytes) : 0,
                        trend,
                        trendDelta,
                        lastSeenAt = g.Max(s => s.CompletedAt ?? s.StartedAt)
                    };
                })
                .OrderByDescending(a => a.failed)
                .ThenByDescending(a => a.failureRate)
                .ToList();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    totalApps = apps.Count,
                    totalInstalls = summaries.Count,
                    windowDays = days,
                    apps
                });
                return response;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unauthorized apps/list request");
                var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauth.WriteAsJsonAsync(new { success = false, message = "Unauthorized" });
                return unauth;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching apps list");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
