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
    /// GET /api/apps/{appName}/analytics?days=30
    /// Returns per-app drill-down: time series, version breakdown, installer phase breakdown,
    /// top failure codes, device-model correlation, flakiness score, detection-lies count.
    /// </summary>
    public class GetAppAnalyticsFunction
    {
        private readonly ILogger<GetAppAnalyticsFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;
        private readonly ISessionRepository _sessionRepo;

        public GetAppAnalyticsFunction(
            ILogger<GetAppAnalyticsFunction> logger,
            IMetricsRepository metricsRepo,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
            _sessionRepo = sessionRepo;
        }

        [Function("GetAppAnalytics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "apps/{appName}/analytics")] HttpRequestData req,
            string appName)
        {
            try
            {
                var tenantId = TenantHelper.GetTenantId(req);

                // Route parameter is URL-decoded by the host, but double-decode is safe for names
                // with spaces/parentheses that may arrive double-encoded from some clients.
                var decodedAppName = Uri.UnescapeDataString(appName ?? string.Empty);
                if (string.IsNullOrWhiteSpace(decodedAppName))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = "appName is required" });
                    return bad;
                }

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                int days = 30;
                if (int.TryParse(query["days"], out var parsedDays) && parsedDays > 0 && parsedDays <= 365)
                    days = parsedDays;

                var now = DateTime.UtcNow;
                var cutoff = now.AddDays(-days);
                var midpoint = now.AddDays(-days / 2.0);

                var allSummaries = await _metricsRepo.GetAppInstallSummariesByTenantAsync(tenantId);
                var summaries = allSummaries
                    .Where(s => string.Equals(s.AppName, decodedAppName, StringComparison.OrdinalIgnoreCase)
                                && s.StartedAt >= cutoff)
                    .ToList();

                if (summaries.Count == 0)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.OK);
                    await notFound.WriteAsJsonAsync(new
                    {
                        success = true,
                        appName = decodedAppName,
                        appType = string.Empty,
                        windowDays = days,
                        bucket = "day",
                        summary = new { totalInstalls = 0, succeeded = 0, failed = 0, failureRate = 0, avgDurationSeconds = 0, p95DurationSeconds = 0, avgDownloadBytes = 0, trend = "stable", trendDelta = (double?)null, flakinessScore = 0.0 },
                        timeSeries = Array.Empty<object>(),
                        versionBreakdown = Array.Empty<object>(),
                        installerPhaseBreakdown = Array.Empty<object>(),
                        topFailureCodes = Array.Empty<object>(),
                        detectionLiesCount = 0,
                        deviceModelBreakdown = Array.Empty<object>()
                    });
                    return notFound;
                }

                var total = summaries.Count;
                var succeeded = summaries.Count(s => s.Status == "Succeeded");
                var failed = summaries.Count(s => s.Status == "Failed");
                var completed = summaries.Where(s => s.Status == "Succeeded").ToList();
                var failureRate = Math.Round((double)failed / total * 100, 1);
                var avgDurationSeconds = completed.Count > 0 ? Math.Round(completed.Average(s => s.DurationSeconds), 0) : 0;
                var p95DurationSeconds = Percentile(completed.Select(s => s.DurationSeconds).ToList(), 0.95);
                var avgDownloadBytes = completed.Count > 0 ? (long)completed.Average(s => s.DownloadBytes) : 0;

                // Trend (same rule as list endpoint)
                var firstHalf = summaries.Where(s => s.StartedAt < midpoint).ToList();
                var secondHalf = summaries.Where(s => s.StartedAt >= midpoint).ToList();
                double? trendDelta = null;
                string trend = "stable";
                if (firstHalf.Count >= 5 && secondHalf.Count >= 5)
                {
                    var fhRate = (double)firstHalf.Count(s => s.Status == "Failed") / firstHalf.Count * 100;
                    var shRate = (double)secondHalf.Count(s => s.Status == "Failed") / secondHalf.Count * 100;
                    trendDelta = Math.Round(shRate - fhRate, 1);
                    if (trendDelta < -1) trend = "improving";
                    else if (trendDelta > 1) trend = "worsening";
                }

                // Flakiness: share of installs with AttemptNumber > 1
                var flakinessScore = total > 0
                    ? Math.Round((double)summaries.Count(s => s.AttemptNumber > 1) / total, 3)
                    : 0.0;

                // Time series: auto bucket — daily for <=30d, weekly for >30d
                var bucket = days <= 30 ? "day" : "week";
                var timeSeries = BuildTimeSeries(summaries, cutoff, now, bucket);

                // Version breakdown
                var versionBreakdown = summaries
                    .Where(s => !string.IsNullOrEmpty(s.AppVersion))
                    .GroupBy(s => s.AppVersion)
                    .Select(g =>
                    {
                        var vTotal = g.Count();
                        var vFailed = g.Count(s => s.Status == "Failed");
                        return new
                        {
                            appVersion = g.Key,
                            installs = vTotal,
                            failed = vFailed,
                            failureRate = vTotal > 0 ? Math.Round((double)vFailed / vTotal * 100, 1) : 0
                        };
                    })
                    .OrderByDescending(v => v.installs)
                    .ToList();

                // Installer phase breakdown (only failures, where a phase was recorded)
                var installerPhaseBreakdown = summaries
                    .Where(s => s.Status == "Failed" && !string.IsNullOrEmpty(s.InstallerPhase))
                    .GroupBy(s => s.InstallerPhase)
                    .Select(g => new { phase = g.Key, failed = g.Count() })
                    .OrderByDescending(p => p.failed)
                    .ToList();

                // Top failure codes
                var topFailureCodes = summaries
                    .Where(s => s.Status == "Failed" && !string.IsNullOrEmpty(s.FailureCode))
                    .GroupBy(s => s.FailureCode)
                    .Select(g => new
                    {
                        code = g.Key,
                        exitCode = g.Select(s => s.ExitCode).FirstOrDefault(e => e.HasValue),
                        count = g.Count(),
                        sampleMessage = g.Select(s => s.FailureMessage).FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? string.Empty
                    })
                    .OrderByDescending(f => f.count)
                    .Take(5)
                    .ToList();

                // Detection lies: Status=Succeeded but DetectionResult=NotDetected
                var detectionLiesCount = summaries.Count(s =>
                    s.Status == "Succeeded" &&
                    string.Equals(s.DetectionResult, "NotDetected", StringComparison.OrdinalIgnoreCase));

                // Device-model correlation: join to sessions, group by (manufacturer, model).
                // Fetch sessions only once per sessionId.
                var distinctSessionIds = summaries.Select(s => s.SessionId).Distinct().ToList();
                var sessionLookup = new Dictionary<string, SessionSummary>();
                foreach (var sid in distinctSessionIds)
                {
                    if (string.IsNullOrEmpty(sid)) continue;
                    var sess = await _sessionRepo.GetSessionAsync(tenantId, sid);
                    if (sess != null) sessionLookup[sid] = sess;
                }

                var deviceModelBreakdown = summaries
                    .Where(s => sessionLookup.ContainsKey(s.SessionId))
                    .Select(s => new
                    {
                        Summary = s,
                        Manufacturer = sessionLookup[s.SessionId].Manufacturer ?? "Unknown",
                        Model = sessionLookup[s.SessionId].Model ?? "Unknown"
                    })
                    .GroupBy(x => new { x.Manufacturer, x.Model })
                    .Where(g => g.Count() >= 5)
                    .Select(g =>
                    {
                        var modelTotal = g.Count();
                        var modelFailed = g.Count(x => x.Summary.Status == "Failed");
                        var modelFailureRate = Math.Round((double)modelFailed / modelTotal * 100, 1);
                        var lift = failureRate > 0
                            ? Math.Round(modelFailureRate / failureRate, 2)
                            : 0;
                        return new
                        {
                            manufacturer = g.Key.Manufacturer,
                            model = g.Key.Model,
                            installs = modelTotal,
                            failed = modelFailed,
                            failureRate = modelFailureRate,
                            liftVsBaseline = lift
                        };
                    })
                    .OrderByDescending(m => m.liftVsBaseline)
                    .ToList();

                var appType = summaries.Select(s => s.AppType).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? string.Empty;

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    appName = decodedAppName,
                    appType,
                    windowDays = days,
                    bucket,
                    summary = new
                    {
                        totalInstalls = total,
                        succeeded,
                        failed,
                        failureRate,
                        avgDurationSeconds,
                        p95DurationSeconds,
                        avgDownloadBytes,
                        trend,
                        trendDelta,
                        flakinessScore
                    },
                    timeSeries,
                    versionBreakdown,
                    installerPhaseBreakdown,
                    topFailureCodes,
                    detectionLiesCount,
                    deviceModelBreakdown
                });
                return response;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unauthorized apps/analytics request");
                var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauth.WriteAsJsonAsync(new { success = false, message = "Unauthorized" });
                return unauth;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching app analytics");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// Groups summaries into day/week buckets and fills empty buckets with zeros
        /// so the frontend chart shows a continuous line.
        /// </summary>
        private static List<object> BuildTimeSeries(List<AppInstallSummary> summaries, DateTime cutoff, DateTime now, string bucket)
        {
            // Align cutoff to bucket start
            var start = bucket == "week" ? StartOfWeek(cutoff) : cutoff.Date;
            var end = now.Date;

            var bucketed = new Dictionary<DateTime, List<AppInstallSummary>>();
            var cursor = start;
            while (cursor <= end)
            {
                bucketed[cursor] = new List<AppInstallSummary>();
                cursor = bucket == "week" ? cursor.AddDays(7) : cursor.AddDays(1);
            }

            foreach (var s in summaries)
            {
                var key = bucket == "week" ? StartOfWeek(s.StartedAt) : s.StartedAt.Date;
                if (bucketed.ContainsKey(key))
                    bucketed[key].Add(s);
            }

            return bucketed
                .OrderBy(kv => kv.Key)
                .Select(kv =>
                {
                    var items = kv.Value;
                    var bTotal = items.Count;
                    var bFailed = items.Count(s => s.Status == "Failed");
                    var bSucceeded = items.Count(s => s.Status == "Succeeded");
                    var bCompleted = items.Where(s => s.Status == "Succeeded").ToList();
                    return (object)new
                    {
                        bucketStart = DateTime.SpecifyKind(kv.Key, DateTimeKind.Utc),
                        installs = bTotal,
                        succeeded = bSucceeded,
                        failed = bFailed,
                        failureRate = bTotal > 0 ? Math.Round((double)bFailed / bTotal * 100, 1) : 0,
                        avgDurationSeconds = bCompleted.Count > 0 ? Math.Round(bCompleted.Average(s => s.DurationSeconds), 0) : 0
                    };
                })
                .ToList();
        }

        /// <summary>ISO week start: Monday 00:00 UTC.</summary>
        private static DateTime StartOfWeek(DateTime dt)
        {
            var date = dt.Date;
            int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.AddDays(-diff);
        }

        /// <summary>Simple percentile (nearest-rank). Returns 0 for empty input.</summary>
        private static int Percentile(List<int> values, double percentile)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            var rank = (int)Math.Ceiling(percentile * sorted.Count) - 1;
            if (rank < 0) rank = 0;
            if (rank >= sorted.Count) rank = sorted.Count - 1;
            return sorted[rank];
        }
    }
}
