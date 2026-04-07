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
    /// GET /api/apps/{appName}/sessions?days=30&status=failed&model=...&version=...&offset=0&limit=50
    /// Returns paginated list of sessions that installed (or tried to install) a given app.
    /// Kept separate from /analytics so the analytics payload stays small.
    /// </summary>
    public class GetAppSessionsFunction
    {
        private const int DefaultLimit = 50;
        private const int MaxLimit = 200;

        private readonly ILogger<GetAppSessionsFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;
        private readonly ISessionRepository _sessionRepo;

        public GetAppSessionsFunction(
            ILogger<GetAppSessionsFunction> logger,
            IMetricsRepository metricsRepo,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
            _sessionRepo = sessionRepo;
        }

        [Function("GetAppSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "apps/{appName}/sessions")] HttpRequestData req,
            string appName)
        {
            try
            {
                var tenantId = TenantHelper.GetTenantId(req);

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

                var statusFilter = (query["status"] ?? "all").Trim().ToLowerInvariant();
                var modelFilter = query["model"];
                var versionFilter = query["version"];

                int offset = 0;
                if (int.TryParse(query["offset"], out var parsedOffset) && parsedOffset >= 0)
                    offset = parsedOffset;

                int limit = DefaultLimit;
                if (int.TryParse(query["limit"], out var parsedLimit) && parsedLimit > 0)
                    limit = Math.Min(parsedLimit, MaxLimit);

                var cutoff = DateTime.UtcNow.AddDays(-days);

                var allSummaries = await _metricsRepo.GetAppInstallSummariesByTenantAsync(tenantId);
                var summaries = allSummaries
                    .Where(s => string.Equals(s.AppName, decodedAppName, StringComparison.OrdinalIgnoreCase)
                                && s.StartedAt >= cutoff)
                    .ToList();

                // Apply status filter
                if (statusFilter == "failed")
                    summaries = summaries.Where(s => s.Status == "Failed").ToList();
                else if (statusFilter == "succeeded")
                    summaries = summaries.Where(s => s.Status == "Succeeded").ToList();

                // Apply version filter
                if (!string.IsNullOrWhiteSpace(versionFilter))
                    summaries = summaries
                        .Where(s => string.Equals(s.AppVersion, versionFilter, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                // Join sessions for device info
                var distinctSessionIds = summaries.Select(s => s.SessionId).Distinct().ToList();
                var sessionLookup = new Dictionary<string, SessionSummary>();
                foreach (var sid in distinctSessionIds)
                {
                    if (string.IsNullOrEmpty(sid)) continue;
                    var sess = await _sessionRepo.GetSessionAsync(tenantId, sid);
                    if (sess != null) sessionLookup[sid] = sess;
                }

                // Apply model filter after join
                if (!string.IsNullOrWhiteSpace(modelFilter))
                {
                    summaries = summaries
                        .Where(s => sessionLookup.TryGetValue(s.SessionId, out var sess)
                                    && string.Equals(sess.Model, modelFilter, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                // Sort: Failed first, then most recent first
                var ordered = summaries
                    .OrderBy(s => s.Status == "Failed" ? 0 : s.Status == "InProgress" ? 1 : 2)
                    .ThenByDescending(s => s.StartedAt)
                    .ToList();

                var total = ordered.Count;
                var page = ordered.Skip(offset).Take(limit).ToList();

                var items = page.Select(s =>
                {
                    sessionLookup.TryGetValue(s.SessionId, out var sess);
                    return new
                    {
                        sessionId = s.SessionId,
                        deviceName = sess?.DeviceName ?? string.Empty,
                        manufacturer = sess?.Manufacturer ?? string.Empty,
                        model = sess?.Model ?? string.Empty,
                        appVersion = s.AppVersion,
                        status = s.Status,
                        installerPhase = s.InstallerPhase,
                        failureCode = s.FailureCode,
                        exitCode = s.ExitCode,
                        attemptNumber = s.AttemptNumber,
                        startedAt = s.StartedAt,
                        durationSeconds = s.DurationSeconds
                    };
                }).ToList();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    total,
                    offset,
                    limit,
                    items
                });
                return response;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unauthorized apps/sessions request");
                var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauth.WriteAsJsonAsync(new { success = false, message = "Unauthorized" });
                return unauth;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching app sessions");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
