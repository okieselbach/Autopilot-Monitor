using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Raw
{
    public class QueryRawSessionsFunction
    {
        private readonly ILogger<QueryRawSessionsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public QueryRawSessionsFunction(ILogger<QueryRawSessionsFunction> logger, ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        /// <summary>
        /// GET /api/raw/sessions — Tenant-scoped raw session query
        /// </summary>
        [Function("QueryRawSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "raw/sessions")] HttpRequestData req)
        {
            try
            {
                var tenantId = TenantHelper.GetTenantId(req);
                return await QuerySessions(req, tenantId, scope: "raw-sessions:tenant",
                    basePath: "/api/raw/sessions", filterTenantId: null);
            }
            catch (UnauthorizedAccessException)
            {
                var err = req.CreateResponse(HttpStatusCode.Unauthorized);
                await err.WriteAsJsonAsync(new { error = "Unauthorized" });
                return err;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Query raw sessions");
            }
        }

        /// <summary>
        /// GET /api/global/raw/sessions — Cross-tenant raw session query (GlobalAdminOnly)
        /// </summary>
        [Function("QueryRawSessionsGlobal")]
        public async Task<HttpResponseData> RunGlobal(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/raw/sessions")] HttpRequestData req)
        {
            try
            {
                var filterTenantId = req.Query["tenantId"];
                var effectiveTenantId = string.IsNullOrEmpty(filterTenantId) ? null : filterTenantId;
                return await QuerySessions(req, effectiveTenantId, scope: "raw-sessions:global",
                    basePath: "/api/global/raw/sessions", filterTenantId: effectiveTenantId);
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Query global raw sessions");
            }
        }

        private async Task<HttpResponseData> QuerySessions(
            HttpRequestData req, string? tenantId, string scope, string basePath, string? filterTenantId)
        {
            var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);

            var status = query["status"];
            var startedAfter = query["startedAfter"];
            var startedBefore = query["startedBefore"];
            var serialNumber = query["serialNumber"];
            var fieldsParam = query["fields"];

            var pagination = SearchSessionsPagination.ParsePagination(query);
            if (pagination.Error != null)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { error = pagination.Error });
                return bad;
            }

            // Build search filter — Limit is ignored, pagination owns size.
            var filter = new SessionSearchFilter();
            if (!string.IsNullOrEmpty(status))
                filter.Status = status;
            if (!string.IsNullOrEmpty(startedAfter) && DateTime.TryParse(startedAfter, out var after))
                filter.StartedAfter = after;
            if (!string.IsNullOrEmpty(startedBefore) && DateTime.TryParse(startedBefore, out var before))
                filter.StartedBefore = before;
            if (!string.IsNullOrEmpty(serialNumber))
                filter.SerialNumber = serialNumber;

            var callerTenantId = TenantHelper.GetTenantId(req);

            string? azureToken = null;
            if (pagination.Continuation != null)
            {
                if (!SearchSessionsPagination.TryAcceptContinuation(
                        pagination.Continuation, scope, callerTenantId, filterTenantId, filter,
                        out azureToken, out var rejectReason))
                {
                    _logger.LogWarning("QueryRawSessions: continuation rejected ({Reason})", rejectReason);
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new
                    {
                        error = $"Invalid continuation token ({rejectReason}). Restart pagination from the first page.",
                    });
                    return bad;
                }
            }

            var page = await _sessionRepo.SearchSessionsPageAsync(tenantId, filter, pagination.PageSize, azureToken);

            // Optional field projection (raw-tool feature) is applied on top of the
            // paginated SessionSummary set; it doesn't affect cursor mechanics.
            object sessionsPayload;
            if (!string.IsNullOrEmpty(fieldsParam))
            {
                var fieldSet = new HashSet<string>(
                    fieldsParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);
                sessionsPayload = page.Items.Select(s => ProjectFields(s, fieldSet)).ToList();
            }
            else
            {
                sessionsPayload = page.Items;
            }

            string? nextLink = null;
            if (!string.IsNullOrEmpty(page.NextRawToken))
            {
                var fp = SearchSessionsPagination.Fingerprint(scope, callerTenantId, filterTenantId, filter);
                var wireToken = ContinuationToken.Encode(page.NextRawToken!, callerTenantId, fp);
                nextLink = SearchSessionsPagination.BuildNextLink(basePath, pagination.PageSize, wireToken, query);
            }

            return await req.OkAsync(new
            {
                tenantId,
                count = page.Items.Count,
                sessions = sessionsPayload,
                nextLink,
            });
        }

        private static Dictionary<string, object?> ProjectFields(SessionSummary session, HashSet<string> fields)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (fields.Contains("sessionId")) dict["sessionId"] = session.SessionId;
            if (fields.Contains("tenantId")) dict["tenantId"] = session.TenantId;
            if (fields.Contains("status")) dict["status"] = session.Status.ToString();
            if (fields.Contains("serialNumber")) dict["serialNumber"] = session.SerialNumber;
            if (fields.Contains("manufacturer")) dict["manufacturer"] = session.Manufacturer;
            if (fields.Contains("model")) dict["model"] = session.Model;
            if (fields.Contains("deviceName")) dict["deviceName"] = session.DeviceName;
            if (fields.Contains("osBuild")) dict["osBuild"] = session.OsBuild;
            if (fields.Contains("osName")) dict["osName"] = session.OsName;
            if (fields.Contains("startedAt")) dict["startedAt"] = session.StartedAt;
            if (fields.Contains("completedAt")) dict["completedAt"] = session.CompletedAt;
            if (fields.Contains("durationSeconds")) dict["durationSeconds"] = session.DurationSeconds;
            if (fields.Contains("currentPhase")) dict["currentPhase"] = session.CurrentPhase;
            if (fields.Contains("failureReason")) dict["failureReason"] = session.FailureReason;
            if (fields.Contains("eventCount")) dict["eventCount"] = session.EventCount;
            if (fields.Contains("enrollmentType")) dict["enrollmentType"] = session.EnrollmentType;
            if (fields.Contains("isPreProvisioned")) dict["isPreProvisioned"] = session.IsPreProvisioned;
            if (fields.Contains("isUserDriven")) dict["isUserDriven"] = session.IsUserDriven;
            if (fields.Contains("isHybridJoin")) dict["isHybridJoin"] = session.IsHybridJoin;
            if (fields.Contains("agentVersion")) dict["agentVersion"] = session.AgentVersion;
            if (fields.Contains("geoCountry")) dict["geoCountry"] = session.GeoCountry;

            // If no fields matched, return a sensible default subset rather than empty.
            if (dict.Count == 0)
            {
                dict["sessionId"] = session.SessionId;
                dict["tenantId"] = session.TenantId;
                dict["status"] = session.Status.ToString();
                dict["serialNumber"] = session.SerialNumber;
                dict["startedAt"] = session.StartedAt;
                dict["completedAt"] = session.CompletedAt;
            }

            return dict;
        }
    }
}
